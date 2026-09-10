using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodjiVpn.Models;
using GodjiVpn.Services;
using GodjiVpn.Utils;

namespace GodjiVpn.ViewModels;

public sealed partial class NodeItem : ObservableObject
{
    public required VlessNode Node { get; init; }
    /// <summary>-2 = ещё не проверяли ("проверить"), -1 = недоступен, иначе — мс.</summary>
    [ObservableProperty] private int pingMs = -2;
    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isChecking;

    /// <summary>Добавлен вручную по JSON-профилю (см. CustomNodeStore), а не пришёл с
    /// подписки — только у таких узлов показываем кнопку удаления.</summary>
    public bool IsCustom => Node.Id.StartsWith("custom-", StringComparison.Ordinal);

    public CountryGeo? Geo => CountryGeoLookup.Find(Node.Name);
    public string DisplayName => RemarkText.StripLeadingFlag(Node.Name);
    public string Flag => Geo != null ? CountryGeoLookup.FlagEmoji(Geo.Code) : "🌐";
    public string? FlagImagePath => FlagIcon.ImagePath(Geo?.Code);
}

/// <summary>Список узлов подписки + пинг через реальный прокси-туннель — аналог
/// PingRepository.kt (Android): временный xray.exe с профилем узла, HTTP-запрос к
/// generate_204 через получившийся SOCKS5. Меряет настоящую задержку с учётом оверхеда
/// VLESS+Reality, а не просто TCP-рукопожатие до порта сервера (см. PingService.cs).</summary>
public sealed partial class ServersViewModel : ObservableObject
{
    private readonly SubscriptionRepository _subscription;
    private readonly PingService _pingService;
    private readonly CustomNodeStore _customNodes;

    public ObservableCollection<NodeItem> Nodes { get; } = new();

    [ObservableProperty] private bool isCheckingAll;
    [ObservableProperty] private bool isRefreshing;

    /// <summary>Баннер-результат ручного обновления (AnimatedContent в Android) — появляется
    /// один раз после нажатия "Обновить", не показывается на автообновлении при открытии
    /// вкладки.</summary>
    [ObservableProperty] private string? refreshResultMessage;
    [ObservableProperty] private bool refreshResultIsError;

    public ServersViewModel(SubscriptionRepository subscription, PingService pingService, CustomNodeStore customNodes)
    {
        _subscription = subscription;
        _pingService = pingService;
        _customNodes = customNodes;
        _subscription.PropertyChanged += (_, _) => RunOnUiThread(SyncFromRepository);
        SyncFromRepository();
    }

    [RelayCommand]
    private void RemoveCustomNode(NodeItem item)
    {
        _customNodes.Remove(item.Node.Id);
        SyncFromRepository();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        var ok = await _subscription.RefreshAsync();
        IsRefreshing = false;
        // RefreshAsync внутри меняет Subscription/Nodes, что уже придёт через PropertyChanged,
        // но подписка регистрируется до первого узла — на всякий случай синхронизируем и тут.
        SyncFromRepository();

        RefreshResultIsError = !ok || _subscription.LastError != null;
        RefreshResultMessage = RefreshResultIsError
            ? _subscription.LastError ?? "Не удалось обновить список серверов"
            : "Список серверов обновлён";
    }

    [RelayCommand]
    private void DismissRefreshResult() => RefreshResultMessage = null;

    private void SyncFromRepository()
    {
        var selectedId = _subscription.SelectedId;
        var existingById = Nodes.ToDictionary(n => n.Node.Id);
        Nodes.Clear();
        foreach (var node in _subscription.Nodes)
        {
            var item = existingById.TryGetValue(node.Id, out var previous)
                ? new NodeItem { Node = node, IsSelected = node.Id == selectedId, PingMs = previous.PingMs }
                : new NodeItem { Node = node, IsSelected = node.Id == selectedId };
            Nodes.Add(item);
        }
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    [RelayCommand]
    private void Select(NodeItem item)
    {
        foreach (var n in Nodes) n.IsSelected = n == item;
        _subscription.Select(item.Node.Id);
    }

    [RelayCommand]
    private async Task PingOneAsync(NodeItem item)
    {
        item.IsChecking = true;
        item.PingMs = await _pingService.MeasureAsync(item.Node);
        item.IsChecking = false;
    }

    [RelayCommand]
    private async Task PingAllAsync()
    {
        IsCheckingAll = true;
        var tasks = Nodes.Select(async item =>
        {
            item.IsChecking = true;
            item.PingMs = await _pingService.MeasureAsync(item.Node);
            item.IsChecking = false;
        });
        await Task.WhenAll(tasks);
        IsCheckingAll = false;
    }
}
