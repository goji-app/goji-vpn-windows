using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using GodjiVpn.Models;

namespace GodjiVpn.Services;

/// <summary>
/// Тянет subscription_link (subs.gojihub.xyz/&lt;token&gt;) и парсит реальный формат ответа —
/// JSON-массив готовых Xray-профилей — сверено с рабочим Android-кодом
/// (SubscriptionRepository.kt в каноническом источнике). Без правильных заголовков
/// (в первую очередь User-Agent) бэкенд отдаёт легаси base64-формат вместо JSON; мы, как и
/// Android-клиент, всегда просим JSON.
/// </summary>
public sealed class SubscriptionService
{
    private readonly HttpClient _http;
    private readonly HwidProvider _hwid;

    public SubscriptionService(HwidProvider hwid)
    {
        _hwid = hwid;
        var handler = new SocketsHttpHandler
        {
            Proxy = new TunnelAwareProxy(),
            UseProxy = true
        };
        _http = new HttpClient(handler);
    }

    /// <returns>Diagnostic не null, если ответ пришёл, но это не настоящий список серверов —
    /// например HWID-гейт (см. память project-subscription-hwid-gate: "Приложение не
    /// поддерживается" / "Превышение максимальное кол-во подключенных устройств" — сервер
    /// прячет такие ошибки внутрь fake-профиля с человекочитаемым remarks вместо HTTP-ошибки),
    /// легаси base64-формат или просто неожиданный ответ.</returns>
    public async Task<(List<VlessNode> Nodes, string? Diagnostic)> FetchNodesAsync(string subscriptionUrl, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, subscriptionUrl);
        // User-Agent намеренно подделан под известный клиент — это единственное, на что
        // бэкенд смотрит, чтобы решить, отдавать ли настоящий JSON-формат вместо base64
        // (см. комментарий в SubscriptionRepository.kt: замена на что-то своё возвращала бы
        // старый формат). X-HWID — гейт устройства по лимиту тарифа (см. память
        // project-subscription-hwid-gate — на триале лимит 1, не жечь слот тестовыми HWID).
        request.Headers.TryAddWithoutValidation("User-Agent", "v2rayNG/1.8.29");
        request.Headers.TryAddWithoutValidation("X-HWID", _hwid.Get());
        request.Headers.TryAddWithoutValidation("X-Device-OS", "Windows");
        request.Headers.TryAddWithoutValidation("X-Device-OS-Version", Environment.OSVersion.VersionString);
        request.Headers.TryAddWithoutValidation("X-Device-Model", Environment.MachineName);
        request.Headers.TryAddWithoutValidation("X-App-Name", "Godji app (Windows)");
        request.Headers.TryAddWithoutValidation("X-App-Version", "0.1.0");

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
            return (new List<VlessNode>(), $"subs.gojihub.xyz ответил HTTP {(int)response.StatusCode}: {Truncate(raw)}");

        if (response.Headers.TryGetValues("x-hwid-not-supported", out _))
            return (new List<VlessNode>(), "Сервер не распознал это устройство (x-hwid-not-supported) — HWID: " + _hwid.Get());
        if (response.Headers.TryGetValues("x-hwid-max-devices-reached", out _))
            return (new List<VlessNode>(), "Превышен лимит устройств по тарифу (x-hwid-max-devices-reached) — освободите слот устройства или смените тариф. HWID этого ПК: " + _hwid.Get());

        var (nodes, remark) = ParseJsonProfiles(raw);
        if (nodes.Count > 0) return (nodes, null);

        // Валидный JSON пришёл, но ни один элемент не распознан как реальный профиль узла —
        // почти всегда это как раз замаскированная под fake-профиль ошибка гейта (см. выше),
        // remark первого элемента и есть текст этой ошибки.
        if (remark != null) return (nodes, "Ответ сервера: " + remark);

