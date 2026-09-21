using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GodjiVpn.Services;
using GodjiVpn.Utils;
using GodjiVpn.Views;

namespace GodjiVpn.ViewModels;

public sealed partial class LogFileItem : ObservableObject
{
    public required string Label { get; init; }
    public required string FileName { get; init; }
    [ObservableProperty] private bool isSelected;
}

public sealed partial class PingMethodItem : ObservableObject
{
    public required string Label { get; init; }
    public required PingMethod Method { get; init; }
    [ObservableProperty] private bool isSelected;
}

public sealed partial class ThemeModeItem : ObservableObject
{
    public required string Label { get; init; }
    public required ThemeMode Mode { get; init; }
    [ObservableProperty] private bool isSelected;
}

/// <summary>Аналог SettingsScreen.kt — версия/HWID (About), просмотр логов вместо отдельного
/// LogViewerDialog.kt (здесь один экран проще нескольких диалогов на маленьком приложении),
/// тёмная тема, выход из аккаунта. Переключение языка не перенесено — языковой слой (i18n) в
/// Windows-версии пока не заведён вообще, добавлять его только ради пары чипов в Settings — по
/// объёму отдельная большая задача, не часть этого прохода.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly TokenStore _tokenStore;
    private readonly VpnEngine _vpnEngine;
    private readonly HwidProvider _hwid;
    private readonly PingSettings _pingSettings;
    private readonly ThemeService _theme;
    private readonly UpdateService _updateService;
    private readonly ApiClient _api;

    private static string LogsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GodjiVpn", "logs");

    [ObservableProperty] private string appVersion = "—";
    [ObservableProperty] private string hwid = "—";
    [ObservableProperty] private LogFileItem selectedLogFile;
    [ObservableProperty] private string logContent = "";
    [ObservableProperty] private string pingTestUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdate))]
    private UpdateInfo? availableUpdate;
    [ObservableProperty] private FlowDocument updateChangelogDocument = new();
    [ObservableProperty] private bool isCheckingUpdate;
    [ObservableProperty] private bool isDownloadingUpdate;
    [ObservableProperty] private double downloadProgress;
    [ObservableProperty] private string? updateCheckMessage;

    public bool HasAvailableUpdate => AvailableUpdate != null;

    public ObservableCollection<LogFileItem> LogFiles { get; } = new()
    {
        new LogFileItem { Label = "Приложение", FileName = "engine.log" },
        new LogFileItem { Label = "VPN-ядро (xray)", FileName = "xray.log" },
        new LogFileItem { Label = "VPN-туннель (sing-box)", FileName = "sing-box.log" },
        new LogFileItem { Label = "Сбои", FileName = "crash.log" },
    };

    /// <summary>Аналог PingSettingsScreen.kt — способ проверки серверов на вкладке "Серверы"
    /// (см. PingService/PingSettings).</summary>
    public ObservableCollection<PingMethodItem> PingMethods { get; } = new()
    {
        new PingMethodItem { Label = "Через прокси · GET", Method = PingMethod.ProxyGet },
        new PingMethodItem { Label = "Через прокси · HEAD", Method = PingMethod.ProxyHead },
        new PingMethodItem { Label = "TCP", Method = PingMethod.Tcp },
        new PingMethodItem { Label = "ICMP", Method = PingMethod.Icmp },
    };

    /// <summary>Аналог ThemeMode (Android) — "Системная" следует теме Windows живьём, пока
    /// приложение открыто (см. ThemeService.OnUserPreferenceChanged), а не только в момент
    /// выбора.</summary>
    public ObservableCollection<ThemeModeItem> ThemeModes { get; } = new()
    {
        new ThemeModeItem { Label = "Светлая", Mode = ThemeMode.Light },
        new ThemeModeItem { Label = "Тёмная", Mode = ThemeMode.Dark },
        new ThemeModeItem { Label = "Системная", Mode = ThemeMode.System },
    };

    public event Action? RequestLogout;

    public SettingsViewModel(TokenStore tokenStore, VpnEngine vpnEngine, HwidProvider hwid, PingSettings pingSettings,
        ThemeService theme, UpdateService updateService, ApiClient api)
    {
        _tokenStore = tokenStore;
        _vpnEngine = vpnEngine;
        _hwid = hwid;
        _pingSettings = pingSettings;
        _theme = theme;
        _updateService = updateService;
        _api = api;
        selectedLogFile = LogFiles[0];
        selectedLogFile.IsSelected = true;
        foreach (var m in PingMethods) m.IsSelected = m.Method == _pingSettings.Method;
        pingTestUrl = _pingSettings.TestUrl;
        foreach (var m in ThemeModes) m.IsSelected = m.Mode == _theme.Mode;
    }

    [RelayCommand]
    private void SelectThemeMode(ThemeModeItem item)
    {
        foreach (var m in ThemeModes) m.IsSelected = m == item;
        _theme.SetMode(item.Mode);
    }

    public void Load()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersion = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "—";
        Hwid = _hwid.Get();
        RefreshLog();
    }

    [RelayCommand]
    private void SelectPingMethod(PingMethodItem item)
    {
        foreach (var m in PingMethods) m.IsSelected = m == item;
        _pingSettings.Method = item.Method;
    }

    partial void OnPingTestUrlChanged(string value) => _pingSettings.TestUrl = value;

    [RelayCommand]
    private void SelectLogFile(LogFileItem item)
    {
        SelectedLogFile = item;
        foreach (var f in LogFiles) f.IsSelected = f == item;
        RefreshLog();
    }

    [RelayCommand]
    private void RefreshLog()
    {
        var path = Path.Combine(LogsDir, SelectedLogFile.FileName);
        try
        {
            if (!File.Exists(path)) { LogContent = "(пока пусто — журнал появится после первого запуска этого компонента)"; return; }
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            const int maxChars = 20000;
            var length = (int)Math.Min(stream.Length, maxChars);
            stream.Seek(-length, SeekOrigin.End);
            using var reader = new StreamReader(stream);
            LogContent = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            LogContent = $"Не удалось прочитать лог: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CopyLog() => Clipboard.SetText(LogContent);

    [RelayCommand]
    private void CopyHwid() => Clipboard.SetText(Hwid);

    [RelayCommand]
    private void OpenLogsFolder()
    {
        Directory.CreateDirectory(LogsDir);
        ShellLauncher.OpenFolder(LogsDir);
    }

    /// <summary>Новая секция "Поддержка" — тот же нативный чат, что и на вкладке "Подписка"
    /// (см. PlansViewModel.OpenSupport). Порт из Android (720f5ff): там тоже добавлена
    /// отдельная точка входа в SettingsScreen.</summary>
    [RelayCommand]
    private void OpenSupport() => new SupportWindow(_api) { Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (_vpnEngine.IsRunning) await _vpnEngine.DisconnectAsync();
        _tokenStore.Clear();
        RequestLogout?.Invoke();
    }

    /// <summary>Вызывается из App.xaml.cs после фоновой проверки при старте — чтобы не делать
    /// два одинаковых сетевых запроса подряд (фоновый + ручной при первом открытии Настроек),
    /// просто подхватываем уже готовый результат, если он есть.</summary>
    public void ApplyBackgroundUpdateCheck(UpdateInfo? update)
    {
        AvailableUpdate = update;
        UpdateChangelogDocument = update != null ? RichContent.Build(update.Changelog) : new FlowDocument();
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        IsCheckingUpdate = true;
        UpdateCheckMessage = null;
        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            ApplyBackgroundUpdateCheck(update);
            UpdateCheckMessage = update == null ? $"У вас последняя версия ({AppVersion})" : null;
        }
        catch
        {
            UpdateCheckMessage = "Не удалось проверить обновления — проверьте подключение к интернету";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (AvailableUpdate is not { } update) return;
        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        try
        {
            var progress = new Progress<double>(p => DownloadProgress = p);
            var installerPath = await _updateService.DownloadAsync(update, progress);
            UpdateService.RunInstallerAndExit(installerPath);
        }
        catch
        {
            UpdateCheckMessage = "Не удалось скачать обновление — попробуйте ещё раз позже";
            IsDownloadingUpdate = false;
        }
    }
}
