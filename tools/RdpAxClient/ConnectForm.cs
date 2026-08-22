using System.Windows.Forms;

namespace RdpAxClient;

internal sealed record Options(string Host, int Port, string User, string Password, int TimeoutSeconds, int AuthLevel);

/// <summary>
/// Hosts the mstscax control, connects to the target, and reports the outcome. Connection
/// progress is polled via the control's <c>Connected</c> property; the process exits 0 once
/// the session becomes active, or 1 on timeout/error (with the extended disconnect reason).
/// </summary>
internal sealed class ConnectForm : Form
{
    private readonly Options _opts;
    private readonly RdpAxControl _rdp;
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 150 };
    private DateTime _deadline;
    private bool _done;

    public int ExitCode { get; private set; } = 1;

    public ConnectForm(Options opts, string clsid, string controlName)
    {
        _opts = opts;
        Text = $"RdpAxClient — {controlName}";
        Width = 1040;
        Height = 810;
        StartPosition = FormStartPosition.CenterScreen;

        _rdp = new RdpAxControl(clsid) { Dock = DockStyle.Fill };
        Controls.Add(_rdp);

        Load += OnLoad;
        _poll.Tick += OnPoll;
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        _deadline = DateTime.UtcNow.AddSeconds(_opts.TimeoutSeconds);
        try
        {
            dynamic ocx = _rdp.Ocx;
            Set(() => ocx.Server = _opts.Host);
            Set(() => ocx.UserName = _opts.User);
            Set(() => ocx.DesktopWidth = 1024);
            Set(() => ocx.DesktopHeight = 768);

            dynamic adv = GetAdvancedSettings(ocx);
            Set(() => adv.RDPPort = _opts.Port);
            Set(() => adv.ClearTextPassword = _opts.Password);
            // Request the negotiation-based security layer so the SSL bit is offered; the mock
            // selects SSL, so TLS proceeds and NLA is never actually used.
            Set(() => adv.NegotiateSecurityLayer = true);
            // Level 1/2 authenticate over TLS (level 0 skips TLS and requests only standard RDP).
            Set(() => adv.AuthenticationLevel = _opts.AuthLevel);
            Set(() => adv.EnableAutoReconnect = false);
            Set(() => adv.GrabFocusOnConnect = false);

            TryPrint("NegotiateSecurityLayer", () => adv.NegotiateSecurityLayer);
            TryPrint("AuthenticationLevel", () => adv.AuthenticationLevel);

            Console.WriteLine($"Connecting to {_opts.Host}:{_opts.Port} as '{_opts.User}'...");
            ocx.Connect();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Finish(1, $"setup error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool _certAccepted;

    private void OnPoll(object? sender, EventArgs e)
    {
        if (_done) return;

        // Transiently accept the self-signed cert warning (persists nothing).
        if (!_certAccepted && Native.AcceptCertWarningIfPresent())
        {
            _certAccepted = true;
            Console.WriteLine("Accepted server certificate warning (nothing persisted).");
        }

        try
        {
            dynamic ocx = _rdp.Ocx;
            if ((int)ocx.Connected == 1) { Finish(0, "CONNECTED — session active"); return; }
        }
        catch { /* control may be mid-transition */ }

        if (DateTime.UtcNow >= _deadline)
        {
            string reason = "";
            try { dynamic ocx = _rdp.Ocx; reason = $" extendedDisconnectReason={ocx.ExtendedDisconnectReason}"; }
            catch { }
            Finish(1, $"TIMEOUT — not connected within {_opts.TimeoutSeconds}s.{reason}");
        }
    }

    private void Finish(int code, string message)
    {
        if (_done) return;
        _done = true;
        _poll.Stop();
        ExitCode = code;
        Console.WriteLine($"[{(code == 0 ? "OK" : "FAIL")}] {message}");
        try { dynamic ocx = _rdp.Ocx; if ((int)ocx.Connected == 1) ocx.Disconnect(); } catch { }
        BeginInvoke(Close);
    }

    /// <summary>Returns the highest-versioned AdvancedSettings interface the control exposes.</summary>
    private static dynamic GetAdvancedSettings(dynamic ocx)
    {
        foreach (var prop in new[] { "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7",
                                     "AdvancedSettings6", "AdvancedSettings5", "AdvancedSettings3", "AdvancedSettings2" })
        {
            try { return ocx.GetType().InvokeMember(prop, System.Reflection.BindingFlags.GetProperty, null, ocx, null); }
            catch { }
        }
        return ocx.AdvancedSettings; // base
    }

    private static void Set(Action set)
    {
        try { set(); }
        catch (Exception ex) { Console.WriteLine($"  (skipped a setting: {ex.GetType().Name})"); }
    }

    private static void TryPrint(string label, Func<object> read)
    {
        try { Console.WriteLine($"  {label} = {read()}"); }
        catch (Exception ex) { Console.WriteLine($"  {label} = <{ex.GetType().Name}>"); }
    }
}
