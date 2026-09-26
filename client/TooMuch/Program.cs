using Microsoft.Win32;
using System.ServiceProcess;
using TooMuch.Client;

namespace TooMuch;

internal static class Program
{
    static void Main(string[] args)
    {
        if (args.FirstOrDefault() != "--service") return;
        ServiceBase.Run(new AgentService());
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
