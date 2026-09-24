using System.Runtime.InteropServices;

namespace EchoDvcClient;

/// <summary>
/// The COM object mstsc instantiates (IWTSPlugin). On Initialize it creates a listener for the shared
/// DVC channel; the per-connection work happens in <see cref="ChannelCallback"/>. The channel name
/// must match the server-side plugin (samples/UppercaseDvcPlugin advertises "SAMPLE::upper").
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
[Guid(PluginHost.ClsidString)]
internal sealed class EchoClientPlugin : IWTSPlugin
{
    // The DVC channel to listen on — the server opens it, this plugin accepts it. Keep in sync with
    // the server-side sample's Channels.
    public const string Channel = "SAMPLE::upper";

    private IWTSListener? _listener;

    public int Initialize(IWTSVirtualChannelManager pChannelMgr)
    {
        Logger.Log("IWTSPlugin.Initialize");
        int hr = pChannelMgr.CreateListener(Channel, 0, new ListenerCallback(), out var listener);
        Logger.Log($"CreateListener('{Channel}') hr=0x{hr:X8}");
        if (hr >= 0) _listener = listener;
        return hr >= 0 ? 0 : hr;
    }

    public int Connected() { Logger.Log("IWTSPlugin.Connected"); return 0; }
    public int Disconnected(int code) { Logger.Log($"IWTSPlugin.Disconnected code={code}"); return 0; }

    public int Terminated()
    {
        Logger.Log("IWTSPlugin.Terminated");
        PluginHost.Shutdown.Set();
        return 0;
    }
}
