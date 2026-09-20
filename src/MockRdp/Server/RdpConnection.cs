using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using MockRdp.Framing;
using MockRdp.Mcs;
using MockRdp.Rdp;
using MockRdp.Util;
using MockRdp.X224;

namespace MockRdp.Server;

/// <summary>
/// Drives one client connection through the RDP connection sequence. Currently
/// implements M1: X.224 negotiation and the TLS upgrade. Later milestones extend
/// <see cref="RunAsync"/> past <see cref="ConnectionState.TlsUp"/>.
/// </summary>
public sealed class RdpConnection(TcpClient tcp, X509Certificate2 cert, ILogger log,
    string[]? dvcChannels = null, string[]? rdpdrReads = null,
    Dictionary<string, Dvc.Behavior>? dvcBehaviors = null,
    string[]? rdpdrLists = null, string[]? rdpdrWrites = null, bool desktop = false)
{
    private Stream _stream = tcp.GetStream();
    private ushort _cliprdrChannelId;
    private bool _offeredServerClipboard;

    // Dynamic virtual channels (MS-RDPEDYC over "drdynvc"). The server advertises
    // capabilities, then opens each configured channel and echoes data back on it.
    private ushort _drdynvcChannelId;
    private readonly string[] _dvcChannelNames = dvcChannels ?? ["ECHO"];
    private readonly Dictionary<uint, string> _dvcOpen = new();       // id -> name (create confirmed)
    private readonly Dictionary<uint, string> _dvcPending = new();    // id -> name (create sent)
    private readonly Dictionary<uint, (int Total, ByteWriter Buf)> _dvcReasm = new();
    private readonly Dictionary<string, Dvc.Behavior> _dvcBehaviors = dvcBehaviors ?? new();
    private uint _nextDvcId = 1;

    // Drive redirection (MS-RDPEFS over "rdpdr"). After the init handshake the mock reads
    // the configured client paths back over the redirected drives (\\tsclient) — proving the
    // redirected content is reachable, not just requested. Reads are chunked small enough to
    // fit one static-channel PDU (no reassembly), and capped so a huge file doesn't stream.
    private const uint RdpdrReadChunk = 1000;
    private const long RdpdrReadCap = 64 * 1024;
    private static readonly byte[] RdpdrMarker = "rdpeek-mock was here\r\n"u8.ToArray();
    private enum RdpdrOp { Read, List, Write }
    private enum RdpdrPhase { Idle, Create, Transfer, Close }
    private ushort _rdpdrChannelId;
    private readonly Queue<(RdpdrOp Op, string Path)> _rdpdrOps = BuildRdpdrOps(rdpdrReads, rdpdrLists, rdpdrWrites);
    private readonly Dictionary<char, uint> _rdpdrDrives = new();     // drive letter -> device id
    private readonly List<string> _rdpdrNames = new();                // accumulated dir entries
    private uint _rdpdrCompletionId;
    private RdpdrOp _rdpdrOp;
    private RdpdrPhase _rdpdrPhase = RdpdrPhase.Idle;
    private string _rdpdrPath = "";
    private string _rdpdrRel = "";
    private uint _rdpdrDeviceId;
    private uint _rdpdrFileId;
    private ulong _rdpdrOffset;
    private long _rdpdrBytes;
    private IncrementalHash? _rdpdrHash;

    private static Queue<(RdpdrOp, string)> BuildRdpdrOps(string[]? reads, string[]? lists, string[]? writes)
    {
        var q = new Queue<(RdpdrOp, string)>();
        foreach (var p in reads ?? []) q.Enqueue((RdpdrOp.Read, p));
        foreach (var p in lists ?? []) q.Enqueue((RdpdrOp.List, p));
        foreach (var p in writes ?? []) q.Enqueue((RdpdrOp.Write, p));
        return q;
    }

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

        // EXTENDED_CLIENT_DATA_SUPPORTED (0x01): required by mstsc/mstscax to advance past TLS.
        await WriteAsync(Cotp.BuildConnectionConfirm(RdpNegProtocol.Ssl, negRspFlags: 0x01), ct);
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
        var channels = Gcc.ReadRequestedChannels(userData);
        int channelCount = channels.Count;
        int clipIndex = channels.FindIndex(n => string.Equals(n, "cliprdr", StringComparison.OrdinalIgnoreCase));
        _cliprdrChannelId = clipIndex >= 0 ? (ushort)(Gcc.FirstVirtualChannelId + clipIndex) : (ushort)0;
        int dvcIndex = channels.FindIndex(n => string.Equals(n, "drdynvc", StringComparison.OrdinalIgnoreCase));
        _drdynvcChannelId = dvcIndex >= 0 ? (ushort)(Gcc.FirstVirtualChannelId + dvcIndex) : (ushort)0;
        int rdpdrIndex = channels.FindIndex(n => string.Equals(n, "rdpdr", StringComparison.OrdinalIgnoreCase));
        _rdpdrChannelId = rdpdrIndex >= 0 ? (ushort)(Gcc.FirstVirtualChannelId + rdpdrIndex) : (ushort)0;
        ushort userChannelId = (ushort)(Gcc.FirstVirtualChannelId + channelCount);
        log.LogInformation("MCS Connect-Initial: {Count} virtual channels requested ({Names}).",
            channelCount, string.Join(", ", channels));

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
                    log.LogInformation("MCS complete: {Joined} channels joined; Client Info received.", joined);
                    var (_, clientInfo) = McsPdu.ParseSendData(mcs);
                    var info = ClientInfo.Parse(clientInfo);
                    log.LogInformation("Client Info: user='{User}' domain='{Domain}' altShell='{Shell}' workDir='{Dir}'.",
                        info.User, info.Domain, info.AlternateShell, info.WorkingDir);
                    await RunActivationAsync(userChannelId, ct);
                    return;

                default:
                    log.LogWarning("Unexpected MCS PDU 0x{Byte:X2} during channel join.", mcs.Length > 0 ? mcs[0] : 0);
                    return;
            }
        }
    }

    /// <summary>M3: licensing → capability exchange → finalization, ending at an active session.</summary>
    private async Task RunActivationAsync(ushort userChannelId, CancellationToken ct)
    {
        State = ConnectionState.Licensing;
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Licensing.BuildValidClient()), ct);
        log.LogInformation("Sent licensing: valid client (no license required).");

        State = ConnectionState.CapabilityExchange;
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Capabilities.BuildDemandActive()), ct);
        log.LogInformation("Sent Demand Active (capabilities).");

        State = ConnectionState.Finalization;
        while (true)
        {
            var packet = await ReadTpktAsync(ct);
            if (packet is null) { log.LogWarning("Closed during activation."); return; }
            var mcs = Cotp.StripDataTpdu(packet);

            if (McsPdu.ClassifyDomainPdu(mcs) != McsDomainPdu.SendDataRequest)
            {
                log.LogDebug("Ignoring non-Send-Data PDU 0x{Byte:X2} during activation.", mcs.Length > 0 ? mcs[0] : 0);
                continue;
            }

            var (_, payload) = McsPdu.ParseSendData(mcs);
            int pduType = ShareControl.PduType(payload);

            if (pduType == (ShareControl.ConfirmActive & 0x0F))
            {
                log.LogInformation("Confirm Active received (client accepted capabilities).");
            }
            else if (pduType == (ShareControl.Data & 0x0F))
            {
                int type2 = Finalization.DataPduType2(payload);
                log.LogDebug("Client Data PDU, pduType2={Type2}.", type2);
                if (type2 == Finalization.Pdu2FontList)
                {
                    await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Finalization.BuildSynchronize(userChannelId)), ct);
                    await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Finalization.BuildControlCooperate()), ct);
                    await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Finalization.BuildControlGranted(userChannelId)), ct);
                    await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Finalization.BuildFontMap()), ct);
                    State = ConnectionState.Active;
                    log.LogInformation("Finalization complete — session ACTIVE.");
                    if (desktop) await DrawDesktopAsync(ct);
                    else await DrawTestPatternAsync(ct);
                    await InitClipboardAsync(ct);
                    await InitDvcAsync(ct);
                    await InitRdpdrAsync(ct);
                    await ServeAsync(ct);
                    return;
                }
            }
            else
            {
                log.LogDebug("Activation PDU with share-control type {Type}.", pduType);
            }
        }
    }

    /// <summary>M4: draws the startup test pattern (a row of colour squares) via bitmap updates.</summary>
    private async Task DrawTestPatternAsync(CancellationToken ct)
    {
        foreach (var square in Graphics.TestPattern())
            await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Graphics.BuildSolidSquare(square)), ct);
        log.LogInformation("Sent startup test pattern ({Count} bitmap updates).", Graphics.TestPattern().Count);
    }

    private Desktop.FakeDesktop? _desktop;

    /// <summary>Renders the fake Windows desktop and sends it as bitmap-update tiles.</summary>
    private async Task DrawDesktopAsync(CancellationToken ct)
    {
        _desktop = new Desktop.FakeDesktop(Capabilities.DesktopWidth, Capabilities.DesktopHeight);
        await SendDesktopAsync(ct);
        log.LogInformation("Rendered fake desktop.");
    }

    private async Task SendDesktopAsync(CancellationToken ct)
    {
        if (_desktop is null) return;
        foreach (var (x, y, w, h, pixels) in _desktop.DirtyTiles())
            await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Graphics.BuildBitmapTile(x, y, w, h, pixels)), ct);
    }

    /// <summary>M5: keeps the active session alive, reacting to client input. While idle it
    /// periodically re-renders the desktop so the taskbar clock ticks (dirty-rect keeps that
    /// to just the clock tile).</summary>
    private async Task ServeAsync(CancellationToken ct)
    {
        Task<(bool FastPath, byte Header, byte[] Payload)?>? pending = null;
        while (!ct.IsCancellationRequested)
        {
            pending ??= ReadFrameAsync(ct);
            var completed = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(20), ct));
            if (ct.IsCancellationRequested) return;

            if (completed != pending)
            {
                if (_desktop is not null) { _desktop.Render(); await SendDesktopAsync(ct); }
                continue; // input read is still pending
            }

            var frame = await pending;
            pending = null;
            if (frame is null) { log.LogInformation("Client disconnected from active session."); return; }

            if (frame.Value.FastPath)
                await HandleInputAsync(frame.Value.Header, frame.Value.Payload, ct);
            else
                await HandleSlowPathAsync(frame.Value.Payload, ct);
        }
    }

    /// <summary>M6: sends the clipboard capabilities + monitor-ready that start the CLIPRDR exchange.</summary>
    private async Task InitClipboardAsync(CancellationToken ct)
    {
        if (_cliprdrChannelId == 0) return;
        await SendClipboardAsync(Clipboard.ClipboardCapabilities(), ct);
        await SendClipboardAsync(Clipboard.MonitorReady(), ct);
        log.LogInformation("Clipboard channel ready on {Channel} (caps + monitor ready).", _cliprdrChannelId);
    }

    /// <summary>Routes a slow-path Send Data PDU; clipboard channel data goes to CLIPRDR handling.</summary>
    private async Task HandleSlowPathAsync(byte[] tpdu, CancellationToken ct)
    {
        var mcs = Cotp.StripDataTpdu(tpdu);
        if (McsPdu.ClassifyDomainPdu(mcs) != McsDomainPdu.SendDataRequest) return;

        var (channelId, data) = McsPdu.ParseSendData(mcs);
        if (channelId == _cliprdrChannelId && _cliprdrChannelId != 0)
            await HandleClipboardAsync(VirtualChannel.Unwrap(data).ToArray(), ct);
        else if (channelId == _drdynvcChannelId && _drdynvcChannelId != 0)
            await HandleDvcAsync(VirtualChannel.Unwrap(data).ToArray(), ct);
        else if (channelId == _rdpdrChannelId && _rdpdrChannelId != 0)
            await HandleRdpdrAsync(VirtualChannel.Unwrap(data).ToArray(), ct);
    }

    /// <summary>Handles one CLIPRDR PDU: acks format lists, offers text, and serves it on request.</summary>
    private async Task HandleClipboardAsync(byte[] clipPdu, CancellationToken ct)
    {
        switch (Clipboard.ReadMsgType(clipPdu))
        {
            case Clipboard.CbFormatList:
                await SendClipboardAsync(Clipboard.FormatListResponseOk(), ct);
                if (!_offeredServerClipboard)
                {
                    _offeredServerClipboard = true;
                    await SendClipboardAsync(Clipboard.FormatListUnicodeText(), ct);
                    log.LogInformation("Clipboard: acked client format list and offered CF_UNICODETEXT.");
                }
                break;

            case Clipboard.CbFormatDataRequest:
                await SendClipboardAsync(Clipboard.FormatDataResponseText(Clipboard.ServedText), ct);
                log.LogInformation("Clipboard: served text on format data request.");
                break;

            case Clipboard.CbClipCaps:
                log.LogDebug("Clipboard: client capabilities received.");
                break;
        }
    }

    private Task SendClipboardAsync(byte[] cliprdrPdu, CancellationToken ct) =>
        WriteAsync(McsPdu.BuildSendDataIndication(_cliprdrChannelId, VirtualChannel.Wrap(cliprdrPdu)), ct);

    /// <summary>Opens the DVC layer: advertise capabilities. Channels are created once the
    /// client answers with its capabilities response.</summary>
    private async Task InitDvcAsync(CancellationToken ct)
    {
        if (_drdynvcChannelId == 0) return;
        await SendDvcAsync(Dvc.BuildCapabilitiesV1(), ct);
        log.LogInformation("DVC (drdynvc) ready on {Channel}: sent capabilities v1.", _drdynvcChannelId);
    }

    /// <summary>Handles one inbound DRDYNVC PDU: capabilities response, create response,
    /// channel data (echoed back), or close.</summary>
    private async Task HandleDvcAsync(byte[] pdu, CancellationToken ct)
    {
        var msg = Dvc.Parse(pdu);
        switch (msg.Cmd)
        {
            case Dvc.Cmd.Capabilities:
                log.LogInformation("DVC: client capabilities (version {Version}); opening {Count} channel(s).",
                    msg.Version, _dvcChannelNames.Length);
                foreach (var name in _dvcChannelNames)
                    await OpenDvcChannelAsync(name, ct);
                break;

            case Dvc.Cmd.Create: // inbound Create is a create RESPONSE
                var name0 = _dvcPending.Remove(msg.ChannelId, out var pending) ? pending : $"#{msg.ChannelId}";
                int status = Dvc.CreationStatus(msg);
                if (status == 0)
                {
                    _dvcOpen[msg.ChannelId] = name0;
                    log.LogInformation("DVC: channel '{Name}' (id {Id}) opened by client.", name0, msg.ChannelId);
                }
                else
                {
                    log.LogWarning("DVC: client rejected channel '{Name}' (id {Id}), status 0x{Status:X8}.",
                        name0, msg.ChannelId, (uint)status);
                }
                break;

            case Dvc.Cmd.DataFirst:
            case Dvc.Cmd.Data:
                await HandleDvcDataAsync(msg, ct);
                break;

            case Dvc.Cmd.Close:
                if (_dvcOpen.Remove(msg.ChannelId, out var closed))
                    log.LogInformation("DVC: client closed channel '{Name}' (id {Id}).", closed, msg.ChannelId);
                _dvcReasm.Remove(msg.ChannelId);
                break;
        }
    }

    private async Task OpenDvcChannelAsync(string name, CancellationToken ct)
    {
        uint id = _nextDvcId++;
        _dvcPending[id] = name;
        await SendDvcAsync(Dvc.BuildCreateRequest(id, name), ct);
        log.LogInformation("DVC: create request for '{Name}' (id {Id}).", name, id);
    }

    /// <summary>Reassembles fragmented data and echoes each complete message back on its channel.</summary>
    private async Task HandleDvcDataAsync(Dvc.Message msg, CancellationToken ct)
    {
        if (!_dvcOpen.ContainsKey(msg.ChannelId))
        {
            log.LogDebug("DVC: data on unopened channel {Id}, ignoring.", msg.ChannelId);
            return;
        }

        byte[]? complete;
        if (msg.Cmd == Dvc.Cmd.Data && !_dvcReasm.ContainsKey(msg.ChannelId))
        {
            complete = msg.Data; // unfragmented
        }
        else
        {
            if (msg.Cmd == Dvc.Cmd.DataFirst)
                _dvcReasm[msg.ChannelId] = (msg.TotalLength, new ByteWriter());

            if (!_dvcReasm.TryGetValue(msg.ChannelId, out var acc)) return;
            acc.Buf.WriteBytes(msg.Data);
            if (acc.Buf.Length < acc.Total) return;
            complete = acc.Buf.ToArray();
            _dvcReasm.Remove(msg.ChannelId);
        }

        var name = _dvcOpen[msg.ChannelId];
        var behavior = _dvcBehaviors.GetValueOrDefault(name);
        var fault = behavior?.Fault ?? Dvc.Fault.None;

        if (fault == Dvc.Fault.Drop)
        {
            log.LogInformation("DVC: '{Name}' (id {Id}) received {Count} bytes — DROPPING (fault).",
                name, msg.ChannelId, complete.Length);
            return;
        }
        if (fault == Dvc.Fault.Close)
        {
            log.LogInformation("DVC: '{Name}' (id {Id}) — CLOSING mid-stream (fault).", name, msg.ChannelId);
            _dvcOpen.Remove(msg.ChannelId);
            await SendDvcAsync(Dvc.BuildClose(msg.ChannelId), ct);
            return;
        }

        var reply = behavior?.Reply ?? complete;                 // canned reply, else echo
        if (fault == Dvc.Fault.Truncate && reply.Length > 1)
            reply = reply[..(reply.Length / 2)];
        if (fault == Dvc.Fault.Delay)
            await Task.Delay(1500, ct);

        string how = behavior?.Reply is not null ? "canned reply" : "echo";
        if (fault != Dvc.Fault.None) how += $" +{fault}";
        log.LogInformation("DVC: '{Name}' (id {Id}) received {Count} bytes — {How} ({Out} bytes).",
            name, msg.ChannelId, complete.Length, how, reply.Length);

        var pdus = fault == Dvc.Fault.Fragment
            ? Dvc.BuildData(msg.ChannelId, reply, 4)   // force tiny fragments
            : Dvc.BuildData(msg.ChannelId, reply);
        foreach (var outPdu in pdus)
            await SendDvcAsync(outPdu, ct);
    }

    private Task SendDvcAsync(byte[] dvcPdu, CancellationToken ct) =>
        WriteAsync(McsPdu.BuildSendDataIndication(_drdynvcChannelId, VirtualChannel.Wrap(dvcPdu)), ct);

    // ── Drive redirection (MS-RDPEFS / rdpdr) ───────────────────────────────

    /// <summary>Starts the rdpdr init handshake if any read/list/write op is configured.</summary>
    private async Task InitRdpdrAsync(CancellationToken ct)
    {
        if (_rdpdrChannelId == 0 || _rdpdrOps.Count == 0) return;
        await SendRdpdrAsync(Rdpdr.ServerAnnounceReq(1), ct);
        log.LogInformation("rdpdr ready on {Channel}: sent Server Announce; {Count} client op(s) queued.",
            _rdpdrChannelId, _rdpdrOps.Count);
    }

    /// <summary>Drives the rdpdr init handshake, then reads the configured files from the client.</summary>
    private async Task HandleRdpdrAsync(byte[] pdu, CancellationToken ct)
    {
        log.LogDebug("rdpdr recv packetId=0x{Id:X4} ({Len} bytes).", Rdpdr.PacketId(pdu), pdu.Length);
        switch (Rdpdr.PacketId(pdu))
        {
            case Rdpdr.ClientName: // follows the client's announce reply — now negotiate capabilities
                await SendRdpdrAsync(Rdpdr.ServerCapabilityReq(), ct);
                await SendRdpdrAsync(Rdpdr.ServerClientIdConfirm(1), ct);
                await SendRdpdrAsync(Rdpdr.UserLoggedOnPdu(), ct);
                break;

            case Rdpdr.DeviceListAnnounce:
                var devices = Rdpdr.ParseDeviceList(pdu);
                int fsAdded = 0;
                foreach (var d in devices)
                {
                    await SendRdpdrAsync(Rdpdr.DeviceAnnounceResponse(d.Id, 0), ct);
                    log.LogDebug("rdpdr: device type=0x{Type:X8} id={Id} name='{Dos}'.", d.Type, d.Id, d.DosName);
                    if (d.Type == Rdpdr.DeviceTypeFilesystem && d.DosName.Length >= 1)
                    {
                        _rdpdrDrives[char.ToUpperInvariant(d.DosName[0])] = d.Id;
                        fsAdded++;
                        log.LogInformation("rdpdr: client redirected drive {Dos} (device {Id}).", d.DosName, d.Id);
                    }
                }
                // Only begin ops once at least one filesystem drive is available (the client
                // may send an empty announce first, then the drives in a later one).
                if (_rdpdrPhase == RdpdrPhase.Idle && fsAdded > 0) await StartNextRdpdrOpAsync(ct);
                break;

            case Rdpdr.DeviceIoCompletion:
                await HandleRdpdrCompletionAsync(pdu, ct);
                break;
        }
    }

    private async Task StartNextRdpdrOpAsync(CancellationToken ct)
    {
        while (_rdpdrOps.Count > 0)
        {
            var (op, clientPath) = _rdpdrOps.Dequeue();
            if (clientPath.Length < 2 || clientPath[1] != ':')
            {
                log.LogWarning("rdpdr: skipping non-drive path '{Path}'.", clientPath);
                continue;
            }
            char letter = char.ToUpperInvariant(clientPath[0]);
            if (!_rdpdrDrives.TryGetValue(letter, out var deviceId))
            {
                log.LogWarning("rdpdr: drive {Letter}: not redirected; cannot {Op} '{Path}'.", letter, op, clientPath);
                continue;
            }

            var rel = clientPath[2..].Replace('/', '\\');
            rel = rel.Length == 0 ? "\\" : (rel.StartsWith('\\') ? rel : "\\" + rel);

            _rdpdrOp = op;
            _rdpdrPath = clientPath;
            _rdpdrRel = rel;
            _rdpdrDeviceId = deviceId;
            _rdpdrFileId = 0;
            _rdpdrOffset = 0;
            _rdpdrBytes = 0;
            _rdpdrNames.Clear();
            _rdpdrHash = op == RdpdrOp.Read ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            _rdpdrPhase = RdpdrPhase.Create;

            var create = op switch
            {
                RdpdrOp.List  => Rdpdr.CreateRequest(deviceId, ++_rdpdrCompletionId, rel, Rdpdr.AccessList,  Rdpdr.DispositionOpen,        Rdpdr.OptionsDirectory),
                RdpdrOp.Write => Rdpdr.CreateRequest(deviceId, ++_rdpdrCompletionId, rel, Rdpdr.AccessWrite, Rdpdr.DispositionOverwriteIf, Rdpdr.OptionsFile),
                _             => Rdpdr.CreateRequest(deviceId, ++_rdpdrCompletionId, rel),
            };
            await SendRdpdrAsync(create, ct);
            log.LogInformation("rdpdr: {Op} '{Path}' over the redirected drive.", op, clientPath);
            return;
        }
        _rdpdrPhase = RdpdrPhase.Idle;
        log.LogInformation("rdpdr: all operations complete.");
    }

    private async Task HandleRdpdrCompletionAsync(byte[] pdu, CancellationToken ct)
    {
        var c = Rdpdr.ParseIoCompletion(pdu);
        switch (_rdpdrPhase)
        {
            case RdpdrPhase.Create:
                if (c.IoStatus != 0)
                {
                    log.LogWarning("rdpdr: {Op} open of '{Path}' failed (status 0x{Status:X8}).", _rdpdrOp, _rdpdrPath, c.IoStatus);
                    await StartNextRdpdrOpAsync(ct);
                    return;
                }
                _rdpdrFileId = Rdpdr.CreateFileId(c.Rest);
                _rdpdrPhase = RdpdrPhase.Transfer;
                await StartTransferAsync(ct);
                break;

            case RdpdrPhase.Transfer:
                await ContinueTransferAsync(c, ct);
                break;

            case RdpdrPhase.Close:
                await StartNextRdpdrOpAsync(ct);
                break;
        }
    }

    private Task StartTransferAsync(CancellationToken ct) => _rdpdrOp switch
    {
        RdpdrOp.List  => SendRdpdrAsync(Rdpdr.QueryDirectoryRequest(_rdpdrDeviceId, _rdpdrFileId, ++_rdpdrCompletionId, initial: true, (_rdpdrRel.EndsWith('\\') ? _rdpdrRel : _rdpdrRel + "\\") + "*"), ct),
        RdpdrOp.Write => SendRdpdrAsync(Rdpdr.WriteRequest(_rdpdrDeviceId, _rdpdrFileId, ++_rdpdrCompletionId, 0, RdpdrMarker), ct),
        _             => SendRdpdrAsync(Rdpdr.ReadRequest(_rdpdrDeviceId, _rdpdrFileId, ++_rdpdrCompletionId, RdpdrReadChunk, _rdpdrOffset), ct),
    };

    private async Task ContinueTransferAsync(Rdpdr.Completion c, CancellationToken ct)
    {
        switch (_rdpdrOp)
        {
            case RdpdrOp.Read:
                if (c.IoStatus != 0) { await FinishRdpdrOpAsync(ct); return; } // e.g. STATUS_END_OF_FILE
                var data = Rdpdr.ReadData(c.Rest);
                if (data.Length > 0)
                {
                    _rdpdrHash!.AppendData(data);
                    _rdpdrBytes += data.Length;
                    _rdpdrOffset += (ulong)data.Length;
                }
                if (data.Length < RdpdrReadChunk || _rdpdrBytes >= RdpdrReadCap) await FinishRdpdrOpAsync(ct);
                else await SendRdpdrAsync(Rdpdr.ReadRequest(_rdpdrDeviceId, _rdpdrFileId, ++_rdpdrCompletionId, RdpdrReadChunk, _rdpdrOffset), ct);
                break;

            case RdpdrOp.List:
                if (c.IoStatus == Rdpdr.StatusNoMoreFiles || c.IoStatus != 0) { await FinishRdpdrOpAsync(ct); return; }
                _rdpdrNames.AddRange(Rdpdr.ParseDirEntries(Rdpdr.ReadData(c.Rest)));
                await SendRdpdrAsync(Rdpdr.QueryDirectoryRequest(_rdpdrDeviceId, _rdpdrFileId, ++_rdpdrCompletionId, initial: false, ""), ct);
                break;

            case RdpdrOp.Write:
                if (c.IoStatus != 0) log.LogWarning("rdpdr: WRITE '{Path}' failed (status 0x{Status:X8}).", _rdpdrPath, c.IoStatus);
                else _rdpdrBytes = RdpdrMarker.Length;
                await FinishRdpdrOpAsync(ct);
                break;
        }
    }

    private async Task FinishRdpdrOpAsync(CancellationToken ct)
    {
        switch (_rdpdrOp)
        {
            case RdpdrOp.Read:
                var hash = _rdpdrHash is not null ? Convert.ToHexString(_rdpdrHash.GetHashAndReset()).ToLowerInvariant() : "";
                bool capped = _rdpdrBytes >= RdpdrReadCap;
                log.LogInformation("rdpdr: READ '{Path}' -> {Bytes} bytes{Capped}, sha256={Hash}",
                    _rdpdrPath, _rdpdrBytes, capped ? " (capped)" : "", hash);
                break;
            case RdpdrOp.List:
                log.LogInformation("rdpdr: LIST '{Path}' -> {Count} entries: {Names}",
                    _rdpdrPath, _rdpdrNames.Count, string.Join(", ", _rdpdrNames));
                break;
            case RdpdrOp.Write:
                log.LogInformation("rdpdr: WROTE '{Path}' -> {Bytes} bytes.", _rdpdrPath, _rdpdrBytes);
                break;
        }
        _rdpdrPhase = RdpdrPhase.Close;
        await SendRdpdrAsync(Rdpdr.CloseRequest(_rdpdrDeviceId, _rdpdrFileId, ++_rdpdrCompletionId), ct);
    }

    private Task SendRdpdrAsync(byte[] pdu, CancellationToken ct) =>
        WriteAsync(McsPdu.BuildSendDataIndication(_rdpdrChannelId, VirtualChannel.Wrap(pdu)), ct);

    /// <summary>Decodes fast-path input and draws a marker where the mouse moves/clicks.</summary>
    private async Task HandleInputAsync(byte fastPathHeader, byte[] payload, CancellationToken ct)
    {
        bool changed = false;
        foreach (var ev in Input.ParseFastPath(fastPathHeader, payload))
        {
            if (_desktop is not null)
            {
                changed |= _desktop.OnInput(ev);
                continue;
            }
            switch (ev.Type)
            {
                case InputEventType.Mouse:
                    await DrawMarkerAsync(ev.X, ev.Y, ct);
                    break;
                case InputEventType.Scancode:
                    log.LogDebug("Key: scancode 0x{Code:X2} flags 0x{Flags:X2}.", ev.Code, ev.Flags);
                    break;
                case InputEventType.Unicode:
                    log.LogDebug("Key: unicode U+{Code:X4}.", ev.X);
                    break;
            }
        }

        if (changed && _desktop is not null)
        {
            await SendDesktopAsync(ct);
            log.LogInformation("Desktop: active={Active} startMenu={Menu} windows={Windows}.",
                _desktop.Active, _desktop.StartMenuOpen, _desktop.WindowCount);
        }
    }

    /// <summary>Draws a small marker square at the cursor position (clamped to the desktop).</summary>
    private async Task DrawMarkerAsync(ushort x, ushort y, CancellationToken ct)
    {
        const int size = 16;
        int mx = Math.Clamp((int)x, 0, Capabilities.DesktopWidth - size);
        int my = Math.Clamp((int)y, 0, Capabilities.DesktopHeight - size);
        var square = new Graphics.Square(mx, my, size, Graphics.Rgb565(255, 255, 0)); // yellow
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Graphics.BuildSolidSquare(square)), ct);
    }

    /// <summary>Reads one frame, distinguishing TPKT (slow-path) from fast-path framing.</summary>
    private async Task<(bool FastPath, byte Header, byte[] Payload)?> ReadFrameAsync(CancellationToken ct)
    {
        var first = new byte[1];
        // A clean EOF or a forcible close (client just disconnected) both mean "no more frames".
        try { await _stream.ReadExactlyAsync(first, ct); }
        catch (EndOfStreamException) { return null; }
        catch (IOException) { return null; }

        if (first[0] == Tpkt.Version)
        {
            var rest = new byte[3];
            await _stream.ReadExactlyAsync(rest, ct);
            int total = (rest[1] << 8) | rest[2];
            var body = new byte[Math.Max(0, total - Tpkt.HeaderLength)];
            await _stream.ReadExactlyAsync(body, ct);
            return (false, first[0], body);
        }

        // Fast-path framing: the header byte is `first`, then a 1- or 2-byte length.
        var l1 = new byte[1];
        await _stream.ReadExactlyAsync(l1, ct);
        int length, headerLen;
        if ((l1[0] & 0x80) != 0)
        {
            var l2 = new byte[1];
            await _stream.ReadExactlyAsync(l2, ct);
            length = ((l1[0] & 0x7F) << 8) | l2[0];
            headerLen = 3;
        }
        else
        {
            length = l1[0];
            headerLen = 2;
        }
        var payload = new byte[Math.Max(0, length - headerLen)];
        await _stream.ReadExactlyAsync(payload, ct);
        return (true, first[0], payload);
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
