using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodjiVpn.Services;
using GodjiVpn.Utils;

namespace GodjiVpn.ViewModels;

/// <summary>Аналог ConnectViewModel.kt/ConnectScreen.kt (Android) — заголовок/статус-строка,
/// приветствие поверх глобуса, карточка текущего узла, скорость/трафик. NetworkPill
/// (Wi-Fi/Мобильная/Глушение) и авто-переключение на резервный узел сознательно не
/// переносятся — специфично для мобильной сети с SIM, на десктопе такого сценария нет
/// (решение принято с пользователем).</summary>
public sealed partial class ConnectViewModel : ObservableObject, IDisposable
{
    private readonly VpnEngine _vpnEngine;
    private readonly SubscriptionRepository _subscription;
    private readonly DispatcherTimer _speedTimer;

    private long _lastRx;
    private long _lastTx;
    private DateTime _lastSampleUtc;

    // Скользящее окно последних замеров для мини-графика в карточках скорости — секунды
    // текущей сессии, не переживает пересоздание ViewModel, что тут и не нужно. Порт из Android
    // (ConnectViewModel.downHistory/upHistory).
    private const int SpeedHistorySize = 30;
    private readonly List<double> _downHistory = new();
    private readonly List<double> _upHistory = new();

    [ObservableProperty] private PointCollection downLinePoints = new();
    [ObservableProperty] private PointCollection downFillPoints = new();
    [ObservableProperty] private PointCollection upLinePoints = new();
    [ObservableProperty] private PointCollection upFillPoints = new();

    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isConnecting;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private string headline = "Пока без защиты";
    [ObservableProperty] private string subline = "Провайдер видит всё, что ты открываешь";

    [ObservableProperty] private string currentNodeName = "Выбрать за меня";
    [ObservableProperty] private string currentNodeMeta = "";
    [ObservableProperty] private string currentFlag = "🌐";
    [ObservableProperty] private string? currentFlagImagePath;

    [ObservableProperty] private string greetingHi = "Hello";
    [ObservableProperty] private string greetingSub = "«хелло» · английский";

    [ObservableProperty] private string globeStatus = "off";
    [ObservableProperty] private CountryGeo? globeGeo;

    [ObservableProperty] private string planName = "—";
    [ObservableProperty] private string expiryLabel = "—";
    [ObservableProperty] private int daysLeft;
    [ObservableProperty] private double usedGb;
    [ObservableProperty] private double quotaGb;
    [ObservableProperty] private bool isUnlimited;

    [ObservableProperty] private double downSpeedMbps;
    [ObservableProperty] private double upSpeedMbps;
    [ObservableProperty] private string connectedTimeLabel = "00:00:00";

    public event Action? NavigateToPlansRequested;

    [RelayCommand]
    private void OpenTraffic() => NavigateToPlansRequested?.Invoke();

    public ConnectViewModel(VpnEngine vpnEngine, SubscriptionRepository subscription)
    {
        _vpnEngine = vpnEngine;
        _subscription = subscription;

        _vpnEngine.PropertyChanged += OnVpnEnginePropertyChanged;
        _subscription.PropertyChanged += OnSubscriptionPropertyChanged;

        _speedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _speedTimer.Tick += (_, _) => SampleSpeed();

        RefreshFromState();
    }

    public async Task LoadAsync()
    {
        await _subscription.RefreshAsync();
        RefreshFromState();
    }

