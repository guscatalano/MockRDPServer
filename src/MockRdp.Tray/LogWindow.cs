using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace MockRdp.Tray;

/// <summary>
/// A live activity monitor for the tray: shows the server's log stream (connection lifecycle, DVC
/// opens, diag answers, input, clipboard, desktop events…) as it happens. Preloads the recent
/// history from the <see cref="ActivityLog"/> ring buffer, then appends new lines. Auto-scrolls
/// unless the user scrolls up; Pause freezes the view; Clear empties the buffer.
/// </summary>
internal sealed class LogWindow : Form
{
    private const int MaxChars = 400_000;   // trim the box so it can't grow without bound

    private readonly ActivityLog _log;
    private readonly Action<string> _onLine;
    private readonly TextBox _view;
    private readonly CheckBox _autoScroll;
    private readonly CheckBox _pause;

    public LogWindow(ActivityLog log)
    {
        _log = log;

        Text = "Mock RDP — activity log";
        Width = 860;
        Height = 500;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        MinimizeBox = true;
        try { Icon = Branding.MakeIcon(32); } catch { /* icon is cosmetic */ }

        _view = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BackColor = Color.FromArgb(0x12, 0x12, 0x12),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9f),
            HideSelection = false,
        };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(4, 3, 0, 0) };
        _autoScroll = new CheckBox { Text = "Auto-scroll", Checked = true, AutoSize = true, Margin = new Padding(4, 4, 12, 0) };
        _pause = new CheckBox { Text = "Pause", Checked = false, AutoSize = true, Margin = new Padding(4, 4, 12, 0) };
        var clear = new Button { Text = "Clear", AutoSize = true, Margin = new Padding(4, 1, 4, 0) };
        clear.Click += (_, _) => { _log.Clear(); _view.Clear(); };
        var copy = new Button { Text = "Copy all", AutoSize = true, Margin = new Padding(4, 1, 4, 0) };
        copy.Click += (_, _) => { if (_view.TextLength > 0) Clipboard.SetText(_view.Text); };
        bar.Controls.AddRange(new Control[] { _autoScroll, _pause, clear, copy });

        Controls.Add(_view);   // fill first (lowest z)
        Controls.Add(bar);     // top

        // Preload history, then subscribe for live lines.
        var sb = new StringBuilder();
        foreach (var line in _log.Snapshot()) sb.AppendLine(line);
        _view.Text = sb.ToString();
        ScrollToEnd();

        _onLine = Append;
        _log.LineWritten += _onLine;
        FormClosing += (_, _) => _log.LineWritten -= _onLine;
    }

    private void Append(string line)
    {
        if (IsDisposed || Disposing) return;
        if (InvokeRequired) { try { BeginInvoke(() => Append(line)); } catch { /* closing */ } return; }
        if (_pause.Checked) return;

        // Keep the box bounded — drop the oldest chunk when it gets large.
        if (_view.TextLength > MaxChars)
        {
            var text = _view.Text;
            _view.Text = text[(text.Length / 2)..];
        }

        _view.AppendText(line + Environment.NewLine);
        if (_autoScroll.Checked) ScrollToEnd();
    }

    private void ScrollToEnd()
    {
        _view.SelectionStart = _view.TextLength;
        _view.SelectionLength = 0;
        _view.ScrollToCaret();
    }
}
