using System.Text.Json;

namespace Viddl.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public SettingsStore(string filePath) => FilePath = filePath;

    public string FilePath { get; }

    /// <summary>Loads settings (defaults if the file is missing or unreadable) and upgrades them.</summary>
    public AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
        }
        catch (JsonException) { /* corrupt file - fall back to defaults */ }
        catch (IOException) { }

        settings ??= new AppSettings();
        settings.Upgrade();
        return settings;
    }

    /// <summary>Saves settings; returns false if the file couldn't be written.</summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
