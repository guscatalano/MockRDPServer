using System.Text;
using MockRdp.Plugin;

namespace UppercaseDvcPlugin;

/// <summary>
/// A minimal server-side DVC plugin: opens "SAMPLE::upper", greets the client, then echoes each
/// message back upper-cased. Copy this as a starting point — the RunAsync loop is the same shape as a
/// real in-session agent looping on WTSVirtualChannelRead / WTSVirtualChannelWrite.
///
/// Build:  dotnet build samples/UppercaseDvcPlugin -c Release
/// Load:   tray → "Load DVC plugin…"  (or:  MockRdpCli --plugin bin/Release/net10.0/UppercaseDvcPlugin.dll)
/// </summary>
public sealed class UppercasePlugin : IServerDvcPlugin
{
    public string Name => "Uppercase echo (sample)";

    public IReadOnlyList<string> Channels => ["SAMPLE::upper"];

    public async Task RunAsync(IDvcChannel channel, CancellationToken ct)
    {
        channel.Log($"opened {channel.Name}");
        await channel.WriteAsync(Encoding.UTF8.GetBytes("ready — send me text\n"), ct);   // server-initiated greeting

        byte[]? msg;
        while ((msg = await channel.ReadAsync(ct)) is not null)
        {
            var reply = Encoding.UTF8.GetString(msg).ToUpperInvariant();
            channel.Log($"{msg.Length} B in -> {reply.Length} B out");
            await channel.WriteAsync(Encoding.UTF8.GetBytes(reply), ct);
        }

        channel.Log("closed");
    }
}
