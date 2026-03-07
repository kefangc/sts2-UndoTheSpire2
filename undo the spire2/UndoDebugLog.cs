using System.Text;

namespace UndoTheSpire2;

internal static class UndoDebugLog
{
    private static readonly Lock Sync = new();
    private static readonly string LogPath = Path.Combine("F:\\projects\\undo-the-spire2-cache", "logs", "undo-debug.log");

    public static string CurrentPath => LogPath;

    public static void Initialize()
    {
        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, $"=== Undo debug log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}", Encoding.UTF8);
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
