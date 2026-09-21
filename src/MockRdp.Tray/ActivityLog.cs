using Microsoft.Extensions.Logging;

namespace MockRdp.Tray;

/// <summary>
/// An <see cref="ILoggerFactory"/> that captures the server's log stream so the tray can show it
/// live. Keeps a bounded ring buffer of recent lines (so a window opened later still sees history)
/// and raises <see cref="LineWritten"/> for each new line. Thread-safe: the server logs from
/// background connection threads.
/// </summary>
internal sealed class ActivityLog : ILoggerFactory
{
    private const int Capacity = 4000;

    private readonly object _gate = new();
    private readonly Queue<string> _buffer = new();

    /// <summary>Raised (possibly off the UI thread) for each formatted line.</summary>
    public event Action<string>? LineWritten;

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate) return _buffer.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _buffer.Clear();
    }

    // ILoggerFactory
    public void AddProvider(ILoggerProvider provider) { }
    public ILogger CreateLogger(string categoryName) => new SinkLogger(this, categoryName);
    public void Dispose() { }

    private void Write(string line)
    {
        lock (_gate)
        {
            _buffer.Enqueue(line);
            while (_buffer.Count > Capacity) _buffer.Dequeue();
        }
        LineWritten?.Invoke(line);
    }

    private sealed class SinkLogger(ActivityLog sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var name = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            sink.Write($"{DateTime.Now:HH:mm:ss} {Short(level)} {name}: {formatter(state, ex)}");
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error or LogLevel.Critical => "ERR ",
            LogLevel.Debug => "DBG ",
            LogLevel.Trace => "TRC ",
            _ => "    ",
        };
    }
}
