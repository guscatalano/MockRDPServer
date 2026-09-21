using MockRdp.Tray;

Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
using var app = new TrayApp();
Application.Run();
