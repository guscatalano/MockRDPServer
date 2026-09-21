using MockRdp.Rdp;
using Xunit;

namespace MockRdp.Tests;

/// <summary>The mock's RDPeek diagnostics responder: Hello → Capabilities, Ping → Ping, with the
/// [4-byte LE length][Envelope] framing and request_id correlation preserved.</summary>
public class DiagResponderTests
{
    // ── tiny protobuf encoder (matches the wire the plugin/agent use) ──
    private static List<byte> Varint(ulong v) { var b = new List<byte>(); while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; } b.Add((byte)v); return b; }
    private static byte[] VarintField(int f, ulong v) { var b = Varint((ulong)(f << 3)); b.AddRange(Varint(v)); return b.ToArray(); }
    private static byte[] LenField(int f, byte[] msg) { var b = Varint((ulong)(f << 3 | 2)); b.AddRange(Varint((ulong)msg.Length)); b.AddRange(msg); return b.ToArray(); }
    private static byte[] Cat(params byte[][] parts) { var b = new List<byte>(); foreach (var p in parts) b.AddRange(p); return b.ToArray(); }
    private static byte[] Frame(byte[] env) => Cat([(byte)env.Length, (byte)(env.Length >> 8), (byte)(env.Length >> 16), (byte)(env.Length >> 24)], env);

    // find the body oneof field number in a response envelope (skips request_id=1, utc_ticks=2)
    private static (ulong ReqId, int BodyField) Parse(byte[] framed)
    {
        var env = framed.AsSpan(4);
        int pos = 0; ulong reqId = 0; int body = 0;
        while (pos < env.Length)
        {
            ulong tag = ReadVarint(env, ref pos);
            int field = (int)(tag >> 3), wire = (int)(tag & 7);
            if (field == 1) reqId = ReadVarint(env, ref pos);
            else if (wire == 0) ReadVarint(env, ref pos);
            else if (wire == 2) { int l = (int)ReadVarint(env, ref pos); if (field is not 1 and not 2) body = field; pos += l; }
            else break;
        }
        return (reqId, body);
    }
    private static ulong ReadVarint(ReadOnlySpan<byte> s, ref int pos) { ulong v = 0; int sh = 0; while (pos < s.Length) { byte b = s[pos++]; v |= (ulong)(b & 0x7F) << sh; if ((b & 0x80) == 0) break; sh += 7; } return v; }

    [Fact]
    public void Hello_IsAnsweredWithCapabilities_EchoingRequestId()
    {
        var hello = VarintField(1, 1);                                   // Hello.protocol_version = 1
        var env = Cat(VarintField(1, 42), VarintField(2, 123456), LenField(10, hello));
        var resp = DiagResponder.Respond(Frame(env));

        Assert.NotNull(resp);
        var (reqId, body) = Parse(resp!);
        Assert.Equal(42UL, reqId);
        Assert.Equal(11, body);   // Capabilities
    }

    [Fact]
    public void Ping_IsEchoedBackAsPing()
    {
        var ping = VarintField(1, 7);                                    // Ping.sequence_number = 7
        var env = Cat(VarintField(1, 99), LenField(20, ping));
        var resp = DiagResponder.Respond(Frame(env));

        Assert.NotNull(resp);
        var (reqId, body) = Parse(resp!);
        Assert.Equal(99UL, reqId);
        Assert.Equal(20, body);   // Ping
    }

    [Fact]
    public void UnknownRequest_GetsError()
    {
        var env = Cat(VarintField(1, 5), LenField(50, VarintField(1, 0))); // ProcessListRequest (field 50)
        var resp = DiagResponder.Respond(Frame(env));

        Assert.NotNull(resp);
        Assert.Equal(12, Parse(resp!).BodyField);   // Error
    }
}