    private bool CanToggle => !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanToggle))]
    private async Task ToggleAsync()
    {
        ErrorMessage = null;
        if (IsConnected)
        {
            await _vpnEngine.DisconnectAsync();
            return;
        }

        var node = _subscription.SelectedNode;
        if (node == null)
        {
            ErrorMessage = "Нет доступного сервера — выберите его на вкладке «Серверы»";
            return;
        }
        await _vpnEngine.ConnectAsync(node);
    }

    private void OnVpnEnginePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RunOnUiThread(RefreshFromState);

    private void OnSubscriptionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        RunOnUiThread(RefreshFromState);

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private void RefreshFromState()
    {
        IsConnected = _vpnEngine.IsRunning;
        IsConnecting = _vpnEngine.IsConnecting;
        ErrorMessage = _vpnEngine.LastError ?? _subscription.LastError;
        ToggleCommand.NotifyCanExecuteChanged();

        var node = _subscription.SelectedNode;
        var geo = node != null ? CountryGeoLookup.Find(node.Name) : null;
        GlobeGeo = geo;

        CurrentNodeName = node != null ? RemarkText.StripLeadingFlag(node.Name) : "Выбрать за меня";
        CurrentNodeMeta = geo != null ? $"{geo.City}, {geo.RuName}" : "";
        CurrentFlag = geo != null ? CountryGeoLookup.FlagEmoji(geo.Code) : "🌐";
        CurrentFlagImagePath = FlagIcon.ImagePath(geo?.Code);

        var greeting = Greetings.ForLang(geo?.Lang);
        GreetingHi = greeting.Hi;
        GreetingSub = (greeting.Transliteration.Length > 0 ? $"«{greeting.Transliteration}» · " : "") + greeting.Language;

        GlobeStatus = IsConnected ? "on" : IsConnecting ? "connecting" : "off";

        var ruPrep = geo?.RuPrep ?? "надёжном месте";
        var nodeLabel = node != null ? RemarkText.StripLeadingFlag(node.Name) : "сервером";
        Headline = IsConnected ? $"Ты в {ruPrep}" : IsConnecting ? "Ищем дорогу…" : "Пока без защиты";
        Subline = IsConnected ? $"{nodeLabel} · в туннеле"
            : IsConnecting ? $"Договариваемся с {nodeLabel}"
            : "Провайдер видит всё, что ты открываешь";

        var sub = _subscription.Subscription;
        PlanName = sub?.PlanName ?? "—";
        ExpiryLabel = sub?.ExpireAt is { } iso ? DateFormat.FormatDate(iso) : "—";
        DaysLeft = sub?.DaysLeft ?? 0;
        UsedGb = (sub?.Traffic?.UsedBytes ?? 0) / 1_000_000_000.0;
        QuotaGb = (sub?.Traffic?.LimitBytes ?? 0) / 1_000_000_000.0;
        IsUnlimited = sub?.Traffic?.IsUnlimited ?? false;

        if (IsConnected)
        {
            var counters = _vpnEngine.ReadAdapterCounters();
            _lastRx = counters?.RxBytes ?? 0;
            _lastTx = counters?.TxBytes ?? 0;
            _lastSampleUtc = DateTime.UtcNow;
            if (!_speedTimer.IsEnabled) _speedTimer.Start();
        }
        else
        {
            _speedTimer.Stop();
            DownSpeedMbps = 0;
            UpSpeedMbps = 0;
            ConnectedTimeLabel = "00:00:00";
            _downHistory.Clear();
            _upHistory.Clear();
            DownLinePoints = new(); DownFillPoints = new();
            UpLinePoints = new(); UpFillPoints = new();
        }
    }

    private void SampleSpeed()
    {
        var counters = _vpnEngine.ReadAdapterCounters();
        var now = DateTime.UtcNow;
        if (counters != null)
        {
            var dt = Math.Max((now - _lastSampleUtc).TotalSeconds, 0.001);
            DownSpeedMbps = Math.Max(counters.Value.RxBytes - _lastRx, 0) / dt / 1_000_000.0;
            UpSpeedMbps = Math.Max(counters.Value.TxBytes - _lastTx, 0) / dt / 1_000_000.0;
            _lastRx = counters.Value.RxBytes;
            _lastTx = counters.Value.TxBytes;

            AppendHistory(_downHistory, DownSpeedMbps);
            AppendHistory(_upHistory, UpSpeedMbps);
            DownLinePoints = BuildLinePoints(_downHistory);
            DownFillPoints = BuildFillPoints(DownLinePoints);
            UpLinePoints = BuildLinePoints(_upHistory);
            UpFillPoints = BuildFillPoints(UpLinePoints);
        }
        _lastSampleUtc = now;

        var since = _vpnEngine.ConnectedSinceUtc;
        if (since != null)
        {
            var elapsed = now - since.Value;
            ConnectedTimeLabel = $"{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}";
        }
    }

    private static void AppendHistory(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > SpeedHistorySize) history.RemoveAt(0);
    }

    /// <summary>Нормализует в единичный квадрат [0,1]×[0,1] (x — позиция по времени, y — 1 у
    /// нуля/дно, 0 у максимума за окно/верх) вместо пиксельных координат — ConnectView.xaml
    /// растягивает через Viewbox Stretch="Fill" на актуальный размер карточки, ViewModel не
    /// должен знать её реальные пиксели.</summary>
    private static PointCollection BuildLinePoints(IReadOnlyList<double> history)
    {
        var points = new PointCollection();
        if (history.Count < 2) return points;
        var max = Math.Max(history.Max(), 0.01);
        for (var i = 0; i < history.Count; i++)
            points.Add(new Point((double)i / (history.Count - 1), 1 - history[i] / max));
        return points;
    }

    /// <summary>Та же линия + замыкание вниз по обоим краям — заливка области под графиком.</summary>
    private static PointCollection BuildFillPoints(PointCollection linePoints)
    {
        if (linePoints.Count == 0) return new PointCollection();
        var fill = new PointCollection(linePoints) { new Point(1, 1), new Point(0, 1) };
        return fill;
    }

    public void Dispose()
    {
        _speedTimer.Stop();
        _vpnEngine.PropertyChanged -= OnVpnEnginePropertyChanged;
        _subscription.PropertyChanged -= OnSubscriptionPropertyChanged;
    }
}
