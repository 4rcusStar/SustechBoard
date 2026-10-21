using System.Security.Cryptography;
using System.Text;

namespace SustechBoard.Core;

public static class Hash
{
    public static string Sha256(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes);
    }
}

public static class AppPaths
{
    public static string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SustechBoard");

    public static string SettingsFile => Path.Combine(RootDir, "settings.json");
    public static string SecretsFile => Path.Combine(RootDir, "secrets.dat");
    public static string DatabaseFile => Path.Combine(RootDir, "board.db");
    public static string LogFile => Path.Combine(RootDir, "app.log");

    public static void EnsureCreated() => Directory.CreateDirectory(RootDir);
}

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) =>
        Write("ERR ", ex is null ? msg : $"{msg} :: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private static void Write(string level, string msg)
    {
        try
        {
            AppPaths.EnsureCreated();
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}";
            lock (Gate)
            {
                var fi = new FileInfo(AppPaths.LogFile);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                {
                    var old = AppPaths.LogFile + ".1";
                    File.Delete(old);
                    File.Move(AppPaths.LogFile, old);
                }
                File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // logging must never throw
        }
    }
}
