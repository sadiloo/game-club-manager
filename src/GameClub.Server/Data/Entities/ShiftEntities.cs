namespace GameClub.Server.Data.Entities;

public enum ShiftStatus
{
    Open = 0,
    Closed = 1,
}

/// <summary>Branch-wide shift and multi-operator settings (singleton row id = 1).</summary>
public sealed class BranchCafeSettingsEntity
{
    public int Id { get; set; } = 1;

    public bool ShiftsEnabled { get; set; }

    public bool MultiOperatorEnabled { get; set; }
}

public sealed class ShiftEntity
{
    public Guid Id { get; set; }

    public ShiftStatus Status { get; set; } = ShiftStatus.Open;

    public DateTime OpenedAtUtc { get; set; }

    public DateTime? ClosedAtUtc { get; set; }

    public long OpeningFloatToman { get; set; }

    public Guid OpenedByUserId { get; set; }

    public CloseRecordEntity? CloseRecord { get; set; }
}

/// <summary>Shift close or working-day close actuals and selected working day.</summary>
public sealed class CloseRecordEntity
{
    public Guid Id { get; set; }

    public Guid? ShiftId { get; set; }

    public ShiftEntity? Shift { get; set; }

    public DateTime PeriodOpenedAtUtc { get; set; }

    public DateTime ClosedAtUtc { get; set; }

    public int WorkingDayYear { get; set; }

    public int WorkingDayMonth { get; set; }

    public int WorkingDayDay { get; set; }

    public long ActualCashToman { get; set; }

    public long ActualCardReaderToman { get; set; }

    public string? DiscrepancyExplanation { get; set; }

    public Guid? NextRecipientUserId { get; set; }

    public Guid ClosedByUserId { get; set; }
}
