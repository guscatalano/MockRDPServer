namespace EchoDvcClient;

/// <summary>Minimal file logger. The plugin runs inside a COM server with no console, so its output
/// goes to %TEMP%\echo-dvc-client.log — the artifact you tail to watch the DVC round-trip.</summary>
internal static class Logger
{
    private static readonly object Gate = new();
    public static readonly string Path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "echo-dvc-client.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch { /* logging must never throw into a COM callback */ }
    }
}
