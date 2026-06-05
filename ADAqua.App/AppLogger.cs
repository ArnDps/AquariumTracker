using System.IO;
using System.Text;

namespace ADAqua.App;

public static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ADAqua",
        "logs");

    public static string LogFilePath => Path.Combine(LogDirectory, "adaqua.log");

    public static void Info(string message)
    {
        Write("INFO", message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        var payload = exception is null ? message : $"{message}{Environment.NewLine}{exception}";
        Write("ERROR", payload);
    }

    public static string ReadTail(int maxChars = 12000)
    {
        lock (SyncRoot)
        {
            if (!File.Exists(LogFilePath))
            {
                return string.Empty;
            }

            var text = File.ReadAllText(LogFilePath, Encoding.UTF8);
            if (text.Length <= maxChars)
            {
                return text;
            }

            return text[^maxChars..];
        }
    }

    private static void Write(string level, string message)
    {
        lock (SyncRoot)
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}{Environment.NewLine}";
            File.AppendAllText(LogFilePath, line, Encoding.UTF8);
        }
    }
}
