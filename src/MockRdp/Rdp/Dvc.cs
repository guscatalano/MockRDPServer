using System.Buffers.Binary;
using System.Text;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// Dynamic Virtual Channel protocol (MS-RDPEDYC / DRDYNVC), carried over the static
/// "drdynvc" virtual channel. Pure codec: builds the PDUs the mock sends (capabilities,
/// create request, data, close) and parses the ones it receives (capabilities response,
/// create response, data). Per-connection state (open channels, reassembly) lives in
/// <see cref="Server.RdpConnection"/>, mirroring how <see cref="Clipboard"/> is used.
///
/// Every PDU starts with one header byte: (Cmd &lt;&lt; 4) | (Sp &lt;&lt; 2) | cbId, where
/// cbId selects the ChannelId width (0→1, 1→2, 2→4 bytes) and, for DATA_FIRST, Sp
/// selects the Length field width the same way.
/// </summary>
public static class Dvc
{
    public enum Cmd : byte
    {
        Create = 0x01,
        DataFirst = 0x02,
        Data = 0x03,
        Close = 0x04,
        Capabilities = 0x05,
    }

    /// <summary>A decoded inbound DVC PDU. <see cref="Data"/> carries the command-specific
    /// body: the create-response bytes, or the (fragment of) channel data.</summary>
    public readonly record struct Message(Cmd Cmd, uint ChannelId, int Version, int TotalLength, byte[] Data);

    // Keep a DVC data PDU comfortably under the 1600-byte static-channel chunk limit.
    private const int MaxChunk = 1590;

    // ---- build (server -> client) -----------------------------------------

    /// <summary>DYNVC_CAPS_VERSION (version 1): the server's capability advertisement.</summary>
    public static byte[] BuildCapabilitiesV1() => BuildCapabilities(1);

    /// <summary>DYNVC_CAPS_RSP / DYNVC_CAPS_VERSION share a layout: cmd, pad, version(LE).</summary>
    public static byte[] BuildCapabilities(int version)
    {
        var w = new ByteWriter();
        w.WriteUInt8(Header(Cmd.Capabilities, 0, 0));
        w.WriteUInt8(0);                    // pad
        w.WriteUInt16LE((ushort)version);
        return w.ToArray();
    }

    /// <summary>DYNVC_CREATE_REQ: open a dynamic channel by name.</summary>
    public static byte[] BuildCreateRequest(uint channelId, string name)
    {
        var w = new ByteWriter();
        w.WriteUInt8(Header(Cmd.Create, 0, CbId(channelId)));
        WriteChannelId(w, channelId);
        w.WriteBytes(Encoding.ASCII.GetBytes(name));
        w.WriteUInt8(0);                    // null terminator
        return w.ToArray();
    }

    /// <summary>DYNVC_CREATE_RSP: the client's accept/reject of a create request
    /// (status 0 = success). Included so test clients can answer the mock.</summary>
    public static byte[] BuildCreateResponse(uint channelId, int creationStatus)
    {
        var w = new ByteWriter();
        w.WriteUInt8(Header(Cmd.Create, 0, CbId(channelId)));
        WriteChannelId(w, channelId);
        w.WriteUInt32LE((uint)creationStatus);
        return w.ToArray();
    }

    /// <summary>DYNVC_CLOSE.</summary>
    public static byte[] BuildClose(uint channelId)
    {
        var w = new ByteWriter();
        w.WriteUInt8(Header(Cmd.Close, 0, CbId(channelId)));
        WriteChannelId(w, channelId);
        return w.ToArray();
    }

