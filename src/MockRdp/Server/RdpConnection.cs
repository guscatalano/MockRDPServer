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
    string[]? rdpdrLists = null, string[]? rdpdrWrites = null, bool desktop = false, bool logon = false,
    bool desktopDirect = false, Desktop.VfsNode? vfsRoot = null)
{
    private bool _nlaRequested;
    private Stream _stream = tcp.GetStream();

    // Snapshot state surfaced by the desktop's "Connection Info" window.
    private readonly DateTime _connectedAt = DateTime.Now;
    private readonly string _clientEndpoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    private IReadOnlyList<string> _requestedChannels = [];
    private string _clientUser = "";
    private string _clientDomain = "";
    private int _dvcVersion;                         // DRDYNVC capability version the client advertised

    // Current desktop resolution (changed by a Display Control resize / Deactivation-Reactivation).
    private int _width = Capabilities.DesktopWidth;
    private int _height = Capabilities.DesktopHeight;
    private ushort _userChannelId;
    private uint _displayControlId;                 // DVC id of Microsoft::Windows::RDS::DisplayControl (0 = not open)
    private (int W, int H)? _pendingResize;         // set by a MONITOR_LAYOUT PDU; applied by the serve loop
    private ushort _cliprdrChannelId;
    private bool _offeredServerClipboard;

    // Clipboard file transfer state. The mock offers one file (defaults to the built-in sample, or
    // whatever the user copies on the desktop). For inbound paste it becomes the requester: it learns
    // the client's FileGroupDescriptorW format id, asks for the descriptor, then the bytes.
    private string _offeredFileName = Clipboard.ServedFileName;
    private byte[] _offeredFileBytes = Clipboard.ServedFileBytes;
    private uint _clientFileFormatId;              // the id the client assigned FileGroupDescriptorW
    private enum PasteState { Idle, WaitingDescriptor, WaitingContents }
    private PasteState _pasteState;
    private string _pasteFileName = "";

    // Dynamic virtual channels (MS-RDPEDYC over "drdynvc"). The server advertises
    // capabilities, then opens each configured channel and echoes data back on it.
    private ushort _drdynvcChannelId;
    private readonly string[] _dvcChannelNames = dvcChannels ?? ["ECHO"];
    private readonly Dictionary<uint, string> _dvcOpen = new();       // id -> name (create confirmed)
    private readonly Dictionary<uint, string> _dvcPending = new();    // id -> name (create sent)
    private readonly Dictionary<uint, (int Total, ByteWriter Buf)> _dvcReasm = new();
    private readonly List<Desktop.DvcEvent> _dvcLog = new();          // live feed for the DVC Monitor window
    private readonly Dictionary<string, List<string>> _dvcPendingSends = new(StringComparer.Ordinal); // console msgs awaiting channel open
    private readonly HashSet<uint> _consoleChannels = new();         // DVC Console channels — don't echo their data

    // Records one traffic event for the desktop's Channel Monitor. `render` repaints the monitor
    // immediately (for low-volume channels); high-volume ones (graphics/input) pass false and
    // ride the next natural frame instead, to avoid a render storm.
    private void LogChannel(string channel, bool inbound, int bytes, string note, bool render = true)
    {
        _dvcLog.Add(new Desktop.DvcEvent(DateTime.Now.ToString("HH:mm:ss"), inbound, channel, bytes, note));
        if (_dvcLog.Count > 600) _dvcLog.RemoveRange(0, _dvcLog.Count - 600);
        if (render && _desktop?.HasDvcMonitor == true) _desktopNeedsSend = true;
    }

    private static string InputSummary(IReadOnlyList<InputEvent> events)
    {
        if (events.Count == 0) return "input";
        var kinds = new HashSet<string>();
        foreach (var e in events)
            kinds.Add(e.Type switch
            {
                InputEventType.Scancode or InputEventType.Unicode => "key",
                InputEventType.Mouse => (e.Flags & Input.PtrFlagsButton1) != 0 ? "click"
                                      : (e.Flags & 0x0200) != 0 ? "wheel" : "move",
                _ => e.Type.ToString().ToLowerInvariant(),
            });
        return string.Join("/", kinds) + $" · {events.Count} ev";
    }

    /// <summary>The channels shown in the Monitor's picker: the core I/O traffic (input, graphics),
    /// the open static virtual channels, and the drdynvc transport with its dynamic channels.</summary>
    private IReadOnlyList<string> BuildChannels()
    {
        var list = new List<string> { "input", "graphics" };
        if (_rdpdrChannelId != 0) list.Add("rdpdr");
        if (_cliprdrChannelId != 0) list.Add("cliprdr");
        if (_drdynvcChannelId != 0) list.Add("drdynvc");
        list.AddRange(_dvcOpen.Values.Select(DvcLabel).Distinct().OrderBy(n => n, StringComparer.Ordinal));
        return list;
    }
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

    /// <summary>One rdpdr operation. Config ops (from --rdpdr-*) just log; on-demand ops (the
    /// desktop's \\tsclient browser) carry a callback that receives the entries or file bytes.</summary>
    private sealed class RdpdrReq
    {
        public required RdpdrOp Op;
        public required string Path;                                  // client path, e.g. C:\Users\...
        public Action<List<(string Name, bool IsDir)>, byte[]>? Done; // null for config ops
    }

    private ushort _rdpdrChannelId;
    private uint _rdpdrClientId = 1;                 // the ClientId the client chose in its Announce Reply
    private readonly Queue<RdpdrReq> _rdpdrOps = BuildRdpdrOps(rdpdrReads, rdpdrLists, rdpdrWrites);
    private readonly Dictionary<char, uint> _rdpdrDrives = new();     // drive letter -> device id
    private readonly List<(string Name, bool IsDir)> _rdpdrEntries = new();
    private readonly List<byte> _rdpdrReadBytes = new();
    private uint _rdpdrCompletionId;
    private RdpdrReq? _rdpdrCurrent;
    private RdpdrPhase _rdpdrPhase = RdpdrPhase.Idle;
    private string _rdpdrPath = "";
    private string _rdpdrRel = "";
    private uint _rdpdrDeviceId;
    private uint _rdpdrFileId;
    private ulong _rdpdrOffset;
    private long _rdpdrBytes;
    private IncrementalHash? _rdpdrHash;

    private RdpdrOp _rdpdrOp => _rdpdrCurrent?.Op ?? RdpdrOp.List;

    private static Queue<RdpdrReq> BuildRdpdrOps(string[]? reads, string[]? lists, string[]? writes)
    {
        var q = new Queue<RdpdrReq>();
        foreach (var p in reads ?? []) q.Enqueue(new RdpdrReq { Op = RdpdrOp.Read, Path = p });
        foreach (var p in lists ?? []) q.Enqueue(new RdpdrReq { Op = RdpdrOp.List, Path = p });
        foreach (var p in writes ?? []) q.Enqueue(new RdpdrReq { Op = RdpdrOp.Write, Path = p });
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

        // NLA/CredSSP (HYBRID) requested => the user authenticated at connect, so the fake
        // desktop boots straight in; otherwise it shows a logon screen (like UserAuthentication=0).
        _nlaRequested = cr.HasNegReq && (cr.RequestedProtocols & (RdpNegProtocol.Hybrid | RdpNegProtocol.HybridEx)) != 0;

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
        _requestedChannels = channels;
        int channelCount = channels.Count;
        int clipIndex = channels.FindIndex(n => string.Equals(n, "cliprdr", StringComparison.OrdinalIgnoreCase));
        _cliprdrChannelId = clipIndex >= 0 ? (ushort)(Gcc.FirstVirtualChannelId + clipIndex) : (ushort)0;
        int dvcIndex = channels.FindIndex(n => string.Equals(n, "drdynvc", StringComparison.OrdinalIgnoreCase));
        _drdynvcChannelId = dvcIndex >= 0 ? (ushort)(Gcc.FirstVirtualChannelId + dvcIndex) : (ushort)0;
        int rdpdrIndex = channels.FindIndex(n => string.Equals(n, "rdpdr", StringComparison.OrdinalIgnoreCase));
        _rdpdrChannelId = rdpdrIndex >= 0 ? (ushort)(Gcc.FirstVirtualChannelId + rdpdrIndex) : (ushort)0;
        ushort userChannelId = (ushort)(Gcc.FirstVirtualChannelId + channelCount);
        _userChannelId = userChannelId;
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
                    _clientUser = info.User;
                    _clientDomain = info.Domain;
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
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Capabilities.BuildDemandActive(_width, _height)), ct);
        log.LogInformation("Sent Demand Active (capabilities) at {W}x{H}.", _width, _height);

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
                    await SendFinalizationAsync(ct);
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

    /// <summary>Sends the four finalization PDUs (Synchronize, Control Cooperate/Granted, Font Map)
    /// that complete activation — used both at connect and after a Deactivation-Reactivation.</summary>
    private async Task SendFinalizationAsync(CancellationToken ct)
    {
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Finalization.BuildSynchronize(_userChannelId)), ct);
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Finalization.BuildControlCooperate()), ct);
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Finalization.BuildControlGranted(_userChannelId)), ct);
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Finalization.BuildFontMap()), ct);
    }

    /// <summary>M4: draws the startup test pattern (a row of colour squares) via bitmap updates.</summary>
    private async Task DrawTestPatternAsync(CancellationToken ct)
    {
        foreach (var square in Graphics.TestPattern())
            await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Graphics.BuildSolidSquare(square)), ct);
        log.LogInformation("Sent startup test pattern ({Count} bitmap updates).", Graphics.TestPattern().Count);
    }

    private Desktop.FakeDesktop? _desktop;
    private bool _desktopNeedsSend;   // set by an rdpdr callback; the serve loop resends
    private bool _rdpdrKick;          // an on-demand rdpdr op was queued; start it after input

    /// <summary>Renders the fake Windows desktop and sends it as bitmap-update tiles.</summary>
    private async Task DrawDesktopAsync(CancellationToken ct)
    {
        bool showLogon = !desktopDirect && (logon || !_nlaRequested);
        _desktop = new Desktop.FakeDesktop(_width, _height, showLogon, vfsRoot)
        {
            OnClientList = RequestClientList,
            OnClientOpen = RequestClientOpen,
            ConnectionStats = BuildConnectionStats,
            DvcTraffic = () => _dvcLog.ToArray(),
            DvcChannels = BuildChannels,
        };
        log.LogInformation("Fake desktop booting to {Boot} (NLA requested: {Nla}).",
            showLogon ? "logon screen" : "desktop", _nlaRequested);
        await SendDesktopAsync(ct);
        log.LogInformation("Rendered fake desktop.");
    }

    /// <summary>Live rows for the desktop's "Connection Info" window: what a real RDP session would
    /// expose about itself — state, security, the joined static channels, the open dynamic virtual
    /// channels, and the client's redirected drives.</summary>
    private IReadOnlyList<(string Label, string Value)> BuildConnectionStats()
    {
        static string Join<T>(IEnumerable<T> items, string empty) =>
            items.Any() ? string.Join(", ", items) : empty;

        var who = string.IsNullOrEmpty(_clientDomain) ? _clientUser : $@"{_clientDomain}\{_clientUser}";
        var up = DateTime.Now - _connectedAt;

        string dvcs = _dvcOpen.Count > 0
            ? Join(_dvcOpen.OrderBy(o => o.Key).Select(o => $"{o.Value} #{o.Key}"), "")
            : Join(_dvcPending.OrderBy(o => o.Key).Select(o => $"{o.Value} #{o.Key} (pending)"), "none open");

        string ChannelState(ushort id, string name) => id == 0 ? "not requested" : $"on (channel {id})";

        return
        [
            ("State", State.ToString()),
            ("Client address", _clientEndpoint),
            ("Client user", string.IsNullOrEmpty(who) ? "(none)" : who),
            ("Security", _nlaRequested ? "TLS + NLA requested" : "TLS (no NLA)"),
            ("Resolution", $"{_width} × {_height} @ {Capabilities.BitsPerPixel}bpp"),
            ("Uptime", $"{(int)up.TotalMinutes:00}:{up.Seconds:00}"),
            ("Share id", $"0x{Capabilities.ShareId:X8}"),
            ("Static channels", Join(_requestedChannels, "(none)")),
            ("Clipboard", ChannelState(_cliprdrChannelId, "cliprdr")),
            ("Drive redir (rdpdr)", ChannelState(_rdpdrChannelId, "rdpdr")),
            ("Dynamic VC", _drdynvcChannelId == 0 ? "not requested" : $"on (channel {_drdynvcChannelId}, v{_dvcVersion})"),
            ("Open DVCs", dvcs),
            ("Redirected drives", Join(_rdpdrDrives.Keys.OrderBy(c => c).Select(c => $"{c}:"), "(none yet)")),
        ];
    }

    /// <summary>The desktop's \\tsclient browser asks for a directory listing. The root lists the
    /// redirected drives; deeper paths issue a live rdpdr LIST.</summary>
    private void RequestClientList(int winId, string clientPath)
    {
        if (string.Equals(clientPath, @"\\tsclient", StringComparison.OrdinalIgnoreCase))
        {
            var drives = _rdpdrDrives.Keys.OrderBy(c => c).Select(c => (c.ToString(), true)).ToList();
            _desktop?.DeliverClientList(winId, clientPath, drives);
            return;
        }
        if (!TryTsClientToLocal(clientPath, out var local)) return;
        _rdpdrOps.Enqueue(new RdpdrReq
        {
            Op = RdpdrOp.List,
            Path = local,
            Done = (entries, _) => { _desktop?.DeliverClientList(winId, clientPath, entries); _desktopNeedsSend = true; },
        });
        _rdpdrKick = true;
    }

    /// <summary>The desktop asks to open a client file: issue a live rdpdr READ and show it in Notepad.</summary>
    private void RequestClientOpen(int winId, string clientPath)
    {
        if (!TryTsClientToLocal(clientPath, out var local)) return;
        var name = clientPath.Split('\\', StringSplitOptions.RemoveEmptyEntries)[^1];
        _rdpdrOps.Enqueue(new RdpdrReq
        {
            Op = RdpdrOp.Read,
            Path = local,
            Done = (_, bytes) => { _desktop?.DeliverClientOpen(name, DecodeText(bytes)); _desktopNeedsSend = true; },
        });
        _rdpdrKick = true;
    }

    /// <summary>Maps a <c>\\tsclient\C\rel</c> path to the local device path <c>C:\rel</c>.</summary>
    private static bool TryTsClientToLocal(string tsPath, out string local)
    {
        local = "";
        var segs = tsPath.Split('\\', StringSplitOptions.RemoveEmptyEntries); // ["tsclient","C","Users",...]
        if (segs.Length < 2) return false;
        char drive = char.ToUpperInvariant(segs[1][0]);
        var rel = segs.Length > 2 ? "\\" + string.Join('\\', segs[2..]) : "\\";
        local = $"{drive}:{rel}";
        return true;
    }

    private static string DecodeText(byte[] bytes)
    {
        try { return System.Text.Encoding.UTF8.GetString(bytes); }
        catch { return $"[{bytes.Length} bytes]"; }
    }

    private async Task SendDesktopAsync(CancellationToken ct)
    {
        if (_desktop is null) return;
        // Write all changed tiles, then flush once (per-tile flushing is the drag bottleneck).
        int tiles = 0, bytes = 0;
        foreach (var (x, y, w, h, pixels) in _desktop.DirtyTiles())
        {
            var pdu = McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Graphics.BuildBitmapTile(x, y, w, h, pixels));
            await _stream.WriteAsync(pdu, ct);
            tiles++;
            bytes += pdu.Length;
        }
        if (tiles > 0)
        {
            await _stream.FlushAsync(ct);
            LogChannel("graphics", false, bytes, $"bitmap update · {tiles} tile{(tiles == 1 ? "" : "s")}", render: false);
        }
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
            // Tick fast (~13 fps) while a live window (Channel/Connection Monitor) is open so the
            // feed stays smooth even between traffic events; otherwise idle at 20s just for the clock.
            // Dirty-rect keeps each idle tick to only the changed tiles, so an unchanged frame sends
            // nothing.
            var tick = _desktop?.WantsLiveTick == true ? TimeSpan.FromMilliseconds(75) : TimeSpan.FromSeconds(20);
            var completed = await Task.WhenAny(pending, Task.Delay(tick, ct));
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

            // A Display Control MONITOR_LAYOUT PDU asked for a new resolution — apply it now
            // (no pending read is in flight here, so the reactivation can read its own frames).
            if (_pendingResize is { } rs)
            {
                _pendingResize = null;
                await ReactivateAsync(rs.W, rs.H, ct);
            }

            // An rdpdr result (e.g. a \\tsclient listing) may have updated the desktop.
            if (_desktopNeedsSend && _desktop is not null)
            {
                _desktopNeedsSend = false;
                _desktop.Render();
                await SendDesktopAsync(ct);
            }
        }
    }

    /// <summary>Performs a Deactivation-Reactivation Sequence (MS-RDPBCGR 1.3.1.3) to change the
    /// desktop resolution: Deactivate All → Demand Active at the new size → wait for the client's
    /// Confirm Active + Font List → finalization, then resize the framebuffer and repaint.</summary>
    private async Task ReactivateAsync(int width, int height, CancellationToken ct)
    {
        log.LogInformation("Reactivation: {OldW}x{OldH} → {W}x{H}.", _width, _height, width, height);
        _width = width;
        _height = height;

        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Capabilities.BuildDeactivateAll()), ct);
        await WriteAsync(McsPdu.BuildSendDataIndication(Gcc.IoChannelId, Capabilities.BuildDemandActive(_width, _height)), ct);

        // Read the client's re-finalization: it re-sends Confirm Active then the Font List.
        while (true)
        {
            var frame = await ReadFrameAsync(ct);
            if (frame is null) { log.LogWarning("Client disconnected during reactivation."); return; }
            if (frame.Value.FastPath) { await HandleInputAsync(frame.Value.Header, frame.Value.Payload, ct); continue; }

            var mcs = Cotp.StripDataTpdu(frame.Value.Payload);
            if (McsPdu.ClassifyDomainPdu(mcs) != McsDomainPdu.SendDataRequest) continue;
            var (channelId, data) = McsPdu.ParseSendData(mcs);
            if (channelId != Gcc.IoChannelId) continue;                    // ignore VC traffic mid-resize
            if (ShareControl.PduType(data) == (ShareControl.Data & 0x0F)
                && Finalization.DataPduType2(data) == Finalization.Pdu2FontList)
            {
                await SendFinalizationAsync(ct);
                break;
            }
        }

        _desktop?.Resize(_width, _height);
        if (_desktop is not null) await SendDesktopAsync(ct);
        log.LogInformation("Reactivation complete — session ACTIVE at {W}x{H}.", _width, _height);
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

        // Slow-path input (TS_INPUT_PDU) on the I/O channel — mstsc/mstscax send input this way
        // rather than fast-path even when fast-path is advertised.
        if (channelId == Gcc.IoChannelId)
        {
            if (ShareControl.PduType(data) == (ShareControl.Data & 0x0F))
            {
                int type2 = Finalization.DataPduType2(data);
                if (type2 == Finalization.Pdu2Input)
                {
                    var events = Input.ParseSlowPath(data);
                    LogChannel("input", true, data.Length, InputSummary(events), render: false);
                    // Flags are normalised by ParseSlowPath: bit0 = release (up), bit1 = extended.
                    foreach (var ev in events)
                        if (ev.Type == InputEventType.Scancode)
                            log.LogInformation("Input key: scancode=0x{Code:X2} {UpDown}{Ext}", ev.Code,
                                (ev.Flags & 0x01) != 0 ? "up" : "down", (ev.Flags & 0x02) != 0 ? " ext" : "");
                        else if (ev.Type == InputEventType.Unicode)
                            log.LogInformation("Input unicode: U+{Code:X4}", ev.X);
                    if (_desktop is not null) await ApplyInputAsync(events, ct);
                }
                else if (type2 == Finalization.Pdu2ShutdownRequest)
                {
                    // The client (e.g. closing mstsc) asked to log off. Deny it, as a real host with
                    // no clean logoff does — mstsc then shows its "disconnect?" confirmation dialog.
                    log.LogInformation("Client sent Shutdown Request — denied (client will confirm).");
                    await WriteAsync(McsPdu.BuildSendDataIndication(
                        Gcc.IoChannelId, Finalization.BuildDataPdu(Finalization.Pdu2ShutdownDenied, default)), ct);
                }
                else
                {
                    log.LogDebug("I/O-channel Data PDU pduType2={Type2} (ignored).", type2);
                }
            }
            return;
        }

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
        LogChannel("cliprdr", true, clipPdu.Length, $"msgType 0x{Clipboard.ReadMsgType(clipPdu):X4}");
        switch (Clipboard.ReadMsgType(clipPdu))
        {
            case Clipboard.CbFormatList:
                await SendClipboardAsync(Clipboard.FormatListResponseOk(), ct);
                // Remember whether the client is offering files (for a later paste).
                _clientFileFormatId = Clipboard.ParseFormatListFileId(clipPdu);
                if (_clientFileFormatId != 0) log.LogInformation("Clipboard: client is offering file(s) (format id 0x{Id:X4}).", _clientFileFormatId);
                if (!_offeredServerClipboard)
                {
                    _offeredServerClipboard = true;
                    await SendClipboardAsync(Clipboard.FormatListWithFile(), ct);
                    log.LogInformation("Clipboard: offered CF_UNICODETEXT + a file ({File}).", _offeredFileName);
                }
                break;

            case Clipboard.CbFormatDataRequest:
                if (Clipboard.ReadFormatDataRequestId(clipPdu) == Clipboard.FileGroupDescriptorId)
                {
                    await SendClipboardAsync(Clipboard.FormatDataResponseFileList(_offeredFileName, _offeredFileBytes.Length), ct);
                    log.LogInformation("Clipboard: served file descriptor for '{File}' ({Size} bytes).",
                        _offeredFileName, _offeredFileBytes.Length);
                }
                else
                {
                    await SendClipboardAsync(Clipboard.FormatDataResponseText(Clipboard.ServedText), ct);
                    log.LogInformation("Clipboard: served text on format data request.");
                }
                break;

            case Clipboard.CbFilecontentsRequest:
            {
                var req = Clipboard.ReadFilecontentsRequest(clipPdu);
                if (req.WantSize)
                {
                    await SendClipboardAsync(Clipboard.FilecontentsResponse(req.StreamId,
                        BitConverter.GetBytes((ulong)_offeredFileBytes.Length)), ct);
                    log.LogInformation("Clipboard: served file size ({Size}) for stream {Stream}.",
                        _offeredFileBytes.Length, req.StreamId);
                }
                else
                {
                    int start = (int)Math.Min(req.Position, (ulong)_offeredFileBytes.Length);
                    int len = (int)Math.Min(req.Length, (uint)(_offeredFileBytes.Length - start));
                    await SendClipboardAsync(Clipboard.FilecontentsResponse(req.StreamId,
                        _offeredFileBytes.AsSpan(start, len)), ct);
                    log.LogInformation("Clipboard: served file bytes [{Start}..{End}) for stream {Stream}.",
                        start, start + len, req.StreamId);
                }
                break;
            }

            // ── inbound paste (mock is the requester): descriptor → contents ──
            case Clipboard.CbFormatDataResponse when _pasteState == PasteState.WaitingDescriptor:
                if (Clipboard.ReadFileGroupDescriptor(clipPdu) is { } fd && fd.Size >= 0)
                {
                    _pasteFileName = string.IsNullOrEmpty(fd.Name) ? "pasted.bin" : fd.Name;
                    uint want = (uint)Math.Min(fd.Size, 1 << 20);   // cap a paste at 1 MiB
                    _pasteState = PasteState.WaitingContents;
                    await SendClipboardAsync(Clipboard.FilecontentsRangeRequest(1, 0, 0, want), ct);
                    log.LogInformation("Clipboard: pasting '{File}' — requested {Bytes} bytes from the client.", _pasteFileName, want);
                }
                else { _pasteState = PasteState.Idle; log.LogInformation("Clipboard: paste — couldn't parse the client's file descriptor."); }
                break;

            case Clipboard.CbFilecontentsResponse when _pasteState == PasteState.WaitingContents:
            {
                var bytes = Clipboard.ReadFilecontentsResponseData(clipPdu);
                _pasteState = PasteState.Idle;
                log.LogInformation("Clipboard: received '{File}' ({Bytes} bytes) from the client.", _pasteFileName, bytes.Length);
                if (_desktop is not null)
                {
                    _desktop.DeliverClientFile(_pasteFileName, bytes);
                    foreach (var note in _desktop.TakeEvents()) log.LogInformation("Desktop: {Note}", note);
                    _desktop.Render();
                    await SendDesktopAsync(ct);
                }
                break;
            }

            case Clipboard.CbClipCaps:
                log.LogDebug("Clipboard: client capabilities received.");
                break;
        }
    }

    private Task SendClipboardAsync(byte[] cliprdrPdu, CancellationToken ct)
    {
        LogChannel("cliprdr", false, cliprdrPdu.Length, $"msgType 0x{Clipboard.ReadMsgType(cliprdrPdu):X4}");
        return WriteAsync(McsPdu.BuildSendDataIndication(_cliprdrChannelId, VirtualChannel.Wrap(cliprdrPdu)), ct);
    }

    /// <summary>Opens the DVC layer: advertise capabilities. Channels are created once the
    /// client answers with its capabilities response.</summary>
    private async Task InitDvcAsync(CancellationToken ct)
    {
        if (_drdynvcChannelId == 0) return;
        var caps = Dvc.BuildCapabilitiesV1();
        await SendDvcAsync(caps, ct);
        LogChannel("drdynvc", false, caps.Length, "server capabilities v1");
        log.LogInformation("DVC (drdynvc) ready on {Channel}: sent capabilities v1.", _drdynvcChannelId);
    }

    // Friendly channel label for the DVC Monitor (the Display Control name is very long).
    private static string DvcLabel(string name) =>
        name == DisplayControl.ChannelName ? "DisplayControl" : name;

    /// <summary>Handles one inbound DRDYNVC PDU: capabilities response, create response,
    /// channel data (echoed back), or close.</summary>
    private async Task HandleDvcAsync(byte[] pdu, CancellationToken ct)
    {
        var msg = Dvc.Parse(pdu);
        switch (msg.Cmd)
        {
            case Dvc.Cmd.Capabilities:
                _dvcVersion = msg.Version;
                LogChannel("drdynvc", true, pdu.Length, $"client capabilities v{msg.Version}");
                log.LogInformation("DVC: client capabilities (version {Version}); opening {Count} channel(s).",
                    msg.Version, _dvcChannelNames.Length);
                foreach (var name in _dvcChannelNames)
                    await OpenDvcChannelAsync(name, ct);
                // In desktop mode, also open the Display Control channel so the client can drive
                // resolution changes (MS-RDPEDISP). Harmless if the client declines it.
                if (desktop)
                    await OpenDvcChannelAsync(DisplayControl.ChannelName, ct);
                break;

            case Dvc.Cmd.Create: // inbound Create is a create RESPONSE
                var name0 = _dvcPending.Remove(msg.ChannelId, out var pending) ? pending : $"#{msg.ChannelId}";
                int status = Dvc.CreationStatus(msg);
                LogChannel(DvcLabel(name0), true, pdu.Length, status == 0 ? $"create OK (id {msg.ChannelId})" : $"create rejected 0x{(uint)status:X8}");
                if (status == 0)
                {
                    _dvcOpen[msg.ChannelId] = name0;
                    log.LogInformation("DVC: channel '{Name}' (id {Id}) opened by client.", name0, msg.ChannelId);
                    if (name0 == DisplayControl.ChannelName)
                    {
                        _displayControlId = msg.ChannelId;
                        var caps = DisplayControl.BuildCapsPdu();
                        foreach (var p in Dvc.BuildData(msg.ChannelId, caps))
                            await SendDvcAsync(p, ct);
                        LogChannel("DisplayControl", false, caps.Length, "caps PDU");
                        log.LogInformation("Display Control ready on DVC id {Id}: sent caps.", msg.ChannelId);
                    }
                    else if (_dvcPendingSends.Remove(name0, out var queued))
                    {
                        // A DVC Console channel just opened — flush the queued messages, and don't echo its data.
                        _consoleChannels.Add(msg.ChannelId);
                        foreach (var text in queued)
                            await SendDvcTextAsync(msg.ChannelId, name0, text, ct);
                    }
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
                {
                    LogChannel(DvcLabel(closed), true, pdu.Length, "close");
                    log.LogInformation("DVC: client closed channel '{Name}' (id {Id}).", closed, msg.ChannelId);
                }
                _dvcReasm.Remove(msg.ChannelId);
                break;
        }
    }

    /// <summary>Sends a DVC Console message on the named channel, opening it first if needed.</summary>
    private async Task SendDvcConsoleAsync(string channel, string text, CancellationToken ct)
    {
        var open = _dvcOpen.FirstOrDefault(o => o.Value == channel);
        if (open.Value == channel) { await SendDvcTextAsync(open.Key, channel, text, ct); return; }

        // Not open yet: queue the message and open the channel; the create response flushes the queue.
        if (!_dvcPendingSends.TryGetValue(channel, out var q))
        {
            q = new List<string>();
            _dvcPendingSends[channel] = q;
            await OpenDvcChannelAsync(channel, ct);
        }
        q.Add(text);
    }

    private async Task SendDvcTextAsync(uint channelId, string channel, string text, CancellationToken ct)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(text);
        foreach (var p in Dvc.BuildData(channelId, payload))
            await SendDvcAsync(p, ct);
        var preview = text.Length > 24 ? text[..24] + "…" : text;
        LogChannel(DvcLabel(channel), false, payload.Length, $"console: \"{preview}\"");
        log.LogInformation("DVC console: sent {Count} bytes on '{Channel}'.", payload.Length, channel);
    }

    private async Task OpenDvcChannelAsync(string name, CancellationToken ct)
    {
        uint id = _nextDvcId++;
        _dvcPending[id] = name;
        var req = Dvc.BuildCreateRequest(id, name);
        await SendDvcAsync(req, ct);
        LogChannel(DvcLabel(name), false, req.Length, $"create request (id {id})");
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

        // Display Control (MS-RDPEDISP): a MONITOR_LAYOUT PDU asks for a resolution change.
        // Don't echo it — record the requested size; the serve loop performs the reactivation.
        if (msg.ChannelId == _displayControlId)
        {
            var size = DisplayControl.ParseMonitorLayout(complete);
            LogChannel("DisplayControl", true, complete.Length,
                size is { } sz ? $"monitor layout {sz.Width}×{sz.Height}" : "monitor layout");
            if (size is { } s && (s.Width != _width || s.Height != _height))
            {
                _pendingResize = s;
                log.LogInformation("Display Control: client requested {W}x{H}.", s.Width, s.Height);
            }
            return;
        }

        // Data the client sends back on a DVC Console channel: show it, but don't echo (avoid loops).
        if (_consoleChannels.Contains(msg.ChannelId))
        {
            LogChannel(DvcLabel(_dvcOpen[msg.ChannelId]), true, complete.Length, "data (from client)");
            return;
        }

        var name = _dvcOpen[msg.ChannelId];

        // RDPeek diagnostics channels: be a real protocol peer (Hello→Capabilities, Ping→Ping)
        // instead of echoing, so the plugin's handshake actually completes.
        if (name.StartsWith("dvc::diag::", StringComparison.Ordinal))
        {
            LogChannel(name, true, complete.Length, "diag request");
            var diagReply = DiagResponder.Respond(complete);
            if (diagReply is not null)
            {
                foreach (var p in Dvc.BuildData(msg.ChannelId, diagReply)) await SendDvcAsync(p, ct);
                LogChannel(name, false, diagReply.Length, "diag response");
                log.LogInformation("DVC diag: answered {In}-byte request on '{Name}' with {Out} bytes.",
                    complete.Length, name, diagReply.Length);
            }
            return;
        }

        LogChannel(DvcLabel(name), true, complete.Length, "data");
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
        LogChannel(DvcLabel(name), false, reply.Length, how + (pdus.Count > 1 ? $" ({pdus.Count} fragments)" : ""));
    }

    private Task SendDvcAsync(byte[] dvcPdu, CancellationToken ct) =>
        WriteAsync(McsPdu.BuildSendDataIndication(_drdynvcChannelId, VirtualChannel.Wrap(dvcPdu)), ct);


    // ── Drive redirection (MS-RDPEFS / rdpdr) ───────────────────────────────

    /// <summary>Starts the rdpdr init handshake if any op is configured, or the fake desktop is
    /// on (so it can browse \\tsclient on demand).</summary>
    private async Task InitRdpdrAsync(CancellationToken ct)
    {
        if (_rdpdrChannelId == 0 || (_rdpdrOps.Count == 0 && _desktop is null)) return;
        await SendRdpdrAsync(Rdpdr.ServerAnnounceReq(1), ct);
        log.LogInformation("rdpdr ready on {Channel}: sent Server Announce.", _rdpdrChannelId);
    }

    /// <summary>Enqueues an on-demand rdpdr op (used by the desktop's \\tsclient browser) and
    /// starts it if the state machine is idle.</summary>
    private async Task EnqueueRdpdrAsync(RdpdrReq req, CancellationToken ct)
    {
        _rdpdrOps.Enqueue(req);
        if (_rdpdrPhase == RdpdrPhase.Idle) await StartNextRdpdrOpAsync(ct);
    }

    /// <summary>Drives the rdpdr init handshake, then reads the configured files from the client.</summary>
    private async Task HandleRdpdrAsync(byte[] pdu, CancellationToken ct)
    {
        log.LogDebug("rdpdr recv packetId=0x{Id:X4} ({Len} bytes).", Rdpdr.PacketId(pdu), pdu.Length);
        LogChannel("rdpdr", true, pdu.Length, $"packetId 0x{Rdpdr.PacketId(pdu):X4}");
        switch (Rdpdr.PacketId(pdu))
        {
            case Rdpdr.ClientIdConfirm: // client's Announce Reply — remember the ClientId it chose
                if (pdu.Length >= 12) _rdpdrClientId = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pdu.AsSpan(8, 4));
                break;

            case Rdpdr.ClientName: // follows the client's announce reply — now negotiate capabilities
                await SendRdpdrAsync(Rdpdr.ServerCapabilityReq(), ct);
                await SendRdpdrAsync(Rdpdr.ServerClientIdConfirm(_rdpdrClientId), ct);
                break;

            case Rdpdr.ClientCapability: // client accepted our capabilities — signal logon so it
                                         // announces its drives (some clients gate drives on this).
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
            var req = _rdpdrOps.Dequeue();
            var clientPath = req.Path;
            if (clientPath.Length < 2 || clientPath[1] != ':')
            {
                log.LogWarning("rdpdr: skipping non-drive path '{Path}'.", clientPath);
                req.Done?.Invoke(new(), []);
                continue;
            }
            char letter = char.ToUpperInvariant(clientPath[0]);
            if (!_rdpdrDrives.TryGetValue(letter, out var deviceId))
            {
                log.LogWarning("rdpdr: drive {Letter}: not redirected; cannot {Op} '{Path}'.", letter, req.Op, clientPath);
                req.Done?.Invoke(new(), []);
                continue;
            }

            var rel = clientPath[2..].Replace('/', '\\');
            rel = rel.Length == 0 ? "\\" : (rel.StartsWith('\\') ? rel : "\\" + rel);

            _rdpdrCurrent = req;
            _rdpdrPath = clientPath;
            _rdpdrRel = rel;
            _rdpdrDeviceId = deviceId;
            _rdpdrFileId = 0;
            _rdpdrOffset = 0;
            _rdpdrBytes = 0;
            _rdpdrEntries.Clear();
            _rdpdrReadBytes.Clear();
            _rdpdrHash = req.Op == RdpdrOp.Read ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
            _rdpdrPhase = RdpdrPhase.Create;

            var create = req.Op switch
            {
                RdpdrOp.List  => Rdpdr.CreateRequest(deviceId, ++_rdpdrCompletionId, rel, Rdpdr.AccessList,  Rdpdr.DispositionOpen,        Rdpdr.OptionsDirectory),
                RdpdrOp.Write => Rdpdr.CreateRequest(deviceId, ++_rdpdrCompletionId, rel, Rdpdr.AccessWrite, Rdpdr.DispositionOverwriteIf, Rdpdr.OptionsFile),
                _             => Rdpdr.CreateRequest(deviceId, ++_rdpdrCompletionId, rel),
            };
            await SendRdpdrAsync(create, ct);
            log.LogInformation("rdpdr: {Op} '{Path}' over the redirected drive.", req.Op, clientPath);
            return;
        }
        _rdpdrPhase = RdpdrPhase.Idle;
        _rdpdrCurrent = null;
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
                    _rdpdrCurrent?.Done?.Invoke(new(), []);
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
                    if (_rdpdrReadBytes.Count < RdpdrReadCap) _rdpdrReadBytes.AddRange(data.ToArray());
                    _rdpdrBytes += data.Length;
                    _rdpdrOffset += (ulong)data.Length;
                }
                if (data.Length < RdpdrReadChunk || _rdpdrBytes >= RdpdrReadCap) await FinishRdpdrOpAsync(ct);
                else await SendRdpdrAsync(Rdpdr.ReadRequest(_rdpdrDeviceId, _rdpdrFileId, ++_rdpdrCompletionId, RdpdrReadChunk, _rdpdrOffset), ct);
                break;

            case RdpdrOp.List:
                if (c.IoStatus == Rdpdr.StatusNoMoreFiles || c.IoStatus != 0) { await FinishRdpdrOpAsync(ct); return; }
                _rdpdrEntries.AddRange(Rdpdr.ParseDirEntries(Rdpdr.ReadData(c.Rest)));
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
                    _rdpdrPath, _rdpdrEntries.Count, string.Join(", ", _rdpdrEntries.Select(e => e.Name)));
                break;
            case RdpdrOp.Write:
                log.LogInformation("rdpdr: WROTE '{Path}' -> {Bytes} bytes.", _rdpdrPath, _rdpdrBytes);
                break;
        }

        _rdpdrCurrent?.Done?.Invoke(new List<(string, bool)>(_rdpdrEntries), _rdpdrReadBytes.ToArray());
        _rdpdrPhase = RdpdrPhase.Close;
        await SendRdpdrAsync(Rdpdr.CloseRequest(_rdpdrDeviceId, _rdpdrFileId, ++_rdpdrCompletionId), ct);
    }

    private Task SendRdpdrAsync(byte[] pdu, CancellationToken ct)
    {
        LogChannel("rdpdr", false, pdu.Length, $"packetId 0x{Rdpdr.PacketId(pdu):X4}");
        return WriteAsync(McsPdu.BuildSendDataIndication(_rdpdrChannelId, VirtualChannel.Wrap(pdu)), ct);
    }

    /// <summary>Decodes fast-path input; feeds the desktop, or draws markers in non-desktop mode.</summary>
    private async Task HandleInputAsync(byte fastPathHeader, byte[] payload, CancellationToken ct)
    {
        var events = Input.ParseFastPath(fastPathHeader, payload);
        LogChannel("input", true, payload.Length, InputSummary(events), render: false);
        if (_desktop is not null) { await ApplyInputAsync(events, ct); return; }

        foreach (var ev in events)
            if (ev.Type == InputEventType.Mouse) await DrawMarkerAsync(ev.X, ev.Y, ct);
    }

    private readonly System.Diagnostics.Stopwatch _desktopClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Applies input events to the fake desktop and resends what changed. While a
    /// window is being dragged, frames are coalesced to ~30fps (dirty-rect accumulates the
    /// changed tiles across the skipped sends, and the final position sends on mouse-up).</summary>
    private async Task ApplyInputAsync(IReadOnlyList<InputEvent> events, CancellationToken ct)
    {
        if (_desktop is null || events.Count == 0) return;
        bool changed = false;
        foreach (var ev in events)
            changed |= _desktop.OnInput(ev);

        // An Explorer \\tsclient click may have queued an on-demand rdpdr op — start it.
        if (_rdpdrKick) { _rdpdrKick = false; if (_rdpdrPhase == RdpdrPhase.Idle) await StartNextRdpdrOpAsync(ct); }

        // The user picked a resolution in Display settings — schedule a server-initiated
        // Deactivation-Reactivation (the serve loop applies it after this input returns).
        if (_desktop.TakeRequestedResize() is { } r) _pendingResize = r;

        // Drain the desktop's verification hooks (Win+R, app launches) to the logger.
        foreach (var note in _desktop.TakeEvents())
            log.LogInformation("Desktop: {Note}", note);

        // Clipboard file transfer driven from the desktop.
        if (_cliprdrChannelId != 0)
        {
            // Mock → client: the user copied a file; (re)offer it on the mock's clipboard.
            if (_desktop.TakeClipboardCopy() is { } copied)
            {
                _offeredFileName = copied.Name;
                _offeredFileBytes = copied.Bytes;
                _offeredServerClipboard = true;
                await SendClipboardAsync(Clipboard.FormatListWithFile(), ct);
                log.LogInformation("Clipboard: offered copied file '{File}' ({Size} bytes) to the client.",
                    copied.Name, copied.Bytes.Length);
            }

            // Client → mock: the user pressed Ctrl+V; pull the client's file if it offered one.
            if (_desktop.TakeClipboardPasteRequest())
            {
                if (_clientFileFormatId != 0)
                {
                    _pasteState = PasteState.WaitingDescriptor;
                    await SendClipboardAsync(Clipboard.FormatDataRequest(_clientFileFormatId), ct);
                    log.LogInformation("Clipboard: paste requested — asking the client for its file descriptor.");
                }
                else
                {
                    log.LogInformation("Clipboard: paste requested, but the client has no file on its clipboard.");
                }
            }
        }

        // The user typed into a DVC Console — open the channel (if needed) and send the text.
        foreach (var (channel, text) in _desktop.TakeDvcSends())
            await SendDvcConsoleAsync(channel, text, ct);

        // The user just signed in at the logon screen — send the Server Save Session Info
        // ("logon") PDU (MS-RDPBCGR 2.2.10.1) before painting the desktop, as a real host would.
        if (_desktop.JustSignedIn)
        {
            _desktop.JustSignedIn = false;
            await WriteAsync(McsPdu.BuildSendDataIndication(
                Gcc.IoChannelId, Finalization.BuildSaveSessionInfo("MOCK", _desktop.LogonUser, 1)), ct);
            log.LogInformation("Sent Save Session Info (logon) PDU for user '{User}'.", _desktop.LogonUser);
        }

        if (!changed) return;
        if (_desktop.IsDragging && _desktopClock.ElapsedMilliseconds < 33) return; // coalesce drag frames
        _desktopClock.Restart();
        _desktop.Render();          // render only when we actually send (not per move event)
        await SendDesktopAsync(ct);
    }

    /// <summary>Draws a small marker square at the cursor position (clamped to the desktop).</summary>
    private async Task DrawMarkerAsync(ushort x, ushort y, CancellationToken ct)
    {
        const int size = 16;
        int mx = Math.Clamp((int)x, 0, _width - size);
        int my = Math.Clamp((int)y, 0, _height - size);
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
