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

    public FileBrowserWindow()
    {
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
            Text = "This is the mock's in-memory C:\\ drive — rebuilt per session and discarded on "
                 + "disconnect. \\\\tsclient (a client's real redirected drives) isn't shown here.",
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
    }

    private void Load()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var root = Vfs.BuildDefault();
        var rootNode = BuildNode(root);
        _tree.Nodes.Add(rootNode);
        rootNode.Expand();
        // Expand the user's home for convenience.
        foreach (TreeNode n in rootNode.Nodes)
            if (n.Text == "Users") { n.Expand(); foreach (TreeNode u in n.Nodes) u.Expand(); }
        _tree.EndUpdate();
        _tree.SelectedNode = rootNode;
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
        else
        {
            _preview.Text = v.Text;
        }
        _preview.SelectionStart = 0;
        _preview.SelectionLength = 0;
    }
}
