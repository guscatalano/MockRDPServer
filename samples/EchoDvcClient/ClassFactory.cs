using System.Runtime.InteropServices;

namespace EchoDvcClient;

/// <summary>IClassFactory that hands <see cref="EchoClientPlugin"/> instances to the RDP client.</summary>
[ComVisible(true)]
internal sealed class PluginClassFactory : IClassFactory
{
    private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    public int CreateInstance(object? pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter is not null) return CLASS_E_NOAGGREGATION;

        var plugin = new EchoClientPlugin();
        IntPtr pUnk = Marshal.GetIUnknownForObject(plugin);
        try { return Marshal.QueryInterface(pUnk, ref riid, out ppvObject); }
        finally { Marshal.Release(pUnk); }   // the QI'd reference keeps the object alive
    }

    public int LockServer(bool fLock) => 0; // S_OK
}
