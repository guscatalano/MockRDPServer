using System.Buffers.Binary;
using System.Text;
using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// File System Virtual Channel Extension (MS-RDPEFS), carried over the static "rdpdr"
/// channel. The mock implements the server side of the init handshake plus a read-only
/// file pull, so it can read files back from the client's redirected drives (\\tsclient) —
/// verifying that redirected content is actually reachable, not just requested.
///
/// Pure codec; per-connection state lives in <see cref="Server.RdpConnection"/>.
/// </summary>
public static class Rdpdr
{
    // RDPDR_HEADER Component.
    private const ushort CtypCore = 0x4472;

    // PacketId values (RDPDR_HEADER).
    public const ushort ServerAnnounce     = 0x496E;
    public const ushort ClientIdConfirm    = 0x4343; // client announce reply AND server clientid confirm
    public const ushort ClientName         = 0x434E;
    public const ushort ServerCapability   = 0x5350;
    public const ushort ClientCapability   = 0x4350;
    public const ushort UserLoggedOn       = 0x554C;
    public const ushort DeviceListAnnounce = 0x4441;
    public const ushort DeviceReply        = 0x6472;
    public const ushort DeviceIoRequest    = 0x4952;
    public const ushort DeviceIoCompletion = 0x4943;

    public const uint DeviceTypeFilesystem = 0x00000008; // RDPDR_DTYP_FILESYSTEM (0x04 is PRINT)
    private const uint IrpMjCreate = 0x00000000;
    private const uint IrpMjClose  = 0x00000002;
    private const uint IrpMjRead   = 0x00000003;

    public static ushort PacketId(ReadOnlySpan<byte> pdu) =>
        pdu.Length >= 4 ? BinaryPrimitives.ReadUInt16LittleEndian(pdu[2..]) : (ushort)0;

    private static void Header(ByteWriter w, ushort packetId)
    {
        w.WriteUInt16LE(CtypCore);
        w.WriteUInt16LE(packetId);
    }

    // ---- server -> client PDUs --------------------------------------------

    public static byte[] ServerAnnounceReq(uint clientId)
    {
        var w = new ByteWriter();
        Header(w, ServerAnnounce);
        w.WriteUInt16LE(1);        // VersionMajor
        w.WriteUInt16LE(12);       // VersionMinor
        w.WriteUInt32LE(clientId);
        return w.ToArray();
    }

    public static byte[] ServerClientIdConfirm(uint clientId)
    {
        var w = new ByteWriter();
        Header(w, ClientIdConfirm);
        w.WriteUInt16LE(1);
        w.WriteUInt16LE(12);
        w.WriteUInt32LE(clientId);
        return w.ToArray();
    }

    public static byte[] UserLoggedOnPdu()
    {
        var w = new ByteWriter();
        Header(w, UserLoggedOn);
        return w.ToArray();
    }

    /// <summary>General + Drive capability sets — enough for filesystem read I/O.</summary>
    public static byte[] ServerCapabilityReq()
    {
        var w = new ByteWriter();
        Header(w, ServerCapability);
        w.WriteUInt16LE(2);        // numCapabilities (GENERAL + DRIVE)
        w.WriteUInt16LE(0);        // padding

        // GENERAL_CAPS_SET (CAP_GENERAL_TYPE = 1), version 2, length 44.
        w.WriteUInt16LE(1);
        w.WriteUInt16LE(44);
        w.WriteUInt32LE(2);
        w.WriteUInt32LE(0);            // osType
        w.WriteUInt32LE(0);            // osVersion
        w.WriteUInt16LE(1);            // protocolMajorVersion
        w.WriteUInt16LE(12);           // protocolMinorVersion
        w.WriteUInt32LE(0x0000FFFF);   // ioCode1 — advertise all IRP major functions
        w.WriteUInt32LE(0);            // ioCode2
        w.WriteUInt32LE(0x00000007);   // extendedPDU — device remove + display name + user logged on
        w.WriteUInt32LE(0);            // extraFlags1
        w.WriteUInt32LE(0);            // extraFlags2
        w.WriteUInt32LE(0);            // SpecialTypeDeviceCap

        // DRIVE_CAPS_SET (CAP_DRIVE_TYPE = 4), version 2, length 8.
        w.WriteUInt16LE(4);
        w.WriteUInt16LE(8);
        w.WriteUInt32LE(2);
        return w.ToArray();
    }

    public static byte[] DeviceAnnounceResponse(uint deviceId, uint resultCode)
    {
        var w = new ByteWriter();
        Header(w, DeviceReply);
        w.WriteUInt32LE(deviceId);
        w.WriteUInt32LE(resultCode);
        return w.ToArray();
    }

