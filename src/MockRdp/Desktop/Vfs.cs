namespace MockRdp.Desktop;

/// <summary>A node in the fake in-memory filesystem: a directory (with children) or a file
/// (with text content). Surfaced by the File Explorer / Notepad windows on the fake desktop.</summary>
public sealed class VfsNode
{
    public required string Name { get; init; }
    public bool IsDir { get; init; }
    public string Text { get; set; } = "";
    /// <summary>&gt; 0 for a large "streamed" file whose bytes are generated on the fly (not stored),
    /// so a multi-megabyte file costs no memory. <see cref="Text"/> is ignored for these.</summary>
    public long GeneratedSize { get; init; }
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

    /// <summary>Adds a large file whose <paramref name="size"/> bytes are generated on demand.</summary>
    public VfsNode AddLargeFile(string name, long size)
    {
        var n = new VfsNode { Name = name, IsDir = false, GeneratedSize = size, Parent = this };
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

        c.AddFile("README.txt",
            "mock-rdp — fake desktop\r\n" +
            "========================\r\n\r\n" +
            "This is a MOCK RDP server. Its filesystem (this C:\\ drive) is entirely\r\n" +
            "IN-MEMORY: nothing here is written to any real disk.\r\n\r\n" +
            "Run standalone it is per-session (rebuilt on connect). Under the systray\r\n" +
            "app it is shared with the tray's file browser, so a file you paste onto\r\n" +
            "the Desktop shows up there.\r\n\r\n" +
            "\\\\tsclient shows your own machine's redirected drives (that IS your real\r\n" +
            "filesystem, read over the RDP drive-redirection channel) — the mock only\r\n" +
            "reads it on demand and keeps nothing.\r\n");

        var win = c.AddDir("Windows");
        var sys32 = win.AddDir("System32");
        // A long, obviously-scrollable listing (drag the scrollbar or use the wheel).
        foreach (var name in new[]
        {
            "kernel32.dll", "user32.dll", "gdi32.dll", "ntdll.dll", "advapi32.dll", "shell32.dll",
            "ole32.dll", "comctl32.dll", "ws2_32.dll", "crypt32.dll", "rpcrt4.dll", "msvcrt.dll",
            "cmd.exe", "notepad.exe", "calc.exe", "mstsc.exe", "explorer.exe", "svchost.exe",
            "taskmgr.exe", "regedit.exe", "services.exe", "lsass.exe", "conhost.exe", "dwm.exe",
            "drivers", "en-US", "config", "spool", "wbem", "WindowsPowerShell",
        })
            if (name.Contains('.')) sys32.AddFile(name, $"(mock stub for {name})");
            else sys32.AddDir(name);
        win.AddFile("win.ini", "; for 16-bit app support\r\n[fonts]\r\n[extensions]\r\n");

        var users = c.AddDir("Users");
        var user = users.AddDir("rdpuser");
        var docs = user.AddDir("Documents");
        docs.AddFile("readme.txt",
            "Hello from the mock RDP desktop.\r\n\r\nThis file lives in an in-memory VFS and\r\nis served by the fake File Explorer.");
        docs.AddFile("notes.txt", "- wire up the desktop\r\n- add a filesystem\r\n- open files in Notepad\r\n- profit");
        var dl = user.AddDir("Downloads");
        // A big generated file: select it and Ctrl+C to copy it to the client and watch the progress bar.
        dl.AddLargeFile("large-sample.dat", 32L * 1024 * 1024);   // 32 MiB, streamed on the fly
        user.AddDir("Desktop");

        var pf = c.AddDir("Program Files");
        pf.AddDir("mock-rdp").AddFile("VERSION.txt", "mock-rdp fake desktop\r\nphase 3");

        c.AddFile("autoexec.bat", "@echo off\r\necho fake machine\r\n");
        return c;
    }

    /// <summary>Fills <paramref name="buf"/> with the deterministic content of a generated file at
    /// byte <paramref name="position"/> — a repeating printable line, so any range is reproducible
    /// without storing the file.</summary>
    public static void FillGenerated(Span<byte> buf, long position)
    {
        var line = System.Text.Encoding.ASCII.GetBytes(
            "mock-rdp large sample file — this content is generated on the fly to demo transfer progress.\r\n");
        for (int i = 0; i < buf.Length; i++)
            buf[i] = line[(int)((position + i) % line.Length)];
    }

    /// <summary>The default starting folder for File Explorer (the user's home).</summary>
    public static VfsNode Home(VfsNode root) =>
        root.Children.FirstOrDefault(u => u.Name == "Users")?
            .Children.FirstOrDefault(x => x.Name == "rdpuser") ?? root;
}
