namespace GameClub.Billing;

internal static class StationId
{
    /// <summary>
    /// Trim, fold letter case, map Arabic ك/ي to Persian ک/ی, and map Persian/Arabic/full-width digits to ASCII.
    /// Internal spaces and punctuation are preserved.
    /// </summary>
    public static string Normalize(string id)
    {
        if (id is null)
        {
            return string.Empty;
        }

        var trimmed = id.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[trimmed.Length];
        for (var i = 0; i < trimmed.Length; i++)
        {
            buffer[i] = NormalizeChar(trimmed[i]);
        }

        return new string(buffer);
    }

    private static char NormalizeChar(char c)
    {
        return c switch
        {
            '\u0643' => '\u06A9',
            '\u064A' => '\u06CC',
            >= '\uFF10' and <= '\uFF19' => (char)('0' + (c - '\uFF10')),
            >= '\u06F0' and <= '\u06F9' => (char)('0' + (c - '\u06F0')),
            >= '\u0660' and <= '\u0669' => (char)('0' + (c - '\u0660')),
            _ => char.IsLetter(c) ? char.ToUpperInvariant(c) : c,
        };
    }
}