    public static byte[] CreateRequest(uint deviceId, uint completionId, string devicePath)
    {
        var w = new ByteWriter();
        IoRequestHeader(w, deviceId, fileId: 0, completionId, IrpMjCreate);
        w.WriteUInt32LE(0x00120089);   // DesiredAccess = FILE_GENERIC_READ
        w.WriteUInt32LE(0); w.WriteUInt32LE(0); // AllocationSize (8)
        w.WriteUInt32LE(0);            // FileAttributes
        w.WriteUInt32LE(0x00000001);   // SharedAccess = FILE_SHARE_READ
        w.WriteUInt32LE(0x00000001);   // CreateDisposition = FILE_OPEN
        w.WriteUInt32LE(0x00000060);   // CreateOptions = NON_DIRECTORY_FILE | SYNCHRONOUS_IO_NONALERT
        var path = Encoding.Unicode.GetBytes(devicePath + "\0");
        w.WriteUInt32LE((uint)path.Length); // PathLength (includes null terminator)
        w.WriteBytes(path);
        return w.ToArray();
    }

    public static byte[] ReadRequest(uint deviceId, uint fileId, uint completionId, uint length, ulong offset)
    {
        var w = new ByteWriter();
        IoRequestHeader(w, deviceId, fileId, completionId, IrpMjRead);
        w.WriteUInt32LE(length);
        w.WriteUInt32LE((uint)(offset & 0xFFFFFFFF));
        w.WriteUInt32LE((uint)(offset >> 32));
        for (int i = 0; i < 20; i++) w.WriteUInt8(0); // Padding
        return w.ToArray();
    }

    public static byte[] CloseRequest(uint deviceId, uint fileId, uint completionId)
    {
        var w = new ByteWriter();
        IoRequestHeader(w, deviceId, fileId, completionId, IrpMjClose);
        for (int i = 0; i < 32; i++) w.WriteUInt8(0); // Padding
        return w.ToArray();
    }

    private static void IoRequestHeader(ByteWriter w, uint deviceId, uint fileId, uint completionId, uint major)
    {
        Header(w, DeviceIoRequest);
        w.WriteUInt32LE(deviceId);
        w.WriteUInt32LE(fileId);
        w.WriteUInt32LE(completionId);
        w.WriteUInt32LE(major);
        w.WriteUInt32LE(0);           // MinorFunction
    }

    // ---- client -> server parsing -----------------------------------------

    public readonly record struct Device(uint Type, uint Id, string DosName);

    public static List<Device> ParseDeviceList(ReadOnlySpan<byte> pdu)
    {
        var list = new List<Device>();
        var r = new ByteReader(pdu);
        r.Skip(4);                        // RDPDR_HEADER
        uint count = r.ReadUInt32LE();
        for (uint i = 0; i < count && r.Remaining >= 20; i++)
        {
            uint type = r.ReadUInt32LE();
            uint id = r.ReadUInt32LE();
            var dos = r.ReadBytes(8);
            int z = dos.IndexOf((byte)0); if (z < 0) z = 8;
            string name = Encoding.ASCII.GetString(dos[..z]);
            uint ddl = r.ReadUInt32LE();
            if (ddl > 0 && r.Remaining >= ddl) r.Skip((int)ddl);
            list.Add(new Device(type, id, name));
        }
        return list;
    }

    public readonly record struct Completion(uint DeviceId, uint CompletionId, uint IoStatus, byte[] Rest);

    public static Completion ParseIoCompletion(ReadOnlySpan<byte> pdu)
    {
        var r = new ByteReader(pdu);
        r.Skip(4);                        // RDPDR_HEADER
        uint dev = r.ReadUInt32LE();
        uint comp = r.ReadUInt32LE();
        uint status = r.ReadUInt32LE();
        return new Completion(dev, comp, status, r.PeekRemaining().ToArray());
    }

    /// <summary>FileId from a CREATE completion (DR_CREATE_RSP: FileId, Information).</summary>
    public static uint CreateFileId(byte[] rest) =>
        rest.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(rest) : 0;

    /// <summary>Data from a READ completion (DR_READ_RSP: Length, ReadData).</summary>
    public static ReadOnlySpan<byte> ReadData(byte[] rest)
    {
        if (rest.Length < 4) return default;
        uint len = BinaryPrimitives.ReadUInt32LittleEndian(rest);
        int n = Math.Min((int)len, rest.Length - 4);
        return rest.AsSpan(4, n);
    }
}
