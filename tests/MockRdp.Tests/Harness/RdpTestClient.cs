using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using MockRdp.Framing;
using MockRdp.Util;
using MockRdp.X224;

namespace MockRdp.Tests.Harness;

/// <summary>Result of an X.224 negotiation (Connection Confirm) from the mock server.</summary>
public sealed record NegResponse(bool IsFailure, RdpNegProtocol SelectedProtocol, RdpNegFailureCode FailureCode);

/// <summary>
/// In-process RDP conformance client. Grows one milestone at a time alongside the server:
/// M1 covers X.224 negotiation + TLS. Later milestones add MCS, capability exchange, and
/// PDU decoders (e.g. bitmap-update → framebuffer assertions) on top of the same instance.
/// </summary>
public sealed class RdpTestClient : IAsyncDisposable
{
    private readonly TcpClient _tcp = new();
    private Stream _stream = Stream.Null;
    private SslStream? _ssl;

    /// <summary>The active stream (TLS once upgraded, otherwise the raw socket).</summary>
    public Stream Stream => _ssl ?? _stream;

    public async Task ConnectAsync(IPEndPoint endpoint, CancellationToken ct = default)
    {
        await _tcp.ConnectAsync(endpoint, ct);
        _stream = _tcp.GetStream();
    }

    /// <summary>Sends an X.224 Connection Request offering the given security protocols.</summary>
    public async Task SendConnectionRequestAsync(RdpNegProtocol protocols, string? cookie = null, CancellationToken ct = default)
    {
        var w = new ByteWriter();
        int variableLen = 8 + (cookie is null ? 0 : Encoding.ASCII.GetByteCount(cookie) + 2);
        w.WriteUInt8((byte)(6 + variableLen)); // LI
        w.WriteUInt8(0xE0);                     // CR TPDU
        w.WriteUInt16BE(0);                     // dst-ref
        w.WriteUInt16BE(0);                     // src-ref
        w.WriteUInt8(0);                        // class
        if (cookie is not null)
        {
            w.WriteBytes(Encoding.ASCII.GetBytes(cookie));
            w.WriteUInt8(0x0D);
            w.WriteUInt8(0x0A);
        }
        w.WriteUInt8(0x01);                     // TYPE_RDP_NEG_REQ
        w.WriteUInt8(0x00);                     // flags
        w.WriteUInt16LE(8);                     // length
        w.WriteUInt32LE((uint)protocols);
        await WriteRawAsync(Tpkt.Wrap(w.AsSpan()), ct);
    }

    /// <summary>Reads and parses the X.224 Connection Confirm (Negotiation Response or Failure).</summary>
    public async Task<NegResponse> ReadConnectionConfirmAsync(CancellationToken ct = default)
    {
        var cc = await ReadTpktPayloadAsync(ct);
        if (cc.Length < 15 || cc[1] != 0xD0)
            throw new FormatException("Expected an X.224 Connection Confirm TPDU.");
        byte negType = cc[7];
        uint value = BitConverter.ToUInt32(cc, 11);
        return negType == 0x03
            ? new NegResponse(true, RdpNegProtocol.Rdp, (RdpNegFailureCode)value)
            : new NegResponse(false, (RdpNegProtocol)value, default);
    }

    /// <summary>Completes the TLS handshake as the client, trusting the mock's self-signed cert.</summary>
    public async Task UpgradeToTlsAsync(string targetHost = "mock-rdp", CancellationToken ct = default)
    {
        _ssl = new SslStream(_stream, leaveInnerStreamOpen: false, (_, _, _, _) => true);
        await _ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = targetHost }, ct);
    }

    public async Task WriteRawAsync(byte[] packet, CancellationToken ct = default)
    {
        await Stream.WriteAsync(packet, ct);
        await Stream.FlushAsync(ct);
    }

    /// <summary>Reads one TPKT-framed packet, returning the payload after the 4-byte header.</summary>
    public async Task<byte[]> ReadTpktPayloadAsync(CancellationToken ct = default)
    {
        var header = new byte[Tpkt.HeaderLength];
        await Stream.ReadExactlyAsync(header, ct);
        int total = Tpkt.ReadLength(header);
        var payload = new byte[total - Tpkt.HeaderLength];
        await Stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ssl is not null) await _ssl.DisposeAsync();
        _tcp.Dispose();
    }
}
