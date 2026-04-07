using System.Collections.Concurrent;
using System.Text;

namespace UndoTheSpire2;

internal sealed class UndoBufferedLogWriter : IDisposable
{
    private readonly string _path;
    private readonly ConcurrentQueue<string> _pendingLines = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _writerTask;
    private int _queuedLineCount;

    public UndoBufferedLogWriter(string path, string headerLine)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, headerLine, Encoding.UTF8);
        _writerTask = Task.Factory.StartNew(
            WriteLoop,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public void Enqueue(string line)
    {
        if (_shutdown.IsCancellationRequested)
            return;

        _pendingLines.Enqueue(line);
        Interlocked.Increment(ref _queuedLineCount);
        _signal.Set();
    }

    public void Flush()
    {
        DrainPendingLines();
    }

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
            return;

        _shutdown.Cancel();
        _signal.Set();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        DrainPendingLines();
        _signal.Dispose();
        _shutdown.Dispose();
    }

    private void WriteLoop()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                _signal.WaitOne(TimeSpan.FromMilliseconds(250));
                DrainPendingLines();
            }
        }
        catch
        {
        }
        finally
        {
            DrainPendingLines();
        }
    }

    private void DrainPendingLines()
    {
        if (Volatile.Read(ref _queuedLineCount) == 0)
            return;

        StringBuilder buffer = new();
        while (_pendingLines.TryDequeue(out string? line))
        {
            Interlocked.Decrement(ref _queuedLineCount);
            buffer.Append(line);
        }

        if (buffer.Length == 0)
            return;

        File.AppendAllText(_path, buffer.ToString(), Encoding.UTF8);
    }
}
