using System.IO;
using System.Text.Json;

namespace GodjiVpn.Services;

/// <summary>Избранные серверы (закреплены сверху списка на вкладке "Серверы") — просто набор id
/// узлов, сами узлы приходят из подписки и не хранятся здесь. Порт из Android
/// (SettingsRepository.favoriteServerIds).</summary>
public sealed class FavoriteServersStore
{
    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GodjiVpn", "favorite-servers.json");

    private readonly HashSet<string> _ids;

    public event Action? Changed;

    public FavoriteServersStore() => _ids = Load();

    public bool IsFavorite(string id) => _ids.Contains(id);

    public void Toggle(string id)
    {
        if (!_ids.Remove(id)) _ids.Add(id);
        Save();
        Changed?.Invoke();
    }

    private static HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new HashSet<string>();
            return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(StorePath)) ?? new HashSet<string>();
        }
        catch { return new HashSet<string>(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_ids));
        }
        catch { /* лучшее усилие — потеря избранного не критична */ }
    }
}
