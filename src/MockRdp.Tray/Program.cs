using MockRdp.Tray;

// --logo <path.png> [size]: render the app logo to a PNG and exit (used to generate docs assets).
int logoIdx = Array.IndexOf(args, "--logo");
if (logoIdx >= 0 && logoIdx + 1 < args.Length)
{
    int size = logoIdx + 2 < args.Length && int.TryParse(args[logoIdx + 2], out var s) ? s : 256;
    Branding.SavePng(args[logoIdx + 1], size);
    Console.WriteLine($"Wrote logo ({size}px) to {args[logoIdx + 1]}");
    return;
}

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
using var app = new TrayApp();
Application.Run();
