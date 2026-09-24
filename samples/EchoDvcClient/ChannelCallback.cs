using System.Runtime.InteropServices;
using System.Text;

namespace EchoDvcClient;

/// <summary>
/// The per-connection read/write core — the whole point of the sample. When the channel opens it
/// sends a line to the server; on every inbound message it logs what came back. Against the
/// UppercaseDvcPlugin server that means: send "hello…", receive "HELLO…".
/// </summary>
[ComVisible(true)]
internal sealed class ChannelCallback : IWTSVirtualChannelCallback
{
    private readonly IWTSVirtualChannel _channel;

    public ChannelCallback(IWTSVirtualChannel channel)
    {
        _channel = channel;

        // Send a line as soon as the channel is up (~WTSVirtualChannelWrite).
        var hello = Encoding.UTF8.GetBytes("hello from the client sample");
        int hr = _channel.Write((uint)hello.Length, hello, IntPtr.Zero);
        Logger.Log($"channel open — sent 'hello from the client sample' (Write hr=0x{hr:X8})");
    }

    // ~WTSVirtualChannelRead: the RDP client hands us the received bytes in unmanaged memory.
    public int OnDataReceived(uint cbSize, IntPtr pBuffer)
    {
        var buf = new byte[cbSize];
        Marshal.Copy(pBuffer, buf, 0, (int)cbSize);
        Logger.Log($"received: {Encoding.UTF8.GetString(buf).TrimEnd()}");
        return 0; // S_OK
    }

    public int OnClose()
    {
        Logger.Log("channel closed");
        return 0;
    }
}
