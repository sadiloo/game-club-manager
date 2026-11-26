using System.Text.Json.Serialization;

namespace GameClub.Kiosk.Core.Setup;

public sealed class KioskSetupDocument
{
    public string ServerBase { get; set; } = "";

    public string KioskManagerHash { get; set; } = "";

    public string KioskOperatorHash { get; set; } = "";

    public string? ServerId { get; set; }

    public DateTimeOffset CompletedAtUtc { get; set; }

    [JsonIgnore]
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(ServerBase)
        && !string.IsNullOrWhiteSpace(KioskManagerHash)
        && !string.IsNullOrWhiteSpace(KioskOperatorHash)
        && CompletedAtUtc != default;
}