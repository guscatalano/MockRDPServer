using System.Windows.Forms;

namespace RdpAxClient;

/// <summary>
/// Minimal ActiveX host for the mstscax RDP client control. AxHost sites the control in a
/// WinForms window; <see cref="Ocx"/> returns the underlying COM object, which we drive by
/// late binding (dynamic) so no generated interop assembly is required.
/// </summary>
internal sealed class RdpAxControl(string clsid) : AxHost(clsid)
{
    public object Ocx => GetOcx();
}
