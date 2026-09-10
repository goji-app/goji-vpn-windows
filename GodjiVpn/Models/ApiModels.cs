using System.Text.Json.Serialization;

namespace GodjiVpn.Models;

// Контракт подтверждён чтением реального кода Android-приложения
// (xyz.gojihub.vpn.network.models.Models.kt в каноническом источнике) — сверено
// напрямую с бэкендом gojihub.xyz разработчиком Android-версии, не догадка.

public sealed class SendOtpRequest
{
    public string Email { get; set; } = "";
}

public sealed class SendOtpResponse
{
    public bool Sent { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }
}

public sealed class VerifyOtpRequest
{
    public string Email { get; set; } = "";
    public string Code { get; set; } = "";
}

public sealed class VerifyOtpResponse
{
    public string Token { get; set; } = "";

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    public AuthUser? User { get; set; }

    [JsonPropertyName("account_existed")]
    public bool AccountExisted { get; set; }
}

public sealed class AuthUser
{
    [JsonPropertyName("customer_id")]
    public string CustomerId { get; set; } = "";

    public string? Email { get; set; }
}

public sealed class MeResponse
{
    [JsonPropertyName("customer_id")]
    public string CustomerId { get; set; } = "";

    public string? Email { get; set; }
    public string Role { get; set; } = "";

    [JsonPropertyName("consent_required")]
    public bool ConsentRequired { get; set; }
}

// ── OAuth (google/yandex — PKCE, тот же redirect URI godjivpn://oauth2redirect,
//    что и Android; telegram-oidc существует на бэкенде, но там сломан — как и в
//    Android, кнопка Telegram остаётся выключенной с бейджем "СКОРО") ───────

public sealed class StartAuthResponse
{
    [JsonPropertyName("auth_url")]
    public string AuthUrl { get; set; } = "";
}

public sealed class NativeExchangeRequest
{
    public string Code { get; set; } = "";

    [JsonPropertyName("code_verifier")]
    public string CodeVerifier { get; set; } = "";

    public string Provider { get; set; } = "";
}

public sealed class NativeExchangeResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }
}

public sealed class ConsentRequest
{
    public bool Terms { get; set; } = true;

    [JsonPropertyName("personal_data")]
    public bool PersonalData { get; set; } = true;
}

public sealed class SubscriptionsResponse
{
    public List<SubscriptionInfo> Subscriptions { get; set; } = new();
}

public sealed class SubscriptionInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    [JsonPropertyName("is_primary")]
    public bool IsPrimary { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; }

    [JsonPropertyName("plan_name")]
    public string PlanName { get; set; } = "";

    [JsonPropertyName("expire_at")]
    public string ExpireAt { get; set; } = "";

    [JsonPropertyName("days_left")]
    public int DaysLeft { get; set; }

    [JsonPropertyName("subscription_link")]
    public string SubscriptionLink { get; set; } = "";

    [JsonPropertyName("device_limit")]
    public int DeviceLimit { get; set; }

    /// <summary>"trial" на пробном тарифе, иначе null — не приходит в старых ответах бэкенда,
    /// см. Models.kt (Android): по умолчанию считаем "не триал".</summary>
    public string? Kind { get; set; }

    public TrafficInfo Traffic { get; set; } = new();
}

public sealed class TrafficInfo
{
    [JsonPropertyName("used_bytes")]
    public long UsedBytes { get; set; }

    [JsonPropertyName("limit_bytes")]
    public long LimitBytes { get; set; }

    [JsonPropertyName("is_unlimited")]
    public bool IsUnlimited { get; set; }
}

public sealed class PlansResponse
{
    public List<PlanInfo> Plans { get; set; } = new();
}

public sealed class PlanInfo
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<PriceInfo> Prices { get; set; } = new();
}

public sealed class PriceInfo
{
    [JsonPropertyName("price_type")]
    public string PriceType { get; set; } = "";

    public int Price { get; set; }
    public string Currency { get; set; } = "";

    [JsonPropertyName("period_value")]
    public int PeriodValue { get; set; }

    [JsonPropertyName("period_unit")]
    public string PeriodUnit { get; set; } = "";
}

/// <summary>
/// Один сервер из подписки пользователя. ConnectPayloadJson — это уже готовый Xray-конфиг
/// (dns/routing/outbounds, без inbounds), полученный с subs.gojihub.xyz при переданном X-HWID.
/// </summary>
public sealed class VlessNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string ConnectPayloadJson { get; init; }
    public string? Uuid { get; init; }
}
