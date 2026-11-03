using System.Text.Json;

namespace GameClub.Kiosk.Core;

public sealed class KioskConfig
{
    public string? HubUrl { get; set; }

    public string? CaThumbprint { get; set; }

    public string? StationToken { get; set; }

    public string? StationId { get; set; }

    public string? GroupId { get; set; }

    public long HourlyToman { get; set; }

    public bool Demo { get; set; }

    public List<string>? GameRoots { get; set; }
}

public sealed class KioskStartup
{
    public bool Run { get; init; }

    public bool Demo { get; init; }

    public string? HubUrl { get; init; }

    public string? CaThumbprint { get; init; }

    public string? StationToken { get; init; }

    public string LockMessage { get; init; } = "";

    public IReadOnlyList<string> GameRoots { get; init; } = LaunchPolicy.DefaultRoots;

    public static KioskStartup Decide(string? json, bool debugBuild, bool demoSwitch)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Locked("پیکربندی کیوسک پیدا نشد. فایل kiosk.json را بگذارید.");
        }

        KioskConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<KioskConfig>(json!);
        }
        catch (JsonException)
        {
            return Locked("فایل kiosk.json خوانده نشد.");
        }

        if (config is null)
        {
            return Locked("فایل kiosk.json خالی است.");
        }

        var roots = config.GameRoots is { Count: > 0 }
            ? (IReadOnlyList<string>)config.GameRoots
            : LaunchPolicy.DefaultRoots;

        if (config.Demo)
        {
            if (debugBuild || demoSwitch)
            {
                return new KioskStartup
                {
                    Run = true,
                    Demo = true,
                    LockMessage = "",
                    GameRoots = roots,
                };
            }

            return Locked("حالت نمایشی در ساخت نهایی خاموش است.");
        }

        var hubError = StationTrust.RejectHub(config.HubUrl, config.CaThumbprint);
        if (hubError is not null)
        {
            return Locked(hubError);
        }

        return new KioskStartup
        {
            Run = true,
            HubUrl = config.HubUrl!.Trim(),
            CaThumbprint = StationTrust.Normalize(config.CaThumbprint),
            StationToken = string.IsNullOrWhiteSpace(config.StationToken) ? null : config.StationToken!.Trim(),
            GameRoots = roots,
        };
    }

    public static KioskConfig? Parse(string json) => JsonSerializer.Deserialize<KioskConfig>(json);

    private static KioskStartup Locked(string message) => new()
    {
        Run = false,
        LockMessage = message,
    };
}
