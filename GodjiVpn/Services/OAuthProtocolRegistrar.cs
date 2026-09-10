using Microsoft.Win32;

namespace GodjiVpn.Services;

/// <summary>
/// Регистрирует кастомную URI-схему godjivpn:// (HKCU — не требует прав сверх уже имеющихся,
/// приложение и так запускается с requireAdministrator) — тот же redirect_uri, что использует
/// Android-приложение (godjivpn://oauth2redirect), поэтому на бэкенде ничего менять не нужно:
/// он уже принимает и редиректит на эту схему после OAuth-логина в браузере.
/// </summary>
public static class OAuthProtocolRegistrar
{
    private const string Scheme = "godjivpn";

    public static void EnsureRegistered()
    {
        var exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
        var command = $"\"{exePath}\" \"%1\"";

        using var schemeKey = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        var currentCommand = schemeKey.OpenSubKey(@"shell\open\command")?.GetValue(null) as string;
        if (currentCommand == command) return; // уже зарегистрировано на этот же exe — ничего не трогаем

        schemeKey.SetValue(null, $"URL:{Scheme} Protocol");
        schemeKey.SetValue("URL Protocol", "");
        using (var iconKey = schemeKey.CreateSubKey("DefaultIcon"))
            iconKey.SetValue(null, $"{exePath},0");
        using (var commandKey = schemeKey.CreateSubKey(@"shell\open\command"))
            commandKey.SetValue(null, command);
    }
}
