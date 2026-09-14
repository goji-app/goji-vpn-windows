using System.Windows;
using System.Windows.Threading;
using GodjiVpn.Services;

namespace GodjiVpn.Views;

/// <summary>
/// Модальное окно веб-входа — см. комментарий в XAML для полного контекста (обход сломанного
/// api/auth/native/exchange, портировано с Android WebLoginActivity.kt). Показывается через
/// ShowDialog(); после успешного логина на сайте SessionToken заполнен и DialogResult == true.
/// </summary>
public partial class WebLoginWindow : Window
{
    private const string SiteUrl = "https://gojihub.xyz/";
    private const string SessionCookieName = "rw_session_token";
    private const string RefreshCookieName = "rw_refresh_token";

    private bool _handled;
    private DispatcherTimer? _cookiePollTimer;

    public string? SessionToken { get; private set; }

    /// <summary>Живёт намного дольше сессионного JWT — без неё ApiClient.RefreshSessionAsync
    /// не смог бы продлевать сессию раз в сутки. Может быть null, если сайт её не выставил.</summary>
    public string? RefreshToken { get; private set; }

    public WebLoginWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var env = await WebView2EnvironmentProvider.GetAsync();
            await Web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            App.LogUnhandledException(ex);
            MessageBox.Show(this, "Не удалось открыть страницу входа — проверьте, установлен ли WebView2 Runtime.",
                "Godji VPN", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
            return;
        }

        Web.CoreWebView2.NavigationStarting += (_, _) => Progress.Visibility = Visibility.Visible;
        Web.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            Progress.Visibility = Visibility.Collapsed;
            await CheckForSessionCookieAsync();
        };
        Web.CoreWebView2.Navigate(SiteUrl);

        // Сайт — SPA: после входа (email/Google/Яндекс/Telegram) кука обычно выставляется
        // JS-кодом по факту успешного запроса, без полной навигации страницы (history.pushState/
        // обновление состояния внутри той же страницы) — NavigationCompleted в таком случае
        // просто не срабатывает повторно, и проверка выше никогда не узнаёт об успешном входе
        // (реальная жалоба пользователя: "авторизуется на самом сайте, а в приложение не
        // переходит"). Поэтому вдобавок опрашиваем куку по таймеру, пока окно открыто — не
        // самое элегантное решение, но надёжно работает независимо от того, как именно сайт
        // сигнализирует об входе на своей стороне.
        _cookiePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _cookiePollTimer.Tick += async (_, _) => await CheckForSessionCookieAsync();
        _cookiePollTimer.Start();
        Closed += (_, _) => _cookiePollTimer?.Stop();
    }

    /// <summary>Кука выставляется сайтом сразу после успешного входа (любым способом — Google/
    /// Яндекс/Telegram/email, что выберет сам пользователь на открывшейся странице), не привязана
    /// к конкретному переходу — поэтому проверяем и после каждой навигации, и по таймеру (см.
    /// InitializeAsync) на случай, если сайт вообще не делает полную навигацию после входа.</summary>
    private async Task CheckForSessionCookieAsync()
    {
        if (_handled) return;
        var cookies = await Web.CoreWebView2.CookieManager.GetCookiesAsync(SiteUrl);
        var cookie = cookies.FirstOrDefault(c => c.Name == SessionCookieName);
        if (cookie == null || string.IsNullOrWhiteSpace(cookie.Value)) return;

        _handled = true;
        SessionToken = cookie.Value;
        RefreshToken = cookies.FirstOrDefault(c => c.Name == RefreshCookieName)?.Value;
        DialogResult = true;
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
