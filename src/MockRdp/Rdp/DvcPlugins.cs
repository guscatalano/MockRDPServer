using System.Reflection;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MockRdp.Plugin;

namespace MockRdp.Rdp;

/// <summary>
/// Loads server-side DVC plugins from DLLs and indexes them by channel name. A plugin is any public
/// parameterless type implementing <see cref="IServerDvcPlugin"/>. Loaded into the default context
/// (via <see cref="Assembly.LoadFrom(string)"/>) so the plugin's <c>MockRdp.Plugin</c> types are the
/// same types the server sees — a dev fixture, not a sandbox.
/// </summary>
public sealed class DvcPluginHost
{
    private readonly List<IServerDvcPlugin> _plugins = new();
    private readonly Dictionary<string, IServerDvcPlugin> _byChannel = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IServerDvcPlugin> Plugins => _plugins;

    /// <summary>All channel names any loaded plugin serves.</summary>
    public IReadOnlyCollection<string> Channels => _byChannel.Keys;

    public IServerDvcPlugin? ForChannel(string channel) => _byChannel.GetValueOrDefault(channel);

    /// <summary>Build a host from already-instantiated plugins (tests, or an in-proc host).</summary>
    public static DvcPluginHost FromPlugins(params IServerDvcPlugin[] plugins)
    {
        var host = new DvcPluginHost();
        foreach (var p in plugins) host.Add(p, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        return host;
    }

    public static DvcPluginHost Load(IEnumerable<string> dllPaths, ILogger log)
    {
        var host = new DvcPluginHost();
        foreach (var raw in dllPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(raw);
            if (!File.Exists(path)) { log.LogWarning("DVC plugin not found: {Path}", path); continue; }
            try
            {
                var asm = Assembly.LoadFrom(path);
                int found = 0;
                foreach (var t in asm.GetTypes())
                {
                    if (t.IsAbstract || !typeof(IServerDvcPlugin).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) is null) continue;
                    var plugin = (IServerDvcPlugin)Activator.CreateInstance(t)!;
                    host.Add(plugin, log);
                    found++;
                }
                if (found == 0) log.LogWarning("No IServerDvcPlugin types in {Path}.", path);
            }
            catch (Exception ex)
            {
                log.LogWarning("Failed to load DVC plugin {Path}: {Err}", path, ex.Message);
            }
        }
        return host;
    }

    private void Add(IServerDvcPlugin plugin, ILogger log)
    {
        _plugins.Add(plugin);
        foreach (var ch in plugin.Channels)
        {
            if (_byChannel.TryGetValue(ch, out var other))
                log.LogWarning("DVC channel '{Ch}' served by both '{A}' and '{B}'; keeping the first.", ch, other.Name, plugin.Name);
            else
                _byChannel[ch] = plugin;
        }
        log.LogInformation("Loaded DVC plugin '{Name}' serving [{Channels}].", plugin.Name, string.Join(", ", plugin.Channels));
    }
}

/// <summary>
/// The server side of one open DVC handed to a plugin: an inbound queue fed by the connection's
/// receive loop (as complete, reassembled messages) plus a write delegate that fragments and sends.
/// This is what makes a plugin's read/write loop mirror a real WTSVirtualChannelRead/Write agent.
/// </summary>
internal sealed class DvcChannelPipe : IDvcChannel
{
    private readonly Channel<byte[]> _inbound = System.Threading.Channels.Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Func<byte[], CancellationToken, Task> _write;
    private readonly Action<string> _log;

    public DvcChannelPipe(string name, Func<byte[], CancellationToken, Task> write, Action<string> log)
    {
        Name = name;
        _write = write;
        _log = log;
    }

    public string Name { get; }

    /// <summary>Called by the connection when a complete client message arrives.</summary>
    public void Feed(byte[] data) => _inbound.Writer.TryWrite(data);

    /// <summary>Called by the connection when the channel closes; unblocks the plugin's ReadAsync.</summary>
    public void Complete() => _inbound.Writer.TryComplete();

    public async Task<byte[]?> ReadAsync(CancellationToken ct)
    {
        try { return await _inbound.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { return null; }
        catch (OperationCanceledException) { return null; }
    }

    public Task WriteAsync(byte[] data, CancellationToken ct) => _write(data, ct);

    public void Log(string message) => _log(message);
}
