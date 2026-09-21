using System.Drawing;
using System.Windows.Forms;
using MockRdp.Desktop;

namespace MockRdp.Tray;

/// <summary>
/// Browses the mock server's in-memory filesystem (the same <see cref="Vfs"/> tree a connected
/// session's File Explorer shows) directly from the tray — a tree of folders/files on the left, the
/// selected file's content on the right. The tree is a fresh <see cref="Vfs.BuildDefault"/>: the
/// server rebuilds this per session, so this shows exactly what a client would see on connect.
/// </summary>
internal sealed class FileBrowserWindow : Form
{
    private readonly TreeView _tree;
    private readonly TextBox _preview;
    private readonly Label _pathLabel;
    private readonly VfsNode _root;

    public FileBrowserWindow(VfsNode root)
    {
        _root = root;
        Text = "Mock RDP — server files (in-memory C:\\)";
        Width = 820;
        Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Branding.MakeIcon(32); } catch { /* icon is cosmetic */ }

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, PathSeparator = "\\" };
        _preview = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            BackColor = Color.FromArgb(0x12, 0x12, 0x12),
            ForeColor = Color.Gainsboro,
            Font = new Font("Consolas", 9.5f),
        };
        _pathLabel = new Label { Dock = DockStyle.Top, Height = 22, Padding = new Padding(6, 4, 0, 0), ForeColor = Color.DimGray };

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 300 };
        split.Panel1.Controls.Add(_tree);
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_preview);   // fill
        right.Controls.Add(_pathLabel); // top
        split.Panel2.Controls.Add(right);

        var banner = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 6, 8, 0),
            Text = "The mock's in-memory C:\\ drive, shared with the live session — a file pasted onto "
                 + "the desktop lands under Desktop (hit Refresh). \\\\tsclient (the client's real drives) isn't shown.",
            ForeColor = Color.FromArgb(0x40, 0x40, 0x40),
        };

        Controls.Add(split);    // fill
        Controls.Add(banner);   // top

        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };
        toolbar.Items.Add(new ToolStripButton("Expand all", null, (_, _) => _tree.ExpandAll()));
        toolbar.Items.Add(new ToolStripButton("Collapse all", null, (_, _) => { _tree.CollapseAll(); _tree.Nodes[0].Expand(); }));
        toolbar.Items.Add(new ToolStripButton("Refresh", null, (_, _) => Load()));
        Controls.Add(toolbar);  // top (above banner)

        _tree.AfterSelect += (_, e) => ShowNode(e.Node?.Tag as VfsNode);
        Load();

        // Auto-refresh: when the window regains focus, and on a poll if the tree changed (e.g. a
        // pasted file) — both re-read the live shared VFS without disturbing expansion/selection.
        _sig = Signature(_root);
        Activated += (_, _) => RefreshIfChanged();
        _poll.Tick += (_, _) => RefreshIfChanged();
        _poll.Start();
        FormClosed += (_, _) => _poll.Stop();
    }

    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 1000 };
    private int _sig;

    private void RefreshIfChanged()
    {
        if (IsDisposed) return;
        int sig = Signature(_root);
        if (sig == _sig) return;
        _sig = sig;
        Load();
    }

    private void Load()
    {
        // Preserve what the user has open/selected across a reload.
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectExpanded(_tree.Nodes, expanded);
        var selectedPath = _tree.SelectedNode?.FullPath;
        bool first = _tree.Nodes.Count == 0;

        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var rootNode = BuildNode(_root);   // the live shared tree — re-read after a paste
        _tree.Nodes.Add(rootNode);

        if (first)
        {
            rootNode.Expand();
            foreach (TreeNode n in rootNode.Nodes)
                if (n.Text == "Users") { n.Expand(); foreach (TreeNode u in n.Nodes) u.Expand(); }
        }
        else
        {
            RestoreExpanded(_tree.Nodes, expanded);
            if (!rootNode.IsExpanded) rootNode.Expand();
        }
        _tree.EndUpdate();

        var pick = selectedPath is null ? null : FindByPath(_tree.Nodes, selectedPath);
        _tree.SelectedNode = pick ?? rootNode;
        ShowNode((_tree.SelectedNode?.Tag) as VfsNode);
    }

    private static void CollectExpanded(TreeNodeCollection nodes, HashSet<string> into)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.IsExpanded) into.Add(n.FullPath);
            CollectExpanded(n.Nodes, into);
        }
    }

    private static void RestoreExpanded(TreeNodeCollection nodes, HashSet<string> expanded)
    {
        foreach (TreeNode n in nodes)
        {
            if (expanded.Contains(n.FullPath)) n.Expand();
            RestoreExpanded(n.Nodes, expanded);
        }
    }

    private static TreeNode? FindByPath(TreeNodeCollection nodes, string path)
    {
        foreach (TreeNode n in nodes)
        {
            if (string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase)) return n;
            if (FindByPath(n.Nodes, path) is { } hit) return hit;
        }
        return null;
    }

    /// <summary>A content signature of the tree (names, dir/file, file text) so a paste or edit is
    /// detected cheaply on the poll timer.</summary>
    private static int Signature(VfsNode root)
    {
        var hc = new HashCode();
        void Walk(VfsNode x)
        {
            hc.Add(x.Name);
            hc.Add(x.IsDir);
            if (!x.IsDir) hc.Add(x.Text);
            foreach (var c in x.Children) Walk(c);
        }
        Walk(root);
        return hc.ToHashCode();
    }

    private static TreeNode BuildNode(VfsNode v)
    {
        var node = new TreeNode(v.Name == "C:" ? "C:\\" : v.Name) { Tag = v };
        // Directories first, then files; each alphabetical.
        foreach (var child in v.Children.OrderByDescending(c => c.IsDir).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            node.Nodes.Add(BuildNode(child));
        return node;
    }

    private void ShowNode(VfsNode? v)
    {
        if (v is null) { _pathLabel.Text = ""; _preview.Clear(); return; }
        _pathLabel.Text = v.Path;
        if (v.IsDir)
        {
            var dirs = v.Children.Count(c => c.IsDir);
            var files = v.Children.Count - dirs;
            var lines = v.Children
                .OrderByDescending(c => c.IsDir).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => (c.IsDir ? "[DIR]  " : "       ") + c.Name);
            _preview.Text = $"{v.Path}   —   {dirs} folder(s), {files} file(s)\r\n\r\n"
                          + string.Join("\r\n", lines);
        }
        else if (v.GeneratedSize > 0)
        {
            _preview.Text = $"[generated file — {v.GeneratedSize:N0} bytes]\r\n\r\n"
                          + "Streamed on the fly (not stored). On the desktop, select it and press "
                          + "Ctrl+C to copy it to the client and watch the transfer progress.";
        }
        else
        {
            _preview.Text = v.Text;
        }
        _preview.SelectionStart = 0;
        _preview.SelectionLength = 0;
    }
}
