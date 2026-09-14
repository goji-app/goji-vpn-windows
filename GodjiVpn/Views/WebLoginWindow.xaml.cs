using System.Windows;
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
    }

    /// <summary>Кука выставляется сайтом сразу после успешного входа (любым способом — Google/
    /// Яндекс/Telegram/email, что выберет сам пользователь на открывшейся странице), не привязана
    /// к конкретному переходу — поэтому проверяем после КАЖДОЙ навигации, а не только один раз.</summary>
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
