using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GodjiVpn.ViewModels;
using Hardcodet.Wpf.TaskbarNotification;

namespace GodjiVpn.Services;

/// <summary>Иконка в трее со статусом — Hardcodet.NotifyIcon.Wpf вместо
/// System.Windows.Forms.NotifyIcon: не тянет UseWindowsForms=true в csproj, который
/// конфликтует с System.Windows.Application/UserControl (см. комментарий в csproj из первого
/// прохода). Аналог кастомного двухстрочного уведомления Android (notification_status.xml) —
/// здесь это просто текст тултипа "страна/статус", т.к. Windows не ограничивает layout трея
/// так же, как Android RemoteViews.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly VpnEngine _vpnEngine;
    private readonly MenuItem _toggleItem;

    public bool IsExiting { get; private set; }

    public TrayIconService(VpnEngine vpnEngine, ConnectViewModel connect, Window window, Action exit)
    {
        _vpnEngine = vpnEngine;

        _icon = new TaskbarIcon
        {
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/Images/logo.png")),
            ToolTipText = "Godji VPN — отключено"
        };

        var openItem = new MenuItem { Header = "Открыть" };
        openItem.Click += (_, _) => ShowWindow(window);

        _toggleItem = new MenuItem { Header = "Подключить" };
        _toggleItem.Click += async (_, _) =>
        {
            if (connect.ToggleCommand.CanExecute(null)) await connect.ToggleCommand.ExecuteAsync(null);
        };

        var exitItem = new MenuItem { Header = "Выход" };
        exitItem.Click += (_, _) => { IsExiting = true; exit(); };

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);
        _icon.ContextMenu = menu;

        _icon.TrayMouseDoubleClick += (_, _) => ShowWindow(window);

        _vpnEngine.PropertyChanged += (_, _) => UpdateStatus();
        UpdateStatus();
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void UpdateStatus()
    {
        // VpnEngine поднимает PropertyChanged из фонового потока (ConnectAsync/DisconnectAsync
        // идут через ConfigureAwait(false)) — трей-иконка и её меню, как любые DependencyObject,
        // требуют UI-потока.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) { dispatcher.BeginInvoke(UpdateStatus); return; }

        _icon.ToolTipText = _vpnEngine.IsRunning ? "Godji VPN — подключено"
            : _vpnEngine.IsConnecting ? "Godji VPN — подключение…"
            : "Godji VPN — отключено";
        _toggleItem.Header = _vpnEngine.IsRunning ? "Отключить" : "Подключить";
    }

    /// <summary>Аналог NotificationCompat-уведомлений Android (см. SubscriptionNotifier) —
    /// здесь просто balloon tip на самой трей-иконке, простейший системный эквивалент toast
    /// на Windows без отдельной инфраструктуры Action Center/COM-регистрации.</summary>
    public void ShowBalloon(string title, string text)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => ShowBalloon(title, text)); return; }
        _icon.ShowBalloonTip(title, text, BalloonIcon.Info);
    }

    public void Dispose() => _icon.Dispose();
}
