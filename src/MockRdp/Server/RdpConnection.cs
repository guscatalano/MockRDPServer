using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MockRdp.Framing;
using MockRdp.Mcs;
using MockRdp.Util;
using MockRdp.X224;

namespace MockRdp.Server;

/// <summary>
/// Drives one client connection through the RDP connection sequence. Currently
/// implements M1: X.224 negotiation and the TLS upgrade. Later milestones extend
/// <see cref="RunAsync"/> past <see cref="ConnectionState.TlsUp"/>.
/// </summary>
public sealed class RdpConnection(TcpClient tcp, X509Certificate2 cert, ILogger log)
{
    private Stream _stream = tcp.GetStream();

    public ConnectionState State { get; private set; } = ConnectionState.Initial;

    public async Task RunAsync(CancellationToken ct)
    {
        State = ConnectionState.Negotiating;

        var crTpdu = await ReadTpktAsync(ct);
        if (crTpdu is null)
        {
            log.LogWarning("Connection closed before X.224 Connection Request.");
            return;
        }
        log.LogTrace("X.224 CR received ({Len} bytes):\n{Hex}", crTpdu.Length, HexDump.Format(crTpdu));

        X224ConnectionRequest cr;
        try
        {
            cr = Cotp.ParseConnectionRequest(crTpdu);
        }
        catch (FormatException ex)
        {
            log.LogWarning(ex, "Malformed X.224 Connection Request.");
            return;
        }

        log.LogInformation("Connection Request: cookie={Cookie} requested={Protocols}",
            cr.Cookie ?? "(none)", cr.HasNegReq ? cr.RequestedProtocols : "(no negReq)");

        // M1 policy: TLS only. Reject anything that does not offer PROTOCOL_SSL.
        if (!cr.HasNegReq || (cr.RequestedProtocols & RdpNegProtocol.Ssl) == 0)
        {
            log.LogWarning("Client did not offer TLS; sending negotiation failure (SSL_REQUIRED_BY_SERVER).");
            await WriteAsync(Cotp.BuildConnectionConfirmFailure(RdpNegFailureCode.SslRequiredByServer), ct);
            return;
        }

        await WriteAsync(Cotp.BuildConnectionConfirm(RdpNegProtocol.Ssl), ct);
        log.LogInformation("Sent Connection Confirm selecting PROTOCOL_SSL; starting TLS handshake.");

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = cert,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            }, ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TLS handshake failed.");
            return;
        }

        _stream = ssl;
        State = ConnectionState.TlsUp;
        log.LogInformation("TLS established: {Protocol} / {Cipher}.", ssl.SslProtocol, ssl.NegotiatedCipherSuite);

        await RunMcsAsync(ct);
    }

    /// <summary>M2: MCS Connect-Initial/Response, then Erect Domain / Attach User / Channel Join.</summary>
    private async Task RunMcsAsync(CancellationToken ct)
    {
        State = ConnectionState.McsConnect;

        var initialPacket = await ReadTpktAsync(ct);
        if (initialPacket is null) { log.LogWarning("Closed before MCS Connect-Initial."); return; }

        var userData = McsPdu.ReadConnectInitialUserData(Cotp.StripDataTpdu(initialPacket));
        int channelCount = Gcc.ReadRequestedChannelCount(userData);
        ushort userChannelId = (ushort)(Gcc.FirstVirtualChannelId + channelCount);
        log.LogInformation("MCS Connect-Initial: {Count} virtual channels requested.", channelCount);

        await WriteAsync(McsPdu.BuildConnectResponse(channelCount, (uint)RdpNegProtocol.Ssl), ct);
        log.LogInformation("Sent MCS Connect-Response (I/O=1003, VCs=1004..{Last}, user={User}).",
            Gcc.FirstVirtualChannelId + channelCount - 1, userChannelId);

        State = ConnectionState.McsChannelJoin;
        int joined = 0;
        int expectedJoins = channelCount + 2; // user channel + I/O channel + each virtual channel

        while (true)
        {
            var packet = await ReadTpktAsync(ct);
            if (packet is null) { log.LogWarning("Closed during MCS channel join."); return; }
            var mcs = Cotp.StripDataTpdu(packet);

            switch (McsPdu.ClassifyDomainPdu(mcs))
            {
                case McsDomainPdu.ErectDomainRequest:
                    log.LogDebug("Erect Domain Request.");
                    break;

                case McsDomainPdu.AttachUserRequest:
                    await WriteAsync(McsPdu.BuildAttachUserConfirm(userChannelId), ct);
                    log.LogDebug("Attach User Request → confirmed user channel {User}.", userChannelId);
                    break;

                case McsDomainPdu.ChannelJoinRequest:
                    var (initiator, channelId) = McsPdu.ParseChannelJoinRequest(mcs);
                    await WriteAsync(McsPdu.BuildChannelJoinConfirm(initiator, channelId), ct);
                    joined++;
                    log.LogDebug("Channel Join Request {Channel} → confirmed ({Joined}/{Expected}).",
                        channelId, joined, expectedJoins);
                    break;

                case McsDomainPdu.SendDataRequest:
                    log.LogInformation(
                        "MCS complete: {Joined} channels joined; first Send Data (Client Info) received. [M2 complete — licensing not yet implemented]",
                        joined);
                    return;

                default:
                    log.LogWarning("Unexpected MCS PDU 0x{Byte:X2} during channel join.", mcs.Length > 0 ? mcs[0] : 0);
                    return;
            }
        }
    }

    /// <summary>Reads one TPKT-framed packet and returns the X.224 TPDU (payload after the 4-byte header).</summary>
    private async Task<byte[]?> ReadTpktAsync(CancellationToken ct)
    {
        var header = new byte[Tpkt.HeaderLength];
        try
        {
            await _stream.ReadExactlyAsync(header, ct);
        }
        catch (EndOfStreamException)
        {
            return null;
        }

        int total = Tpkt.ReadLength(header);
        if (total < Tpkt.HeaderLength)
            throw new FormatException($"Invalid TPKT length {total}.");

        var payload = new byte[total - Tpkt.HeaderLength];
        await _stream.ReadExactlyAsync(payload, ct);
        return payload;
    }

    private async Task WriteAsync(byte[] packet, CancellationToken ct)
    {
        log.LogTrace("Sending ({Len} bytes):\n{Hex}", packet.Length, HexDump.Format(packet));
        await _stream.WriteAsync(packet, ct);
        await _stream.FlushAsync(ct);
    }
}
