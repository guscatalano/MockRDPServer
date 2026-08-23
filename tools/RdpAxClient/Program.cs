using System.Windows.Forms;

namespace RdpAxClient;

// A minimal mstsc-equivalent: hosts the mstscax ActiveX control and connects to a target.
// Usage: RdpAxClient [--server H] [--port N] [--user U] [--pass P] [--timeout S]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var opts = ParseArgs(args);

        const string progId = "MsTscAx.MsTscAx";
        var type = Type.GetTypeFromProgID(progId)
            ?? throw new InvalidOperationException($"RDP control ProgID '{progId}' is not registered.");
        string clsid = type.GUID.ToString("B");
        Console.WriteLine($"Using control {progId} {clsid}");

        Application.EnableVisualStyles();
        using var form = new ConnectForm(opts, clsid, progId);
        Application.Run(form);

        Console.WriteLine($"Exit code {form.ExitCode}");
        return form.ExitCode;
    }

    private static Options ParseArgs(string[] args)
    {
        string host = "127.0.0.1", user = "test", pass = "test";
        int port = 3389, timeout = 20, authLevel = 2, hold = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--server" or "-s": host = args[++i]; break;
                case "--port" or "-p": port = int.Parse(args[++i]); break;
                case "--user" or "-u": user = args[++i]; break;
                case "--pass": pass = args[++i]; break;
                case "--timeout" or "-t": timeout = int.Parse(args[++i]); break;
                case "--auth-level": authLevel = int.Parse(args[++i]); break;
                case "--hold": hold = int.Parse(args[++i]); break;
            }
        }
        return new Options(host, port, user, pass, timeout, authLevel, hold);
    }
}
