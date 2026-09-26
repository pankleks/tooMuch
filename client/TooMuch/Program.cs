using Microsoft.Win32;
using System.ServiceProcess;
using TooMuch.Client;

namespace TooMuch;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "--service":
                ServiceBase.Run(new AgentService());
                break;
            case "--tray":
                ApplicationConfiguration.Initialize();
                Application.Run(new StatusTrayContext());
                break;
            case "--message":
                if (args.Length != 3 || !uint.TryParse(args[2], out var timeout))
                {
                    Environment.ExitCode = 2;
                    break;
                }
                ApplicationConfiguration.Initialize();
                Environment.ExitCode = MessageDialog.Run(args[1], timeout);
                break;
        }
    }

    internal static AgentConfig LoadConfig()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\TooMuch")
            ?? throw new InvalidOperationException("Run install.ps1 first.");
        var cfg = new AgentConfig
        {
            ServerUrl = key.GetValue("ServerUrl") as string ?? "",
            DeviceId = key.GetValue("DeviceId") as string ?? "",
            Token = key.GetValue("Token") as string ?? "",
            ChildSid = key.GetValue("ChildSid") as string ?? "",
        };
        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) || string.IsNullOrWhiteSpace(cfg.DeviceId)
            || string.IsNullOrWhiteSpace(cfg.Token) || string.IsNullOrWhiteSpace(cfg.ChildSid))
            throw new InvalidOperationException("Incomplete installation: server, device identity and child SID required.");
        return cfg;
    }
}
