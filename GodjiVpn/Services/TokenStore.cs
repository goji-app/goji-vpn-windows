using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace GodjiVpn.Services;

/// <summary>
/// Хранит JWT в %LOCALAPPDATA%\GodjiVpn\secure.dat, зашифрованным через DPAPI
/// (CurrentUser scope) — аналог EncryptedSharedPreferences в Android-версии: файл
/// бесполезен вне профиля текущего пользователя Windows на этой машине.
/// </summary>
public sealed class TokenStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GodjiVpn", "secure.dat");

    private string? _cachedToken;

    public string? AccessToken => _cachedToken ??= Load();

    public void Save(string accessToken)
    {
        _cachedToken = accessToken;
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        var plainBytes = Encoding.UTF8.GetBytes(accessToken);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(StorePath, protectedBytes);
    }

    public bool IsLoggedIn => !string.IsNullOrEmpty(AccessToken);

    public void Clear()
    {
        _cachedToken = null;
        if (File.Exists(StorePath)) File.Delete(StorePath);
    }

    private static string? Load()
    {
        if (!File.Exists(StorePath)) return null;
        try
        {
            var protectedBytes = File.ReadAllBytes(StorePath);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception)
        {
            // CryptographicException — файл зашифрован под другим профилем/машиной
            // (переустановка Windows и т.п.). Но File.ReadAllBytes/Unprotect может упасть и
            // по другим причинам (IOException — файл временно залочен антивирусом/бэкап-
            // агентом, UnauthorizedAccessException — права поменялись) — это вызывается из
            // App.OnStartup ДО показа окна, необработанное исключение здесь означало бы, что
            // приложение вообще не запускается, пока кто-то вручную не удалит secure.dat.
            // Во всех этих случаях правильнее считать, что токена нет (попросим войти заново),
            // чем не дать приложению открыться вовсе.
            return null;
        }
    }
}
