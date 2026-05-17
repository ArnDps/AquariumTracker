using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ADAqua.App;

public static class MySqlConfigurationStore
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ADAqua");

    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "mysql-settings.json");

    public static bool Exists => File.Exists(SettingsPath);

    public static MySqlConnectionSettings? Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return null;
        }

        var stored = JsonSerializer.Deserialize<StoredMySqlConnectionSettings>(File.ReadAllText(SettingsPath));
        if (stored is null)
        {
            return null;
        }

        return new MySqlConnectionSettings
        {
            Server = stored.Server,
            Port = stored.Port,
            Database = stored.Database,
            UserId = stored.UserId,
            Password = Unprotect(stored.ProtectedPassword)
        };
    }

    public static void Save(MySqlConnectionSettings settings)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var stored = new StoredMySqlConnectionSettings
        {
            Server = settings.Server,
            Port = settings.Port,
            Database = settings.Database,
            UserId = settings.UserId,
            ProtectedPassword = Protect(settings.Password)
        };

        var json = JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static MySqlConnectionSettings CreateDefault(string? fallbackConnectionString)
    {
        var saved = Load();
        if (saved is not null)
        {
            return saved;
        }

        if (!string.IsNullOrWhiteSpace(fallbackConnectionString))
        {
            return MySqlConnectionSettings.FromConnectionString(fallbackConnectionString);
        }

        return new MySqlConnectionSettings();
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedValue);
            var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            return string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private sealed class StoredMySqlConnectionSettings
    {
        public string Server { get; set; } = "localhost";
        public uint Port { get; set; } = 3306;
        public string Database { get; set; } = "ADAqua";
        public string UserId { get; set; } = "root";
        public string ProtectedPassword { get; set; } = string.Empty;
    }
}
