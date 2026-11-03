namespace GameClub.Kiosk.Core;

/// <summary>
/// A logout the server has not acknowledged. It survives a shell restart.
/// The server prices from its own tariff and its own clock; the fields here are
/// the station's checkpoint, sent as a hint.
/// </summary>
public sealed class PendingReport
{
    public string SessionId { get; set; } = "";

    public string AccountId { get; set; } = "";

    public long LoginRevision { get; set; }

    public string StationId { get; set; } = "";

    public string GroupId { get; set; } = "";

    public int Mode { get; set; }

    public int PlayerCount { get; set; } = 1;

    public long HourlyToman { get; set; }

    public string StartedAt { get; set; } = "";

    public string EndedAt { get; set; } = "";

    public int Reason { get; set; }

    public int Attempts { get; set; }

    public string? NextAttemptAt { get; set; }

    public string MemberName { get; set; } = "";
}
