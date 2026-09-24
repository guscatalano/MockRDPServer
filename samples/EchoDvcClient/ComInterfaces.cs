using System.Runtime.InteropServices;

namespace EchoDvcClient;

// RDP Dynamic Virtual Channel COM interfaces, transcribed from tsvirtualchannels.idl. The IIDs and
// vtable method order are exact — the RDP client marshals against these. Every method is [PreserveSig]
// returning an HRESULT so no managed exception ever crosses the COM boundary.

[ComImport, Guid("A1230201-1439-4e62-a414-190d0ac3d40e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWTSPlugin
{
    [PreserveSig] int Initialize([MarshalAs(UnmanagedType.Interface)] IWTSVirtualChannelManager pChannelMgr);
    [PreserveSig] int Connected();
    [PreserveSig] int Disconnected(int dwDisconnectCode);
    [PreserveSig] int Terminated();
}

[ComImport, Guid("A1230205-d6a7-11d8-b9fd-000bdbd1f198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWTSVirtualChannelManager
{
    [PreserveSig] int CreateListener(
        [MarshalAs(UnmanagedType.LPStr)] string pszChannelName,
        uint uFlags,
        [MarshalAs(UnmanagedType.Interface)] IWTSListenerCallback pListenerCallback,
        [MarshalAs(UnmanagedType.Interface)] out IWTSListener ppListener);
}

[ComImport, Guid("A1230206-9a39-4d58-8674-cdb4dff4e73b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWTSListener
{
    [PreserveSig] int GetConfiguration(out IntPtr ppPropertyBag);
}

[ComImport, Guid("A1230203-d6a7-11d8-b9fd-000bdbd1f198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWTSListenerCallback
{
    [PreserveSig] int OnNewChannelConnection(
        [MarshalAs(UnmanagedType.Interface)] IWTSVirtualChannel pChannel,
        [MarshalAs(UnmanagedType.BStr)] string? data,
        [MarshalAs(UnmanagedType.Bool)] out bool pbAccept,
        [MarshalAs(UnmanagedType.Interface)] out IWTSVirtualChannelCallback ppCallback);
}

[ComImport, Guid("A1230204-d6a7-11d8-b9fd-000bdbd1f198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWTSVirtualChannelCallback
{
    [PreserveSig] int OnDataReceived(uint cbSize, IntPtr pBuffer);
    [PreserveSig] int OnClose();
}

[ComImport, Guid("A1230207-d6a7-11d8-b9fd-000bdbd1f198"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IWTSVirtualChannel
{
    [PreserveSig] int Write(uint cbSize, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] byte[] pBuffer, IntPtr pReserved);
    [PreserveSig] int Close();
}

// Standard COM class factory used to hand instances to the RDP client.
[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IClassFactory
{
    [PreserveSig] int CreateInstance(
        [MarshalAs(UnmanagedType.Interface)] object? pUnkOuter,
        ref Guid riid,
        out IntPtr ppvObject);
    [PreserveSig] int LockServer([MarshalAs(UnmanagedType.Bool)] bool fLock);
}
