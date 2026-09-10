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

        var active = subsResponse.Subscriptions.FirstOrDefault(s => s.IsPrimary)
            ?? subsResponse.Subscriptions.FirstOrDefault();
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
