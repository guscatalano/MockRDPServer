using System.Buffers.Binary;
using System.Linq;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Tests.Harness;
using MockRdp.X224;
using Xunit;

namespace MockRdp.Tests;

/// <summary>
/// End-to-end Standard RDP Security (b1) with a synthetic encrypting client, so the RC4 / cert /
/// Security-Exchange / salted-MAC path is validated in CI without a real client. The client offers
/// 128-bit RC4, reads the server's proprietary cert + random, does the Security Exchange, and encrypts
/// the Client Info, Confirm Active and Font List PDUs on one RC4 stream — the server must decrypt all
/// of them correctly (matching keys + stream continuity) to reach finalization.
/// </summary>
public class EncryptedHandshakeTests
{
    private static CancellationToken Timeout => new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token;

    // 128-bit is additionally verified against real FreeRDP; 40/56-bit are locked here for regression
    // (server and client agree on the salted key schedule), the paths FreeRDP couldn't confirm.
    [Theory]
    [InlineData(StandardSecurity.Method128Bit)]
    [InlineData(StandardSecurity.Method56Bit)]
    [InlineData(StandardSecurity.Method40Bit)]
    public async Task StandardRdpSecurity_CompletesEncryptedActivation(uint method)
    {
        using var server = new MockServerFixture(desktop: true);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        // Negotiate PROTOCOL_RDP offering just this RC4 method (no TLS on this path).
        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Rdp, ct: ct);
        Assert.Equal(RdpNegProtocol.Rdp, (await client.ReadConnectionConfirmAsync(ct)).SelectedProtocol);

        await client.WriteRawAsync(McsClient.BuildConnectInitial(method, "cliprdr", "drdynvc"), ct);
        var connectResp = await client.ReadTpktPayloadAsync(ct);

        // Derive keys from the server's cert + random (proves the server advertised them correctly).
        var crypto = new RdpCryptoClient(connectResp, method);
        var (io, ids) = McsClient.ParseConnectResponseNetwork(connectResp);

        await client.WriteRawAsync(McsClient.ErectDomainRequest(), ct);
        await client.WriteRawAsync(McsClient.AttachUserRequest(), ct);
        ushort user = McsClient.ParseAttachUserConfirm(await client.ReadTpktPayloadAsync(ct));
        foreach (var ch in new[] { user, io }.Concat(ids))
        {
            await client.WriteRawAsync(McsClient.ChannelJoinRequest(user, ch), ct);
            await client.ReadTpktPayloadAsync(ct);
        }

        // Security Exchange (encrypted client random), then the encrypted Client Info PDU.
        await client.WriteRawAsync(crypto.SecurityExchange(user), ct);
        await client.WriteRawAsync(crypto.EncryptClientInfo(user), ct);

        // Server → licensing (its own SEC_LICENSE_PKT header) then Demand Active. At level LOW the
        // server→client direction is unencrypted but carries the 4-byte basic security header.
        var licensing = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(0x0080, BinaryPrimitives.ReadUInt16LittleEndian(licensing.Payload)); // SEC_LICENSE_PKT

        var demand = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(ShareControl.DemandActive & 0x0F, ShareControl.PduType(demand.Payload.AsSpan(4)));

        // Encrypted Confirm Active + Font List. The server must decrypt both (matching RC4 keys, stream
        // continuity, and MAC) to recognise the Font List and respond — wrong keys never reach here.
        await client.WriteRawAsync(
            crypto.Encrypt(user, Gcc.IoChannelId, McsClient.BuildConfirmActivePdu(user, Capabilities.ShareId)), ct);
        await client.WriteRawAsync(
            crypto.Encrypt(user, Gcc.IoChannelId, McsClient.BuildFontListPdu()), ct);

        // Server → the first finalization PDU (Synchronize) — proof it decrypted the Font List.
        var fin = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        Assert.Equal(ShareControl.Data & 0x0F, ShareControl.PduType(fin.Payload.AsSpan(4)));
        Assert.Equal(Finalization.Pdu2Synchronize, Finalization.DataPduType2(fin.Payload.AsSpan(4)));
    }

    [Fact]
    public async Task StandardRdpSecurity_HighLevel_EncryptsBothDirections()
    {
        using var server = new MockServerFixture(desktop: true, rdpHighEncryption: true);
        await using var client = new RdpTestClient();
        var ct = Timeout;

        await client.ConnectAsync(server.Endpoint, ct);
        await client.SendConnectionRequestAsync(RdpNegProtocol.Rdp, ct: ct);
        await client.ReadConnectionConfirmAsync(ct);

        await client.WriteRawAsync(
            McsClient.BuildConnectInitial(StandardSecurity.Method128Bit, "cliprdr", "drdynvc"), ct);
        var connectResp = await client.ReadTpktPayloadAsync(ct);
        var crypto = new RdpCryptoClient(connectResp, StandardSecurity.Method128Bit);
        var (io, ids) = McsClient.ParseConnectResponseNetwork(connectResp);

        await client.WriteRawAsync(McsClient.ErectDomainRequest(), ct);
        await client.WriteRawAsync(McsClient.AttachUserRequest(), ct);
        ushort user = McsClient.ParseAttachUserConfirm(await client.ReadTpktPayloadAsync(ct));
        foreach (var ch in new[] { user, io }.Concat(ids))
        {
            await client.WriteRawAsync(McsClient.ChannelJoinRequest(user, ch), ct);
            await client.ReadTpktPayloadAsync(ct);
        }

        await client.WriteRawAsync(crypto.SecurityExchange(user), ct);
        await client.WriteRawAsync(crypto.EncryptClientInfo(user), ct);

        // At HIGH the server→client PDUs are encrypted too — the client must decrypt them.
        var lic = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        var (_, licFlags) = crypto.DecryptServerPdu(lic.Payload);
        Assert.True((licFlags & StandardSecurity.SecLicensePkt) != 0);   // encrypted licensing
        Assert.True((licFlags & StandardSecurity.SecEncrypt) != 0);

        var demand = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        var (demandPdu, _) = crypto.DecryptServerPdu(demand.Payload);
        Assert.Equal(ShareControl.DemandActive & 0x0F, ShareControl.PduType(demandPdu));

        await client.WriteRawAsync(
            crypto.Encrypt(user, Gcc.IoChannelId, McsClient.BuildConfirmActivePdu(user, Capabilities.ShareId)), ct);
        await client.WriteRawAsync(
            crypto.Encrypt(user, Gcc.IoChannelId, McsClient.BuildFontListPdu()), ct);

        var fin = McsPdu.ParseSendData(Cotp.StripDataTpdu(await client.ReadTpktPayloadAsync(ct)));
        var (finPdu, _) = crypto.DecryptServerPdu(fin.Payload);
        Assert.Equal(Finalization.Pdu2Synchronize, Finalization.DataPduType2(finPdu));
    }
}
