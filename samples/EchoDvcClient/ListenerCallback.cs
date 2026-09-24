using System.Runtime.InteropServices;

namespace EchoDvcClient;

/// <summary>Accepts the incoming channel connection and hands back a per-connection callback.</summary>
[ComVisible(true)]
internal sealed class ListenerCallback : IWTSListenerCallback
{
    public int OnNewChannelConnection(
        IWTSVirtualChannel pChannel,
        string? data,
        out bool pbAccept,
        out IWTSVirtualChannelCallback ppCallback)
    {
        Logger.Log("OnNewChannelConnection — accepting");
        pbAccept = true;
        ppCallback = new ChannelCallback(pChannel);
        return 0; // S_OK
    }
}
