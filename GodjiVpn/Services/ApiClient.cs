using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using GodjiVpn.Models;

namespace GodjiVpn.Services;

/// <summary>
/// Клиент реального бэкенда gojihub.xyz — эндпоинты и заголовки сверены напрямую с рабочим
/// Android-кодом (RemnawaveApi.kt/NetworkModule.kt в каноническом источнике), не догадка.
/// host.gojihub.xyz (админка Remnawave) сюда НИКОГДА не подставлять — см. память
/// project-remnawave-backend-architecture.
/// </summary>
public sealed class ApiClient
{
    private const string BaseUrl = "https://gojihub.xyz/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TokenStore _tokenStore;

    public ApiClient(TokenStore tokenStore)
    {
        _tokenStore = tokenStore;
        var handler = new SocketsHttpHandler
        {
            // Пока туннель поднят — гоняем и собственные запросы приложения через локальный
            // SOCKS самого Xray (тот же приём, что tunnelAwareProxySelector() в Android):
            // иначе они шли бы мимо VPN напрямую, что для просмотра тарифов/подписки не
            // критично, но ломает единообразие с тем, что видит сам пользователь через тоннель.
            Proxy = new TunnelAwareProxy(),
            UseProxy = true
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        // Дефолтный User-Agent у HttpClient пустой (System.Net.Http без явного значения ничего
        // не шлёт) — заменяем на честный "имя приложения/версия (ОС)", как у обычного
        // десктоп-клиента, а не как у SubscriptionService (там User-Agent намеренно подделан
        // под v2rayNG — это отдельный, узкий случай именно для эндпоинта subs.gojihub.xyz,
        // не трогаем его здесь).
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GodjiVPN-Windows", versionText));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue($"({RuntimeInformation.OSDescription})"));
    }

    public async Task<SendOtpResponse> SendOtpAsync(string email, CancellationToken ct = default) =>
        await PostAsync<SendOtpRequest, SendOtpResponse>("api/auth/email/send-otp", new SendOtpRequest { Email = email }, ct).ConfigureAwait(false);

    public async Task<VerifyOtpResponse> VerifyOtpAsync(string email, string code, CancellationToken ct = default) =>
        await PostAsync<VerifyOtpRequest, VerifyOtpResponse>("api/auth/email/verify-otp", new VerifyOtpRequest { Email = email, Code = code }, ct).ConfigureAwait(false);

    public async Task<MeResponse> GetMeAsync(CancellationToken ct = default) =>
        await GetAsync<MeResponse>("api/auth/me", ct).ConfigureAwait(false);

    /// <summary>provider: "google" | "yandex" | "telegram-oidc" (последний на бэкенде сломан,
    /// см. LoginViewModel — кнопка Telegram остаётся выключенной, как и в Android). Параметр
    /// называется именно app_redirect (сверено с Android-кодом — с redirect_uri сервер не
    /// редиректил обратно в приложение).</summary>
    public async Task<StartAuthResponse> StartOAuthAsync(string provider, string appRedirect, string codeChallenge, CancellationToken ct = default) =>
        await GetAsync<StartAuthResponse>(
            $"api/auth/{provider}/start?app_redirect={Uri.EscapeDataString(appRedirect)}&code_challenge={Uri.EscapeDataString(codeChallenge)}&code_challenge_method=S256",
            ct).ConfigureAwait(false);

    public async Task<NativeExchangeResponse> ExchangeNativeOAuthAsync(string code, string codeVerifier, string provider, CancellationToken ct = default) =>
        await PostAsync<NativeExchangeRequest, NativeExchangeResponse>("api/auth/native/exchange",
            new NativeExchangeRequest { Code = code, CodeVerifier = codeVerifier, Provider = provider }, ct).ConfigureAwait(false);

    public async Task ConsentAsync(CancellationToken ct = default) =>
        await PostAsync<ConsentRequest, object?>("api/auth/consent", new ConsentRequest(), ct).ConfigureAwait(false);

    public async Task<SubscriptionsResponse> GetSubscriptionsAsync(CancellationToken ct = default) =>
        await GetAsync<SubscriptionsResponse>("api/subscriptions", ct).ConfigureAwait(false);

    public async Task<PlansResponse> GetPlansAsync(CancellationToken ct = default) =>
        await GetAsync<PlansResponse>("api/dashboard/plans", ct).ConfigureAwait(false);

    private async Task<TResponse> GetAsync<TResponse>(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        PrepareRequest(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadOrThrowAsync<TResponse>(response, ct).ConfigureAwait(false);
    }

    private async Task<TResponse> PostAsync<TBody, TResponse>(string path, TBody body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        PrepareRequest(request);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadOrThrowAsync<TResponse>(response, ct).ConfigureAwait(false);
    }

    /// <summary>Аналог provideAuthInterceptor() в NetworkModule.kt — там оба заголовка
    /// (Authorization и X-Requested-With) добавляются БЕЗУСЛОВНО на каждый запрос через общий
    /// OkHttp-interceptor, а не только на POST. Раньше X-Requested-With стоял только в
    /// PostAsync — из-за этого GET api/auth/{provider}/start уходил без него, бэкенд не
    /// сохранял app_redirect для сессии, и после логина в Google/Яндекс браузер просто
    /// показывал обычный сайт (gojihub.xyz/#/stats/overview) вместо редиректа в приложение.</summary>
    private void PrepareRequest(HttpRequestMessage request)
    {
        var token = _tokenStore.AccessToken;
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
    }

    private static async Task<TResponse> ReadOrThrowAsync<TResponse>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(response.StatusCode, body);
        if (typeof(TResponse) == typeof(object))
            return default!;
        return JsonSerializer.Deserialize<TResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Пустой ответ сервера");
    }

    /// <summary>Прокси по 127.0.0.1:SocksPort, пока VpnEngine.Current.IsRunning — решение
    /// принимается заново на каждый запрос (а не фиксируется при создании HttpClient),
    /// поэтому первый вызов до подключения и все последующие после — ведут себя корректно.</summary>
    private sealed class TunnelAwareProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }

        public Uri? GetProxy(Uri destination) =>
            VpnEngine.Current?.IsRunning == true
                ? new Uri($"socks5://127.0.0.1:{VpnEngine.SocksPort}")
                : null;

        public bool IsBypassed(Uri host) => VpnEngine.Current?.IsRunning != true;
    }
}

public sealed class ApiException(HttpStatusCode statusCode, string body)
    : Exception($"HTTP {(int)statusCode}: {body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}
