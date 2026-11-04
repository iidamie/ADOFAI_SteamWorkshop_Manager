using System.Text.Json;

namespace SteamWorkshopManager;

internal sealed class SteamAuthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _filePath;

    public string DirectoryPath => _directory;

    public SteamAuthStore(string modDirectory)
    {
        _directory = Path.Combine(modDirectory, "data");
        _filePath = Path.Combine(_directory, "steam-session.json");
        Directory.CreateDirectory(_directory);
    }

    public SteamSavedLogin? Load()
    {
        if (!File.Exists(_filePath))
            return null;

        var saved = JsonSerializer.Deserialize<SteamSavedLogin>(File.ReadAllText(_filePath));
        if (saved == null || string.IsNullOrWhiteSpace(saved.AccountName)
            || saved.SteamId == 0 || string.IsNullOrWhiteSpace(saved.RefreshToken))
            throw new InvalidDataException("已保存的 Steam 会话数据无效。");
        return saved;
    }

    public void Save(SteamSavedLogin saved)
    {
        Directory.CreateDirectory(_directory);
        var temporaryPath = Path.Combine(_directory, $"steam-session.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(saved, JsonOptions));
            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public void Clear()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

}
