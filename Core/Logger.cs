using System.Text;

namespace LocalSendWinForms.Core;

/// <summary>
/// Minimal thread-safe file logger (no external dependencies). Logs live under
/// %LocalAppData%\LocalSendWin\logs\localsend.log and are also mirrored to the
/// debugger output window while developing.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private static readonly string LogPath = BuildPath();
    private static bool _failed;

    private static string BuildPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LocalSendWin", "logs");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "localsend.log");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (_failed) return;

        try
        {
            lock (Gate)
            {
                FileInfo fi = new(LogPath);
                if (fi.Exists && fi.Length > 4 * 1024 * 1024)
                {
                    // Simple single-generation rotation.
                    var bak = LogPath + ".1";
                    try { File.Delete(bak); } catch { }
                    try { File.Move(LogPath, bak); } catch { }
                }
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            _failed = true; // logging must never take the app down
        }
    }
}
