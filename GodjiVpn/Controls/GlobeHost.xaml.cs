using System.IO;
using System.Windows.Controls;
using GodjiVpn.Services;
using Microsoft.Web.WebView2.Core;

namespace GodjiVpn.Controls;

/// <summary>
/// Обёртка над WebView2, хостит оригинальный веб-компонент &lt;goji-globe&gt; (three.js),
/// скопированный из godji-android/goji-globe.js — тот же файл, с которого был сделан
/// Android-порт на OpenGL, только теперь используется как есть, без переписывания на
/// Direct3D. Статус/выбранный узел прокидываются в JS через ExecuteScriptAsync.
///
/// Виртуальный хост (SetVirtualHostNameToFolderMapping), а не file:// — ES-модули и
/// fetch() внутри goji-globe.js (three.js/world-atlas грузятся с CDN) не отрабатывают
/// корректно под file:// (CORS/opaque origin); подтверждено вручную через тестовый
/// http.server до интеграции — под http(s) всё работает как надо.
/// </summary>
public partial class GlobeHost : UserControl
{
    private const string VirtualHost = "godji.local";
    private bool _ready;
    private readonly Queue<Func<Task>> _pending = new();

    public GlobeHost()
    {
        InitializeComponent();
        Loaded += async (_, _) => await InitializeAsync();
        // MainWindow меняет CurrentViewModel между LoginViewModel/ShellViewModel через implicit
        // DataTemplate (см. App.xaml) — при каждом входе/выходе из аккаунта WPF полностью
        // пересобирает визуальное дерево, а значит и этот GlobeHost со своим WebView2 создаётся
        // заново. Без явного Dispose() старый CoreWebView2Environment/браузерный процесс не
        // освобождается сам по себе (WebView2 не диспозится автоматически на Unloaded) — при
        // нескольких логинах/логаутах за сессию это утечка процессов/памяти.
        Unloaded += (_, _) =>
        {
            if (ThemeService.Current != null) ThemeService.Current.Changed -= OnThemeChanged;
            Web.Dispose();
        };
    }

    private async Task InitializeAsync()
    {
        if (_ready) return;

        try
        {
            // Общий Environment на процесс (см. WebView2EnvironmentProvider) — та же папка
            // данных, которую использует и WebLoginWindow, вместо создания второй независимой
            // копии по умолчанию рядом с exe (там нет прав на запись в Program Files).
            var env = await WebView2EnvironmentProvider.GetAsync();
            await Web.EnsureCoreWebView2Async(env);

            var globeDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Globe");
            Web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, globeDir, CoreWebView2HostResourceAccessKind.Allow);

            Web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                _ready = true;
                await SetThemeAsync(ThemeService.Current?.IsDark ?? false);
                while (_pending.Count > 0) await _pending.Dequeue()();
            };
            Web.CoreWebView2.Navigate($"https://{VirtualHost}/globe.html");

            // Живое переключение темы, пока экран с глобусом уже открыт (не только на старте) —
            // тот же статический синглтон-паттерн, что и VpnEngine.Current.
            if (ThemeService.Current != null) ThemeService.Current.Changed += OnThemeChanged;
        }
        catch (Exception ex)
        {
            // Глобус — декоративный элемент, а не критичная для VPN функция. Если на машине
            // нет установленного WebView2 Runtime (не на всех Windows 10 он есть из коробки),
            // он заблокирован групповой политикой, или что-то ещё пошло не так — приложение
            // должно остаться полностью рабочим, просто без глобуса, а не падать целиком (это
            // было бы особенно обидно, учитывая, что GlobeHost встроен и в LoginView, то есть
            // сработало бы на самом первом экране, который видит пользователь).
            App.LogUnhandledException(ex);
        }
    }

    private async void OnThemeChanged() => await SetThemeAsync(ThemeService.Current?.IsDark ?? false);

    public Task SetThemeAsync(bool isDark) =>
        RunAsync($"window.godjiSetTheme && window.godjiSetTheme('{(isDark ? "dark" : "light")}')");

    public Task SetStatusAsync(string status) =>
        RunAsync($"window.godjiSetStatus && window.godjiSetStatus('{Escape(status)}')");

    public Task SetNodeAsync(double lat, double lon, string country, string city, string title) =>
        RunAsync($"window.godjiSetNode && window.godjiSetNode({lat.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                  $"{lon.ToString(System.Globalization.CultureInfo.InvariantCulture)}, " +
                  $"'{Escape(country)}', '{Escape(city)}', '{Escape(title)}')");

    private Task RunAsync(string script)
    {
        if (_ready) return Web.CoreWebView2.ExecuteScriptAsync(script);
        _pending.Enqueue(() => Web.CoreWebView2.ExecuteScriptAsync(script));
        return Task.CompletedTask;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");
}
