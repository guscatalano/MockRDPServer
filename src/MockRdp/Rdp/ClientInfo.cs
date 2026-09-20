using System.Text;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// Parses the interesting fields of the Client Info PDU (TS_INFO_PACKET,
/// MS-RDPBCGR 2.2.1.11.1.1): the alternate shell (the RDP "start program") and working
/// directory, so a harness can confirm what a client requested without a real session.
/// Assumes Enhanced (TLS) security — the info packet is not additionally RDP-encrypted.
/// Best-effort and defensive: a parse it can't make returns empty fields rather than throwing.
/// </summary>
public static class ClientInfo
{
    public readonly record struct Info(string Domain, string User, string AlternateShell, string WorkingDir, bool Unicode);

    private const uint InfoUnicode = 0x00000010;

    public static Info Parse(ReadOnlySpan<byte> sendDataPayload)
    {
        try
        {
            var r = new ByteReader(sendDataPayload);
            r.ReadUInt16LE();               // Security Header: flags
            r.ReadUInt16LE();               // Security Header: flagsHi
            r.ReadUInt32LE();               // CodePage
            uint flags = r.ReadUInt32LE();  // INFO_* flags
            bool unicode = (flags & InfoUnicode) != 0;
            int cbDomain = r.ReadUInt16LE();
            int cbUser   = r.ReadUInt16LE();
            int cbPass   = r.ReadUInt16LE();
            int cbAlt    = r.ReadUInt16LE();
            int cbWork   = r.ReadUInt16LE();

            int nul = unicode ? 2 : 1;      // cb* exclude the trailing null terminator
            string domain = ReadStr(ref r, cbDomain, unicode); r.Skip(nul);
            string user   = ReadStr(ref r, cbUser, unicode);   r.Skip(nul);
            r.Skip(cbPass + nul);           // password — never read or logged
            string alt    = ReadStr(ref r, cbAlt, unicode);    r.Skip(nul);
            string work   = ReadStr(ref r, cbWork, unicode);
            return new Info(domain, user, alt, work, unicode);
        }
        catch (FormatException)
        {
            return new Info("", "", "", "", false);
        }
    }

    private static string ReadStr(ref ByteReader r, int cb, bool unicode)
    {
        if (cb <= 0 || cb > r.Remaining) return "";
        var bytes = r.ReadBytes(cb);
        return (unicode ? Encoding.Unicode : Encoding.ASCII).GetString(bytes);
    }
}