        return (nodes, "Не удалось разобрать ответ subs.gojihub.xyz (неожиданный формат): " + Truncate(raw));
    }

    /// <summary>Протоколы, которые xray-core умеет обрабатывать как proxy-outbound (см.
    /// VpnEngine.WriteXrayConfigAsync — там те же четыре, плюс "freedom" для direct-правил).
    /// VLESS/VMess используют одну структуру settings.vnext (адрес+порт+users[].id), Trojan/
    /// Shadowsocks — другую, settings.servers (адрес+порт+пароль/метод, без отдельного
    /// UUID-пользователя) — раньше здесь искался только vnext, поэтому Trojan/Shadowsocks-узлы
    /// от Remnawave молча пропадали из списка серверов ещё на этапе парсинга, хотя xray их
    /// прекрасно умеет поднимать. Hysteria2/TUIC сюда намеренно не включены — это протоколы на
    /// QUIC, xray-core их не поддерживает в принципе (нужен sing-box-outbound или отдельный
    /// клиент), сейчас это отдельная, не связанная с парсингом подписки задача.</summary>
    private static readonly string[] ProxyProtocols = { "vless", "vmess", "trojan", "shadowsocks" };

    /// <summary>Реальный формат: JSON-массив профилей {remarks, dns, routing, outbounds, ...}.
    /// Легаси base64/vless:// формат (запасной вариант в Android) здесь не реализуем — бэкенд
    /// отдаёт его только при "неправильном" User-Agent, а мы всегда шлём "правильный".
    /// firstRemark — remarks первого элемента массива, даже если он не похож на реальный узел
    /// (нужно для диагностики HWID-гейта выше).</summary>
    private static (List<VlessNode> Nodes, string? FirstRemark) ParseJsonProfiles(string raw)
    {
        JsonArray array;
        try { array = JsonNode.Parse(raw.Trim())?.AsArray() ?? new JsonArray(); }
        catch { return (new List<VlessNode>(), null); }

        string? firstRemark = null;
        var result = new List<VlessNode>();
        for (var index = 0; index < array.Count; index++)
        {
            var profile = array[index]?.AsObject();
            if (profile == null) continue;
            if (index == 0) firstRemark = profile["remarks"]?.GetValue<string>();

            var outbounds = profile["outbounds"]?.AsArray();
            if (outbounds == null) continue;

            JsonObject? proxyOutbound = null;
            foreach (var ob in outbounds)
            {
                var obj = ob?.AsObject();
                var protocol = obj?["protocol"]?.GetValue<string>();
                if (protocol == null || Array.IndexOf(ProxyProtocols, protocol) < 0) continue;
                var settings = obj!["settings"]?.AsObject();
                if (settings?["vnext"] != null || settings?["servers"] != null)
                {
                    proxyOutbound = obj;
                    break;
                }
            }
            if (proxyOutbound == null) continue;

            // Один "странный" профиль (например пустой vnext/servers или неожиданная
            // структура) не должен обрушивать разбор всего списка — иначе пользователь вместо
            // 9 рабочих серверов из 10 не увидит ни одного.
            try
            {
                var settings = proxyOutbound["settings"]!.AsObject();
                string? host;
                int port;
                string? uuid = null;
                if (settings["vnext"] is JsonArray { Count: > 0 } vnextArray)
                {
                    // VLESS/VMess — адрес/порт/UUID клиента лежат в vnext[0].
                    var vnext = vnextArray[0]!.AsObject();
                    host = vnext["address"]?.GetValue<string>();
                    port = vnext["port"]?.GetValue<int>() is int p and > 0 ? p : 443;
                    uuid = vnext["users"]?.AsArray().Count > 0
                        ? vnext["users"]!.AsArray()[0]?.AsObject()["id"]?.GetValue<string>()
                        : null;
                }
                else if (settings["servers"] is JsonArray { Count: > 0 } serversArray)
                {
                    // Trojan/Shadowsocks — тот же адрес+порт, но без отдельного UUID-
                    // пользователя (общий пароль/метод на весь сервер) — VlessNode.Uuid
                    // остаётся null, это нормально, поле нужно только для отображения.
                    var server = serversArray[0]!.AsObject();
                    host = server["address"]?.GetValue<string>();
                    port = server["port"]?.GetValue<int>() is int p2 and > 0 ? p2 : 443;
                }
                else continue;

                if (string.IsNullOrWhiteSpace(host)) continue;
                var remark = profile["remarks"]?.GetValue<string>();
                remark = string.IsNullOrWhiteSpace(remark) ? $"Сервер {index + 1}" : remark;

                result.Add(new VlessNode
                {
                    Id = index.ToString(),
                    Name = remark,
                    Host = host,
                    Port = port,
                    ConnectPayloadJson = profile.ToJsonString(),
                    Uuid = uuid
                });
            }
            catch { /* битый отдельный профиль — пропускаем, остальные разбираем как обычно */ }
        }
        return (result, firstRemark);
    }

    private static string Truncate(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
