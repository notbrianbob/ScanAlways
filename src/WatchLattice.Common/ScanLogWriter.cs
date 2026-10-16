namespace WatchLattice.Common;

/// <summary>
/// Appends scanner-style lines to a rolling, size-capped text file. This log is
/// cosmetic telemetry — a place where the decoy's narration lands so the
/// machine looks like it keeps a live scan log. It is disposable and never read
/// back by the engine.
/// </summary>
public sealed class ScanLogWriter : IDisposable
{
    private const long MaxSizeBytes = 2L * 1024 * 1024;

    private readonly string _path;
    private readonly object _gate = new();
    private StreamWriter? _writer;

    public ScanLogWriter(string path)
    {
        _path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        Open(append: true);
    }

    public string Path => _path;

    public void WriteLine(string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        lock (_gate)
        {
            _writer?.WriteLine(line);
            RollIfNeeded();
        }
    }

    private void Open(bool append)
    {
        _writer = new StreamWriter(
            new FileStream(_path, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    private void RollIfNeeded()
    {
        try
        {
            if (_writer is null || _writer.BaseStream.Length < MaxSizeBytes)
            {
                return;
            }

            _writer.Dispose();
            var backup = _path + ".1";
            File.Delete(backup);
            File.Move(_path, backup);
            Open(append: false);
        }
        catch (IOException)
        {
            // A locked log is a cosmetic failure; keep running without it.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
