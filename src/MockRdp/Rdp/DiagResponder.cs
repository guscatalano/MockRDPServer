using MockRdp.Util;

namespace MockRdp.Rdp;

/// <summary>
/// A minimal responder for the RDPeek diagnostics protocol (Dvc.Diag / proto/diag.proto) so the
/// mock is a real test peer for the RDPeek plugin instead of just echoing. Frames are
/// <c>[4-byte LE length][Envelope]</c>; the handshake is client <c>Hello</c> (field 10) → agent
/// <c>Capabilities</c> (field 11). We answer Hello with Capabilities, Ping with Ping, and anything
/// else with an Error(NOT_SUPPORTED) — hand-rolled protobuf, no generated types.
/// </summary>
public static class DiagResponder
{
    // Envelope oneof field numbers.
    private const int FHello = 10, FCapabilities = 11, FError = 12, FPing = 20;

    /// <summary>Given one framed request, returns a framed response, or null if it can't parse it.</summary>
    public static byte[]? Respond(ReadOnlySpan<byte> framed, string agentBuild = "mock-rdp diag responder")
    {
        if (framed.Length < 4) return null;
        int len = framed[0] | framed[1] << 8 | framed[2] << 16 | framed[3] << 24;
        if (len <= 0 || 4 + len > framed.Length) return null;
        var env = framed.Slice(4, len);

        ulong requestId = 0;
        int bodyField = 0;
        uint helloVersion = 1;
        byte[]? pingBody = null;

        int pos = 0;
        while (pos < env.Length)
        {
            ulong tag = ReadVarint(env, ref pos);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            switch (field)
            {
                case 1: requestId = ReadVarint(env, ref pos); break;                 // request_id
                case 2: ReadVarint(env, ref pos); break;                             // utc_ticks (ignore)
                case FHello:
                    bodyField = FHello;
                    var hello = ReadLenDelim(env, ref pos);
                    helloVersion = ReadUint32Field(hello, 1);                          // Hello.protocol_version
                    break;
                case FPing:
                    bodyField = FPing;
                    pingBody = ReadLenDelim(env, ref pos).ToArray();
                    break;
                default:
                    if (field >= 10 && wire == 2) bodyField = field;   // a oneof body we don't specifically handle
                    SkipField(env, ref pos, wire);
                    break;
            }
        }

        return bodyField switch
        {
            FHello => Frame(BuildEnvelope(requestId, FCapabilities, BuildCapabilities(helloVersion, agentBuild))),
            FPing  => pingBody is null ? null : Frame(BuildEnvelope(requestId, FPing, pingBody)),
            0      => null,                                                            // no recognised body
            _      => Frame(BuildEnvelope(requestId, FError, BuildError(1, "not supported by the mock"))),
        };
    }

    // ── message builders ────────────────────────────────────────────────────

    private static byte[] BuildCapabilities(uint version, string agentBuild)
    {
        var w = new ByteWriter();
        WriteVarintField(w, 1, version);                 // protocol_version
        WriteStringField(w, 2, agentBuild);              // agent_build
        WriteBoolField(w, 10, true);                     // sysinfo
        WriteBoolField(w, 11, true);                     // counters
        WriteBoolField(w, 12, true);                     // process_list
        WriteVarintField(w, 21, 64 * 1024);              // max_chunk_bytes
        return w.ToArray();
    }

    private static byte[] BuildError(uint code, string message)
    {
        var w = new ByteWriter();
        WriteVarintField(w, 1, code);
        WriteStringField(w, 2, message);
        return w.ToArray();
    }

    private static byte[] BuildEnvelope(ulong requestId, int bodyField, ReadOnlySpan<byte> body)
    {
        var w = new ByteWriter();
        WriteVarintField(w, 1, requestId);               // request_id
        WriteVarintField(w, 2, (ulong)DateTime.UtcNow.Ticks); // utc_ticks
        WriteTag(w, bodyField, 2);                        // body (length-delimited)
        WriteVarint(w, (ulong)body.Length);
        w.WriteBytes(body);
        return w.ToArray();
    }

    private static byte[] Frame(byte[] envelope)
    {
        var w = new ByteWriter();
        w.WriteBytes([(byte)envelope.Length, (byte)(envelope.Length >> 8), (byte)(envelope.Length >> 16), (byte)(envelope.Length >> 24)]);
        w.WriteBytes(envelope);
        return w.ToArray();
    }

    // ── protobuf primitives ───────────────────────────────────────────────────

    private static void WriteTag(ByteWriter w, int field, int wire) => WriteVarint(w, (ulong)(field << 3 | wire));
    private static void WriteVarintField(ByteWriter w, int field, ulong v) { WriteTag(w, field, 0); WriteVarint(w, v); }
    private static void WriteBoolField(ByteWriter w, int field, bool b) { WriteTag(w, field, 0); WriteVarint(w, b ? 1UL : 0UL); }
    private static void WriteStringField(ByteWriter w, int field, string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s);
        WriteTag(w, field, 2);
        WriteVarint(w, (ulong)bytes.Length);
        w.WriteBytes(bytes);
    }

    private static void WriteVarint(ByteWriter w, ulong v)
    {
        while (v >= 0x80) { w.WriteUInt8((byte)(v | 0x80)); v >>= 7; }
        w.WriteUInt8((byte)v);
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> s, ref int pos)
    {
        ulong v = 0; int shift = 0;
        while (pos < s.Length)
        {
            byte b = s[pos++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }

    private static ReadOnlySpan<byte> ReadLenDelim(ReadOnlySpan<byte> s, ref int pos)
    {
        int len = (int)ReadVarint(s, ref pos);
        if (len < 0 || pos + len > s.Length) { pos = s.Length; return default; }
        var slice = s.Slice(pos, len);
        pos += len;
        return slice;
    }

    private static void SkipField(ReadOnlySpan<byte> s, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: ReadVarint(s, ref pos); break;      // varint
            case 1: pos += 8; break;                    // 64-bit
            case 2: ReadLenDelim(s, ref pos); break;    // length-delimited
            case 5: pos += 4; break;                    // 32-bit
            default: pos = s.Length; break;
        }
    }

    /// <summary>Reads a uint32 (varint) field from a message, or 0 if absent.</summary>
    private static uint ReadUint32Field(ReadOnlySpan<byte> msg, int wantField)
    {
        int pos = 0;
        while (pos < msg.Length)
        {
            ulong tag = ReadVarint(msg, ref pos);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (field == wantField && wire == 0) return (uint)ReadVarint(msg, ref pos);
            SkipField(msg, ref pos, wire);
        }
        return 0;
    }
}
