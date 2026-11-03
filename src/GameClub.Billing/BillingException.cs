namespace GameClub.Billing;

/// <summary>
/// A domain rule was violated. Programmer errors (bad tariffs, overlapping
/// sessions, negative money) throw. Operator outcomes such as an unbalanced
/// split payment are returned as results instead.
/// </summary>
public sealed class BillingException : Exception
{
    public BillingException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
