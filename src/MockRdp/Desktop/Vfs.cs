namespace MockRdp.Desktop;

/// <summary>A node in the fake in-memory filesystem: a directory (with children) or a file
/// (with text content). Surfaced by the File Explorer / Notepad windows on the fake desktop.</summary>
public sealed class VfsNode
{
    public required string Name { get; init; }
    public bool IsDir { get; init; }
    public string Text { get; set; } = "";
    public VfsNode? Parent { get; private set; }
    public List<VfsNode> Children { get; } = new();

    public VfsNode AddDir(string name)
    {
        var n = new VfsNode { Name = name, IsDir = true, Parent = this };
        Children.Add(n);
        return n;
    }

    public VfsNode AddFile(string name, string text)
    {
        var n = new VfsNode { Name = name, IsDir = false, Text = text, Parent = this };
        Children.Add(n);
        return n;
    }

    /// <summary>Windows-style path, e.g. <c>C:\Users\rdpuser</c>.</summary>
    public string Path
    {
        get
        {
            if (Parent is null) return Name + "\\";     // drive root "C:\"
            var p = Parent.Path;
            return p.EndsWith('\\') ? p + Name : p + "\\" + Name;
        }
    }
}

/// <summary>Builds a small Windows-ish filesystem tree for the fake desktop.</summary>
public static class Vfs
{
    public static VfsNode BuildDefault()
    {
        var c = new VfsNode { Name = "C:", IsDir = true };

        var win = c.AddDir("Windows");
        win.AddDir("System32");
        win.AddFile("win.ini", "; for 16-bit app support\r\n[fonts]\r\n[extensions]\r\n");

        var users = c.AddDir("Users");
        var user = users.AddDir("rdpuser");
        var docs = user.AddDir("Documents");
        docs.AddFile("readme.txt",
            "Hello from the mock RDP desktop.\r\n\r\nThis file lives in an in-memory VFS and\r\nis served by the fake File Explorer.");
        docs.AddFile("notes.txt", "- wire up the desktop\r\n- add a filesystem\r\n- open files in Notepad\r\n- profit");
        user.AddDir("Downloads");
        user.AddDir("Desktop");

        var pf = c.AddDir("Program Files");
        pf.AddDir("mock-rdp").AddFile("VERSION.txt", "mock-rdp fake desktop\r\nphase 3");

        c.AddFile("autoexec.bat", "@echo off\r\necho fake machine\r\n");
        return c;
    }

    /// <summary>The default starting folder for File Explorer (the user's home).</summary>
    public static VfsNode Home(VfsNode root) =>
        root.Children.FirstOrDefault(u => u.Name == "Users")?
            .Children.FirstOrDefault(x => x.Name == "rdpuser") ?? root;
}