    /// <summary>Frames channel data as one DYNVC_DATA, or DYNVC_DATA_FIRST + DYNVC_DATA
    /// fragments when it exceeds one chunk.</summary>
    public static IReadOnlyList<byte[]> BuildData(uint channelId, ReadOnlySpan<byte> data)
    {
        var pdus = new List<byte[]>();

        if (data.Length <= MaxChunk)
        {
            var w = new ByteWriter();
            w.WriteUInt8(Header(Cmd.Data, 0, CbId(channelId)));
            WriteChannelId(w, channelId);
            w.WriteBytes(data);
            pdus.Add(w.ToArray());
            return pdus;
        }

        // DATA_FIRST carries the total length; the continuations are plain DATA.
        int lenWidth = LenWidth(data.Length);
        var first = new ByteWriter();
        first.WriteUInt8(Header(Cmd.DataFirst, (byte)lenWidth, CbId(channelId)));
        WriteChannelId(first, channelId);
        WriteVarLen(first, lenWidth, data.Length);
        int take = Math.Min(MaxChunk, data.Length);
        first.WriteBytes(data[..take]);
        pdus.Add(first.ToArray());

        for (int off = take; off < data.Length; off += take)
        {
            take = Math.Min(MaxChunk, data.Length - off);
            var w = new ByteWriter();
            w.WriteUInt8(Header(Cmd.Data, 0, CbId(channelId)));
            WriteChannelId(w, channelId);
            w.WriteBytes(data.Slice(off, take));
            pdus.Add(w.ToArray());
        }
        return pdus;
    }

    // ---- parse (client -> server, plus test-client helpers) ---------------

    public static Message Parse(ReadOnlySpan<byte> pdu)
    {
        var r = new ByteReader(pdu);
        byte header = r.ReadUInt8();
        var cmd = (Cmd)((header >> 4) & 0x0F);
        int sp = (header >> 2) & 0x03;
        int cbId = header & 0x03;

        if (cmd == Cmd.Capabilities)
        {
            r.ReadUInt8();                  // pad
            return new Message(cmd, 0, r.ReadUInt16LE(), 0, []);
        }

        uint channelId = ReadChannelId(ref r, cbId);

        return cmd switch
        {
            // Create body is direction-dependent (response status vs. request name); hand
            // the raw bytes back and let CreationStatus/ChannelName interpret them.
            Cmd.Create => new Message(cmd, channelId, 0, 0, r.PeekRemaining().ToArray()),
            Cmd.DataFirst => new Message(cmd, channelId, 0, (int)ReadVarLen(ref r, sp), r.PeekRemaining().ToArray()),
            Cmd.Data => new Message(cmd, channelId, 0, 0, r.PeekRemaining().ToArray()),
            Cmd.Close => new Message(cmd, channelId, 0, 0, []),
            _ => new Message(cmd, channelId, 0, 0, r.PeekRemaining().ToArray()),
        };
    }

    /// <summary>Create-response status from a <see cref="Cmd.Create"/> message (server side).</summary>
    public static int CreationStatus(Message m) =>
        m.Data.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(m.Data) : 0;

    /// <summary>Channel name from a create <em>request</em> (test-client side).</summary>
    public static string ChannelName(Message m)
    {
        int nul = Array.IndexOf(m.Data, (byte)0);
        return Encoding.ASCII.GetString(m.Data, 0, nul < 0 ? m.Data.Length : nul);
    }

    // ---- header helpers ----------------------------------------------------

    private static byte Header(Cmd cmd, byte sp, int cbId) =>
        (byte)(((int)cmd << 4) | ((sp & 0x03) << 2) | (cbId & 0x03));

    private static int CbId(uint id) => id <= 0xFF ? 0 : id <= 0xFFFF ? 1 : 2;

    private static void WriteChannelId(ByteWriter w, uint id)
    {
        switch (CbId(id))
        {
            case 0: w.WriteUInt8((byte)id); break;
            case 1: w.WriteUInt16LE((ushort)id); break;
            default: w.WriteUInt32LE(id); break;
        }
    }

    private static uint ReadChannelId(ref ByteReader r, int cbId) => cbId switch
    {
        0 => r.ReadUInt8(),
        1 => r.ReadUInt16LE(),
        2 => r.ReadUInt32LE(),
        _ => throw new FormatException("Reserved cbId (3) in DVC header."),
    };

    private static int LenWidth(int len) => len <= 0xFF ? 0 : len <= 0xFFFF ? 1 : 2;

    private static void WriteVarLen(ByteWriter w, int width, int len)
    {
        switch (width)
        {
            case 0: w.WriteUInt8((byte)len); break;
            case 1: w.WriteUInt16LE((ushort)len); break;
            default: w.WriteUInt32LE((uint)len); break;
        }
    }

    private static uint ReadVarLen(ref ByteReader r, int width) => width switch
    {
        0 => r.ReadUInt8(),
        1 => r.ReadUInt16LE(),
        2 => r.ReadUInt32LE(),
        _ => throw new FormatException("Reserved length width in DVC DATA_FIRST."),
    };
}
