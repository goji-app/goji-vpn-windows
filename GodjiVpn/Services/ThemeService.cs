using System.IO;
using System.Windows;

namespace GodjiVpn.Services;

/// <summary>
/// Переключает светлую/тёмную палитру (см. Themes/LightTheme.xaml, Themes/DarkTheme.xaml —
/// 1:1 с Android GodjiColors.applyLight()/applyDark()) подменой ПЕРВОГО элемента
/// Application.Resources.MergedDictionaries — App.xaml всегда держит там ровно один словарь
/// темы (см. его собственный комментарий). Все *Brush-ссылки в XAML — {DynamicResource}, не
/// {StaticResource}: только Dynamic видит подмену словаря в рантайме без перезапуска.
///
/// Раньше тёмной темы не было вовсе — сознательный выбор ("фирменный светлый стиль, не
/// адаптивная тема"), который пользователь позже прямо попросил пересмотреть.
/// </summary>
public sealed class ThemeService
{
    /// <summary>Тот же паттерн, что VpnEngine.Current — глобус (GlobeHost.xaml.cs) живёт в
    /// WebView2, до него не дотянуться обычной проводкой через ViewModel/DataContext на
    /// каждый экран, где он есть (Login, Connect), проще прочитать статический синглтон.</summary>
    public static ThemeService? Current { get; private set; }

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GodjiVpn", "theme.txt");

    public bool IsDark { get; private set; }

    /// <summary>Не событие PropertyChanged — GlobeHost не ViewModel, ему не нужен полный
    /// INotifyPropertyChanged ради одного bool.</summary>
    public event Action? Changed;

    public ThemeService() => Current = this;

    public void Initialize()
    {
        IsDark = Load();
        Apply();
    }

    public void SetDark(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        Save();
        Apply();
        Changed?.Invoke();
    }

    private void Apply()
    {
        var uri = new Uri(IsDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };
        Application.Current.Resources.MergedDictionaries[0] = dict;
    }

    private static bool Load()
    {
        try { return File.Exists(StatePath) && File.ReadAllText(StatePath).Trim() == "dark"; }
        catch { return false; }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, IsDark ? "dark" : "light");
        }
        catch { /* лучшее усилие — при следующем старте просто снова спросим у файла и получим light по умолчанию */ }
    }
}
