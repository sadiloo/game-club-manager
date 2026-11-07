using GameClub.Server.Data.Entities;

namespace GameClub.Server.Operator;

/// <summary>PO defaults for open questions; change in one place pending product review.</summary>
public sealed class OperatorPolicies
{
    /// <summary>OQ1: manual session start/end edit — manager only, audited.</summary>
    public bool OnlyManagerMayEditSessionTimes { get; init; } = true;

    /// <summary>OQ2: concurrent bill actions — first wins, second gets 409.</summary>
    public bool ConcurrentBillActionReturns409 { get; init; } = true;

    /// <summary>OQ3: operators may resolve manual-settle queue.</summary>
    public bool OperatorMayResolveManualSettle { get; init; } = true;

    /// <summary>OQ4: operators may confirm/reject pending card-to-card.</summary>
    public bool OperatorMayConfirmCardToCard { get; init; } = true;

    /// <summary>OQ5: rejected card-to-card stays on manager unpaid list; no customer debt.</summary>
    public bool RejectedCardToCardGoesToManagerUnpaidList { get; init; } = true;
}

public sealed class OperatorContext
{
    public Guid? UserId { get; set; }

    public UserRole? Role { get; set; }

    public bool IsAuthenticated => UserId.HasValue;
}
