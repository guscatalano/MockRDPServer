namespace MockRdp.Plugin;

/// <summary>
/// A server-side dynamic virtual channel plugin. Implement this in your own DLL, reference only
/// <c>MockRdp.Plugin</c>, and load it into the mock (tray → <b>Load DVC plugin…</b>, or
/// <c>--plugin &lt;your.dll&gt;</c>). The mock opens each of your <see cref="Channels"/> on every
/// connection and calls <see cref="RunAsync"/> with a channel you read and write.
///
/// This mirrors the real thing: a genuine server-side DVC is an in-session program that opens the
/// channel with <c>WTSVirtualChannelOpenEx</c> and loops on <c>WTSVirtualChannelRead</c> /
/// <c>WTSVirtualChannelWrite</c>. Your <see cref="RunAsync"/> loop over <see cref="IDvcChannel"/>
/// is that same loop — so code written here is a step away from running as a real agent.
///
/// The implementing type must have a public parameterless constructor.
/// </summary>
public interface IServerDvcPlugin
{
    /// <summary>A short display name, shown in the tray/log.</summary>
    string Name { get; }

    /// <summary>The dynamic virtual channel name(s) this plugin serves, e.g. <c>"MYAPP::control"</c>.</summary>
    IReadOnlyList<string> Channels { get; }

    /// <summary>
    /// Serve one opened channel for the life of the connection. Read requests and write replies just
    /// as an in-session agent would — return (or let <paramref name="ct"/> cancel) when done.
    /// A fresh call is made per connection and per channel.
    /// </summary>
    Task RunAsync(IDvcChannel channel, CancellationToken ct);
}

/// <summary>
/// One open DVC on one connection — the plugin's view of it. Read complete (reassembled) client
/// messages and write bytes back, mirroring <c>WTSVirtualChannelRead</c> / <c>WTSVirtualChannelWrite</c>.
/// The mock owns fragmentation and reassembly.
/// </summary>
public interface IDvcChannel
{
    /// <summary>The channel name.</summary>
    string Name { get; }

    /// <summary>The next complete message from the client, or <c>null</c> when the channel closes.
    /// Like <c>WTSVirtualChannelRead</c>.</summary>
    Task<byte[]?> ReadAsync(CancellationToken ct);

    /// <summary>Send bytes to the client (the mock fragments to the negotiated VC chunk size).
    /// Like <c>WTSVirtualChannelWrite</c>.</summary>
    Task WriteAsync(byte[] data, CancellationToken ct);

    /// <summary>Write a line to the mock's activity log.</summary>
    void Log(string message);
}
