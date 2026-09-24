using EchoDvcClient;

// Minimal CLIENT-side DVC plugin (COM LocalServer32). mstsc activates it with -Embedding; run
// directly, it just prints usage. Runtime output → %TEMP%\echo-dvc-client.log.

bool embedding = args.Any(a =>
    a.TrimStart('-', '/').Equals("Embedding", StringComparison.OrdinalIgnoreCase));

if (embedding)
    return PluginHost.RunServer();

Console.WriteLine("EchoDvcClient — a minimal client-side RDP DVC plugin (COM LocalServer32).");
Console.WriteLine();
Console.WriteLine($"Channel : {EchoClientPlugin.Channel}   (matches samples/UppercaseDvcPlugin)");
Console.WriteLine($"CLSID   : {{{PluginHost.ClsidString}}}");
Console.WriteLine();
Console.WriteLine("mstsc activates this exe; you don't run it directly. To install (per-user, no admin):");
Console.WriteLine("  .\\register.ps1 -ExePath \"<path to this exe>\"");
Console.WriteLine("Then connect mstsc to the mock (with the Uppercase server plugin loaded).");
Console.WriteLine($"Runtime log: {Logger.Path}");
return 0;
