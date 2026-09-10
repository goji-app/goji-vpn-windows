using System.IO;
using System.Windows;
using System.Windows.Threading;
using GodjiVpn.Services;
using GodjiVpn.ViewModels;

namespace GodjiVpn;

/// <summary>
/// Composition root. Приложение маленькое — контейнер DI избыточен, сервисы и вьюмодели
/// собираются вручную здесь, один раз при старте.
/// </summary>
public partial class App : Application
{
    private SingleInstanceService? _singleInstance;
    private LoginViewModel? _loginViewModel;
    private SubscriptionRepository? _subscriptionRepository;
    private DispatcherTimer? _hourlyRefreshTimer;
    public TrayIconService? Tray { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Раньше нигде в проекте не было глобального перехватчика — любое необработанное
        // исключение в async void-методе или обработчике события (клик по кнопке, Loaded
        // WPF-контрола и т.п.) валило весь процесс мгновенно, без лога и без сообщения
        // пользователю (реальный риск: сбой инициализации WebView2 в GlobeHost — нет
        // установленного рантайма, заблокирован групповой политикой и т.п. — крашил бы всё
        // приложение целиком просто из-за того, что на экране должен был быть глобус).
        // Логируем и показываем понятное сообщение вместо тихого исчезновения окна.
        DispatcherUnhandledException += (_, args) =>
        {
            LogUnhandledException(args.Exception);
            MessageBox.Show(
                "Произошла непредвиденная ошибка:\n\n" + args.Exception.Message +
                "\n\nПодробности сохранены в журнал (Настройки → Журнал). Приложение продолжит работу.",
                "Godji VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogUnhandledException(args.Exception);
            args.SetObserved();
        };

        var activationUrl = e.Args.FirstOrDefault(a => a.StartsWith("godjivpn://", StringComparison.OrdinalIgnoreCase));

        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.TryAcquire())
        {
            // Уже есть работающий экземпляр (мы запущены заново кликом по godjivpn://
            // после возврата из браузера с OAuth-логином) — передаём ему URL и молча
            // завершаемся, не показывая собственного окна.
            SingleInstanceService.ForwardToRunningInstanceAndExit(activationUrl);
            Shutdown();
            return;
        }

        // Применяем сохранённую тему ДО создания окна — иначе был бы виден краткий "мигок"
        // светлой темой перед подменой на тёмную сразу вслед.
        var themeService = new ThemeService();
        themeService.Initialize();

        var tokenStore = new TokenStore();
        var hwidProvider = new HwidProvider();
        var apiClient = new ApiClient(tokenStore);
        var subscriptionService = new SubscriptionService(hwidProvider);
        var customNodeStore = new CustomNodeStore();
        var subscriptionRepository = new SubscriptionRepository(apiClient, subscriptionService, customNodeStore);
        _subscriptionRepository = subscriptionRepository;
        var subscriptionNotifier = new SubscriptionNotifier();
        var broadcastNotifier = new BroadcastNotifier();
        var vpnEngine = new VpnEngine(); // конструктор сам регистрирует себя в VpnEngine.Current

        var loginViewModel = new LoginViewModel(apiClient, tokenStore);
        _loginViewModel = loginViewModel;
        var connectViewModel = new ConnectViewModel(vpnEngine, subscriptionRepository);
        var pingSettings = new PingSettings();
        var pingService = new PingService(pingSettings);
        var serversViewModel = new ServersViewModel(subscriptionRepository, pingService, customNodeStore);
        var plansViewModel = new PlansViewModel(apiClient, subscriptionRepository);
        var settingsViewModel = new SettingsViewModel(tokenStore, vpnEngine, hwidProvider, pingSettings, themeService);
        var shellViewModel = new ShellViewModel(connectViewModel, serversViewModel, plansViewModel, settingsViewModel);
        var mainViewModel = new MainViewModel(tokenStore, loginViewModel, shellViewModel);

        var window = new MainWindow { DataContext = mainViewModel };
        MainWindow = window;

        Tray = new TrayIconService(vpnEngine, connectViewModel, window, ExitApplication);
        subscriptionNotifier.NotificationRequested += (title, text) => Tray.ShowBalloon(title, text);
        broadcastNotifier.NotificationRequested += (title, text) => Tray.ShowBalloon(title, text);
        subscriptionRepository.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SubscriptionRepository.Subscription) && subscriptionRepository.Subscription is { } sub)
                subscriptionNotifier.Check(sub);
            if (args.PropertyName == nameof(SubscriptionRepository.Broadcasts))
                broadcastNotifier.Check(subscriptionRepository.Broadcasts);
        };
        window.Closing += (_, args) =>
        {
            if (Tray.IsExiting) return;
            // Как и у любого VPN-клиента — крестик сворачивает в трей, а не завершает
            // процесс (иначе поднятый туннель пришлось бы либо обрывать вместе с окном,
            // либо он остался бы висеть без какого-либо UI вообще).
            args.Cancel = true;
            window.Hide();
        };

        window.Show();

        try { OAuthProtocolRegistrar.EnsureRegistered(); }
        catch { /* реестр недоступен (групповые политики и т.п.) — OAuth-вход просто не заработает, email+OTP не затронут */ }

        _singleInstance.ActivationRequested += url => Dispatcher.Invoke(() => HandleActivationUrl(url, window));
        if (activationUrl != null) HandleActivationUrl(activationUrl, window);

        _hourlyRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _hourlyRefreshTimer.Tick += async (_, _) => await subscriptionRepository.RefreshAsync();
        _hourlyRefreshTimer.Start();

        await mainViewModel.InitializeAsync();
    }

    /// <summary>godjivpn://oauth2redirect?code=...&amp;provider=... — тот же формат, что
    /// разбирает OAuthCallbackActivity в Android.</summary>
    private void HandleActivationUrl(string url, MainWindow window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        var query = ParseQuery(uri.Query);
        query.TryGetValue("code", out var code);
        query.TryGetValue("provider", out var provider);
        _ = _loginViewModel?.CompleteOAuthAsync(provider, code);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var value = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
            result[key] = value;
        }
        return result;
    }

    /// <summary>Единственный путь закрыть процесс целиком (крестик на окне сворачивает в
    /// трей, см. window.Closing выше) — если не разорвать туннель здесь, xray.exe/sing-box.exe
    /// (обычные Process.Start, не привязанные к жизни родителя Job-объектом) остаются висеть
    /// сами по себе вместе с системным default route на TUN-адаптер, а у пользователя больше
    /// нет ни окна, ни трея, чтобы отключиться — только Task Manager или перезагрузка.</summary>
    internal static void LogUnhandledException(Exception ex)
    {
        try
        {
            var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GodjiVpn", "logs");
            Directory.CreateDirectory(logsDir);
            File.AppendAllText(Path.Combine(logsDir, "crash.log"),
                $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} =====\n{ex}\n\n");
        }
        catch { /* если даже лог не записать — уже ничего не поделать, не даём этому уронить обработчик */ }
    }

    private async void ExitApplication()
    {
        _hourlyRefreshTimer?.Stop();
        try { await (VpnEngine.Current?.DisconnectAsync() ?? Task.CompletedTask); }
        catch { /* уходим в любом случае — не даём сбою разрыва тоннеля помешать закрытию */ }
        Tray?.Dispose();
        Shutdown();
    }
}
