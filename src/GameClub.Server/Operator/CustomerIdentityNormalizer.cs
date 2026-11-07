namespace GameClub.Server.Operator;

internal static class CustomerIdentityNormalizer
{
    public static string NormalizeMobile(string? mobile)
    {
        if (mobile is null)
        {
            return string.Empty;
        }

        var trimmed = mobile.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[trimmed.Length];
        for (var i = 0; i < trimmed.Length; i++)
        {
            buffer[i] = FoldDigit(trimmed[i]);
        }

        return new string(buffer);
    }

    public static string NormalizeName(string? name)
        => (name ?? string.Empty).Trim();

    public static string? NormalizeMembershipCode(string? code)
    {
        if (code is null)
        {
            return null;
        }

        var trimmed = code.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static string? MembershipCodeKey(string? code)
    {
        var normalized = NormalizeMembershipCode(code);
        return normalized is null ? null : normalized.ToUpperInvariant();
    }

    private static char FoldDigit(char c) => c switch
    {
        >= '\uFF10' and <= '\uFF19' => (char)('0' + (c - '\uFF10')),
        >= '\u06F0' and <= '\u06F9' => (char)('0' + (c - '\u06F0')),
        >= '\u0660' and <= '\u0669' => (char)('0' + (c - '\u0660')),
        _ => c,
    };
}
