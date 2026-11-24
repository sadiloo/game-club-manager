using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using GameClub.Kiosk.Core.Guard;

namespace GameClub.Kiosk.Guard.Internet;

internal static class InternetGuardCli
{
    public static int RunConfigure(string[] args)
    {
        var devicePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Checkpoint",
            "kiosk",
            "device.json");
        if (!File.Exists(devicePath))
        {
            Console.Error.WriteLine("device.json missing");
            return 2;
        }

        var device = JsonSerializer.Deserialize<DeviceJson>(File.ReadAllText(devicePath)) ?? new DeviceJson();
        if (string.IsNullOrWhiteSpace(device.StationId) || string.IsNullOrWhiteSpace(device.ServerBase))
        {
            Console.Error.WriteLine("invalid device.json");
            return 2;
        }

        var uri = new Uri(device.ServerBase);
        var ips = Dns.GetHostAddresses(uri.Host).Select(a => a.ToString()).Distinct().ToArray();
        var config = new InternetGuardConfig
        {
            StationId = device.StationId,
            ServerHost = uri.Host,
            ServerPort = uri.Port > 0 ? uri.Port : uri.Scheme == "https" ? 443 : 80,
            ServerIps = ips,
            ServerPublicKeyPem = device.LeaseVerifyPublicKeyPem ?? "",
            ExtraAllowPrograms = ParseExtra(args),
        };
        Directory.CreateDirectory(InternetGuardPaths.ProgramDataGuard);
        File.WriteAllText(InternetGuardPaths.ConfigPath, JsonSerializer.Serialize(config));
        return 0;
    }

    public static int RunReset()
    {
        InternetGuardConfig? config = null;
        if (File.Exists(InternetGuardPaths.ConfigPath))
        {
            config = JsonSerializer.Deserialize<InternetGuardConfig>(File.ReadAllText(InternetGuardPaths.ConfigPath));
        }

        var applier = new InternetFirewallApplier();
        var code = config is not null ? applier.Apply(config, on: true) : applier.RunResetScript();
        if (!string.Equals(code, "applied", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(code);
            return 1;
        }

        if (File.Exists(InternetGuardPaths.StatePath))
        {
            File.Delete(InternetGuardPaths.StatePath);
        }

        return 0;
    }

    private static string[] ParseExtra(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--extraAllowPrograms" && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return args[i + 1].Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        return Array.Empty<string>();
    }

    private sealed class DeviceJson
    {
        public string StationId { get; set; } = "";
        public string ServerBase { get; set; } = "";
        public string? LeaseVerifyPublicKeyPem { get; set; }
    }
}