using System.Text;

namespace UndoTheSpire2;

internal static class UndoDebugLog
{
    private static readonly Lock Sync = new();
    private static string? _logPath;

    public static string CurrentPath => _logPath ?? string.Empty;

    public static void Initialize()
    {
        try
        {
            lock (Sync)
            {
                _logPath = ResolveLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.WriteAllText(_logPath, $"=== Undo debug log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            _logPath = null;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                _logPath ??= ResolveLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(_logPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
        }
    }

    private static string ResolveLogPath()
    {
        List<string> candidates = [];
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            candidates.Add(Path.Combine(appData, "SlayTheSpire2", "undo-the-spire2", "undo-debug.log"));
        }

        candidates.Add(Path.Combine("C:\\undo-the-spire2", "undo-debug.log"));

        foreach (string candidate in candidates)
        {
            try
            {
                string? directory = Path.GetDirectoryName(candidate);
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                Directory.CreateDirectory(directory);
                return candidate;
            }
            catch
            {
            }
        }

        return Path.Combine("C:\\undo-the-spire2", "undo-debug.log");
    }
}
