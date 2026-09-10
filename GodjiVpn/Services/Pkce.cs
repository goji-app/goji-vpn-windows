using System.Security.Cryptography;
using System.Text;

namespace GodjiVpn.Services;

/// <summary>Генерация пары code_verifier/code_challenge для PKCE (RFC 7636) — используется для
/// OAuth (Google/Yandex) через api/auth/{provider}/start + api/auth/native/exchange.
/// Портировано 1:1 из auth/Pkce.kt.</summary>
public static class Pkce
{
    public static string GenerateVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Encode(bytes);
    }

    public static string ChallengeFor(string verifier)
    {
        var digest = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Encode(digest);
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
