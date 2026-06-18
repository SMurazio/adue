namespace Mmo.Server.Runtime;

public static class Log
{
    private static readonly ServerLogWriter Writer = ServerLogWriter.FromEnvironment(Console.WriteLine);
    private static readonly AsyncConsoleLogSink Sink = new(Writer.Write);

    public static void Info(string message)
    {
        Sink.Write(LogLevel.Info, message);
    }

    public static void Warn(string message)
    {
        Sink.Write(LogLevel.Warn, message);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Sink.Write(LogLevel.Error, exception is null ? message : $"{message}: {exception.Message}");
    }

    public static bool Flush()
    {
        return Sink.Flush(TimeSpan.FromSeconds(2));
    }
}

internal enum LogLevel
{
    Info,
    Warn,
    Error
}

internal sealed class AsyncConsoleLogSink : IDisposable
{
    private const int DefaultCapacity = 4096;

    private readonly object _gate = new();
    private readonly LinkedList<LogEntry> _entries = new();
    private readonly Action<LogLevel, string> _write;
    private readonly AutoResetEvent _hasEntries = new(initialState: false);
    private readonly ManualResetEventSlim _drained = new(initialState: true);
    private readonly Thread _worker;
    private readonly int _capacity;
    private int _pendingOrWritingCount;
    private long _droppedNonErrorCount;
    private bool _isStopping;
    private bool _disposed;

    public AsyncConsoleLogSink(Action<string> write, int capacity = DefaultCapacity)
        : this((_, line) => write(line), capacity)
    {
    }

    public AsyncConsoleLogSink(Action<LogLevel, string> write, int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Log queue capacity must be positive.");
        }

        _write = write ?? throw new ArgumentNullException(nameof(write));
        _capacity = capacity;
        _worker = new Thread(Drain)
        {
            IsBackground = true,
            Name = "mmo-log-writer"
        };
        _worker.Start();
    }

    public long DroppedNonErrorCount => Interlocked.Read(ref _droppedNonErrorCount);

    public void Write(LogLevel level, string message)
    {
        var shouldSignal = false;
        lock (_gate)
        {
            if (_isStopping)
            {
                return;
            }

            if (level == LogLevel.Error)
            {
                if (_pendingOrWritingCount >= _capacity)
                {
                    DropOldestNonError();
                }
            }
            else if (_pendingOrWritingCount >= _capacity)
            {
                Interlocked.Increment(ref _droppedNonErrorCount);
                return;
            }

            _entries.AddLast(new LogEntry(DateTimeOffset.UtcNow, level, message));
            _pendingOrWritingCount++;
            _drained.Reset();
            shouldSignal = true;
        }

        if (shouldSignal)
        {
            _hasEntries.Set();
        }
    }

    public bool Flush(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Flush timeout cannot be negative.");
        }

        return _drained.Wait(timeout);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_gate)
        {
            _isStopping = true;
        }

        _hasEntries.Set();
        if (_worker.Join(TimeSpan.FromSeconds(2)))
        {
            _hasEntries.Dispose();
            _drained.Dispose();
        }
    }

    private void DropOldestNonError()
    {
        for (var node = _entries.First; node is not null; node = node.Next)
        {
            if (node.Value.Level == LogLevel.Error)
            {
                continue;
            }

            _entries.Remove(node);
            _pendingOrWritingCount--;
            Interlocked.Increment(ref _droppedNonErrorCount);
            return;
        }
    }

    private void Drain()
    {
        while (true)
        {
            if (!TryDequeue(out var entry))
            {
                return;
            }

            SafeWrite(entry);
            lock (_gate)
            {
                _pendingOrWritingCount--;
                if (_pendingOrWritingCount == 0)
                {
                    _drained.Set();
                }
            }
        }
    }

    private bool TryDequeue(out LogEntry entry)
    {
        while (true)
        {
            lock (_gate)
            {
                if (_entries.First is not null)
                {
                    entry = _entries.First.Value;
                    _entries.RemoveFirst();
                    return true;
                }

                if (_isStopping)
                {
                    entry = default;
                    return false;
                }
            }

            _hasEntries.WaitOne();
        }
    }

    private void SafeWrite(LogEntry entry)
    {
        try
        {
            _write(entry.Level, entry.Format());
        }
        catch
        {
            // Logging must never crash the server.
        }
    }

    private readonly record struct LogEntry(DateTimeOffset Timestamp, LogLevel Level, string Message)
    {
        public string Format()
        {
            return $"{Timestamp:O} [{FormatLevel(Level)}] {Message}";
        }

        private static string FormatLevel(LogLevel level)
        {
            return level switch
            {
                LogLevel.Info => "info",
                LogLevel.Warn => "warn",
                LogLevel.Error => "error",
                _ => "unknown"
            };
        }
    }
}

internal sealed class ServerLogWriter
{
    private const string LogFileEnvironmentKey = "MMO_SERVER_LOG_FILE";
    private const string ErrorLogFileEnvironmentKey = "MMO_SERVER_ERR_LOG_FILE";

    private readonly Action<string> _consoleWrite;
    private readonly string? _logPath;
    private readonly string? _errorLogPath;
    private readonly object _fileGate = new();

    public ServerLogWriter(Action<string> consoleWrite, string? logPath = null, string? errorLogPath = null)
    {
        _consoleWrite = consoleWrite ?? throw new ArgumentNullException(nameof(consoleWrite));
        _logPath = NormalizePath(logPath);
        _errorLogPath = NormalizePath(errorLogPath);
    }

    public static ServerLogWriter FromEnvironment(Action<string> consoleWrite)
    {
        return new ServerLogWriter(
            consoleWrite,
            Environment.GetEnvironmentVariable(LogFileEnvironmentKey),
            Environment.GetEnvironmentVariable(ErrorLogFileEnvironmentKey));
    }

    public void Write(LogLevel level, string line)
    {
        _consoleWrite(line);

        if (_logPath is null && _errorLogPath is null)
        {
            return;
        }

        lock (_fileGate)
        {
            if (_logPath is not null)
            {
                TryAppendLine(_logPath, line);
            }

            if (level == LogLevel.Error && _errorLogPath is not null)
            {
                TryAppendLine(_errorLogPath, line);
            }
        }
    }

    private static string? NormalizePath(string? path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
    }

    private static void TryAppendLine(string path, string line)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch
        {
            // File logging must never crash the server.
        }
    }
}
