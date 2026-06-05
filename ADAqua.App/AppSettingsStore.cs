using System.IO;
using System.Text.Json;

namespace ADAqua.App;

public static class AppSettingsStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ADAqua");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "app-settings.json");

    public static AppSettings? Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}

public sealed class AppSettings
{
    public string LanguageCode { get; set; } = "fr";
    public string ThemeCode { get; set; } = "light";
}
