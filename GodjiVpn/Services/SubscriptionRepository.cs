using System.ComponentModel;
using GodjiVpn.Models;

namespace GodjiVpn.Services;

/// <summary>
/// Держит текущую подписку и список узлов в памяти, отдаёт их и Connect-, и Servers-экрану —
/// прямой аналог SubscriptionRepository.kt (Android): один источник правды, обновляемый через
/// Refresh(), вместо того чтобы каждый экран тянул gojihub.xyz/subs.gojihub.xyz заново.
/// </summary>
public sealed class SubscriptionRepository : INotifyPropertyChanged
{
    private readonly ApiClient _api;
    private readonly SubscriptionService _subscriptionService;
    private readonly CustomNodeStore _customNodes;

    public event PropertyChangedEventHandler? PropertyChanged;

    private Models.SubscriptionInfo? _subscription;
    public Models.SubscriptionInfo? Subscription
    {
        get => _subscription;
        private set { _subscription = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subscription))); }
    }

    private IReadOnlyList<VlessNode> _subscriptionNodes = Array.Empty<VlessNode>();

    /// <summary>Узлы подписки + добавленные вручную по JSON-профилю (см. CustomNodeStore) —
    /// единый список для Connect/Servers/Plans, ничего в потребителях менять не пришлось: они
    /// как читали Nodes/SelectedNode отсюда, так и продолжают, просто набор пополнился.</summary>
    public IReadOnlyList<VlessNode> Nodes => _subscriptionNodes.Concat(_customNodes.Nodes).ToList();

    private string? _selectedId;
    public string? SelectedId
    {
        get => _selectedId;
        set { _selectedId = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedId))); }
    }

    /// <summary>Причина, по которой RefreshAsync() в последний раз вернул false — раньше все
    /// сбои (сеть, 401/403/500, пустой список подписок) молча проглатывались, и пользователь
    /// видел просто пустой экран без единого объяснения, почему "не подтянуло подписку".</summary>
    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set { _lastError = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LastError))); }
    }

    /// <summary>Новости/рассылки (см. ApiClient.GetBroadcastsAsync) — та же страница, что
    /// "Мои рассылки" веб-версии. Не персистится на диск: список короткий, лишний раз сходить
    /// в сеть при следующем запуске не накладно, а устаревшие новости в офлайн-кэше приносили
    /// бы больше путаницы, чем пользы.</summary>
    private IReadOnlyList<BroadcastDto> _broadcasts = Array.Empty<BroadcastDto>();
    public IReadOnlyList<BroadcastDto> Broadcasts
    {
        get => _broadcasts;
        private set { _broadcasts = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Broadcasts))); }
    }

    public SubscriptionRepository(ApiClient api, SubscriptionService subscriptionService, CustomNodeStore customNodes)
    {
        _api = api;
        _subscriptionService = subscriptionService;
        _customNodes = customNodes;
        _customNodes.Changed += () => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Nodes)));
    }

    public VlessNode? SelectedNode => Nodes.FirstOrDefault(n => n.Id == SelectedId);

    /// <returns>true, если подписку удалось реально получить с бэкенда.</returns>
    public async Task<bool> RefreshAsync()
    {
        Models.SubscriptionsResponse subsResponse;
        try
        {
            subsResponse = await _api.GetSubscriptionsAsync();
        }
        catch (Exception ex)
        {
            LastError = "Не удалось получить подписку: " + ex.Message;
            return false;
        }

        // Не завязано на наличие активной подписки — новости могут быть релевантны и до
        // покупки тарифа. Отдельная от подписки/серверов ошибка не должна прерывать остальной
        // RefreshAsync, поэтому просто отбрасывается.
        try { Broadcasts = await _api.GetBroadcastsAsync(); }
        catch { /* новости не критичны для основного функционала */ }

        var active = subsResponse.Subscriptions.FirstOrDefault(s => s.IsPrimary)
            ?? subsResponse.Subscriptions.FirstOrDefault();

        // С бэкенда 7.1.0 traffic не приходит в списке (см. SubscriptionInfo.Traffic) — если
        // его нет, дозапрашиваем одиночным эндпоинтом. Сбой дозапроса не должен откатывать уже
        // полученную active — просто трафик останется пустым до следующего обновления.
        if (active != null && active.Traffic == null)
        {
            try { active = await _api.GetSubscriptionAsync(active.Id); }
            catch { /* не критично — попробуем на следующем RefreshAsync */ }
        }

        Subscription = active;
        if (active == null)
        {
            LastError = subsResponse.Subscriptions.Count == 0
                ? "На аккаунте нет активной подписки — тариф ещё не оформлен"
                : "Не удалось определить активную подписку";
            return false;
        }

        var (nodes, nodesError) = await SafeFetchNodesAsync(active.SubscriptionLink);
        // Пустой список = сбой получения (см. SafeFetchNodesAsync), а не "в подписке теперь
        // ноль узлов" — не затираем прежний непустой список при временном сбое сети, иначе
        // пользователь на вкладке "Серверы" внезапно теряет весь список, хотя туннель (если
        // уже поднят) продолжает работать как ни в чём не бывало.
        if (nodes.Count > 0)
        {
            LastError = null;
            _subscriptionNodes = nodes;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Nodes)));
            if (SelectedId == null || Nodes.All(n => n.Id != SelectedId))
                SelectedId = nodes[0].Id;
        }
        else
        {
            // device_limit тарифа — из того же /api/subscriptions, что мы уже получили выше,
            // добавляем к тексту ошибки HWID-гейта, чтобы не лезть отдельно на вкладку
            // "Подписка" ради одной цифры, когда ошибка именно про лимит устройств.
            var suffix = nodesError?.Contains("HWID", StringComparison.OrdinalIgnoreCase) == true
                ? $" (лимит устройств по тарифу «{active.PlanName}»: {active.DeviceLimit})"
                : "";
            LastError = (nodesError ?? "Список серверов пуст") + suffix;
        }
        return true;
    }

    public void Select(string id) => SelectedId = id;

    private async Task<(List<VlessNode> Nodes, string? Error)> SafeFetchNodesAsync(string subscriptionLink)
    {
        try { return await _subscriptionService.FetchNodesAsync(subscriptionLink); }
        catch (Exception ex) { return (new List<VlessNode>(), "Не удалось получить список серверов: " + ex.Message); }
    }
}
