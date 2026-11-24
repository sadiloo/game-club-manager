namespace GameClub.Server.Services;

public static class SegmentKeySupport
{
    /// <summary>Excel-style base-26 keys: a..z, aa, ab, ...</summary>
    public static string Allocate(int existingSegmentCount)
    {
        var n = existingSegmentCount;
        var chars = new List<char>(4);
        while (n >= 0)
        {
            chars.Insert(0, (char)('a' + n % 26));
            n = n / 26 - 1;
        }

        return new string(chars.ToArray());
    }
}