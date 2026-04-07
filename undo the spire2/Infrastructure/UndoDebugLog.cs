// 文件说明：输出额外调试日志，便于复现和定位 undo 问题。
using System.Text;

namespace UndoTheSpire2;

internal static class UndoDebugLog
{
    private static readonly Lock Sync = new();
    private static bool _processExitHooked;
    private static string? _logPath;
    private static UndoBufferedLogWriter? _writer;

    public static string CurrentPath => _logPath ?? string.Empty;

    public static void Initialize()
    {
        try
        {
            lock (Sync)
            {
                _logPath = ResolveLogPath();
                EnsureProcessExitHooks();
                ReplaceWriter(_logPath, $"=== Undo debug log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}");
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
            UndoBufferedLogWriter writer = EnsureWriter();
            writer.Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static UndoBufferedLogWriter EnsureWriter()
    {
        lock (Sync)
        {
            _logPath ??= ResolveLogPath();
            EnsureProcessExitHooks();
            _writer ??= new UndoBufferedLogWriter(
                _logPath,
                $"=== Undo debug log started {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ==={Environment.NewLine}");
            return _writer;
        }
    }

    private static void ReplaceWriter(string logPath, string headerLine)
    {
        UndoBufferedLogWriter? previousWriter = _writer;
        _writer = null;
        try
        {
            previousWriter?.Dispose();
        }
        catch
        {
        }

        _writer = new UndoBufferedLogWriter(logPath, headerLine);
    }

    private static void EnsureProcessExitHooks()
    {
        if (_processExitHooked)
            return;

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => FlushAndDisposeWriter();
        _processExitHooked = true;
    }

    private static void FlushAndDisposeWriter()
    {
        lock (Sync)
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
            }
            finally
            {
                _writer = null;
            }
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
