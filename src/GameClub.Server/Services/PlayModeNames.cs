using GameClub.Billing;

namespace GameClub.Server.Services;

public static class PlayModeNames
{
    public static bool TryParse(string? raw, out PlayMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        foreach (var name in Enum.GetNames<PlayMode>())
        {
            if (string.Equals(name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                mode = Enum.Parse<PlayMode>(name);
                return true;
            }
        }

        return false;
    }
}