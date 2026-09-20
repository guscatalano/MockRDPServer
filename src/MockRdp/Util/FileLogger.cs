using Microsoft.Extensions.Logging;

namespace MockRdp.Util;

/// <summary>
/// Minimal file logger provider: writes each entry to a file and flushes immediately, so
/// the log survives even when the server process is killed (e.g. a test harness stopping
/// the mock after a connection). Complements the console logger; enabled with --log-file.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public FileLoggerProvider(string path)
    {
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _writer, _gate);

    public void Dispose() => _writer.Dispose();

    private sealed class FileLogger(string category, StreamWriter writer, object gate) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            lock (gate)
            {
                writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {logLevel,-5} {category}: {formatter(state, exception)}");
                if (exception is not null) writer.WriteLine(exception);
            }
        }
    }
}
