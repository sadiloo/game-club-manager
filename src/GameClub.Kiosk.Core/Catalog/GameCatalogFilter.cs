namespace GameClub.Kiosk.Core.Catalog;

public sealed record GameCatalogItem(
    int Id,
    string Name,
    int CategoryId,
    string ExecutableOrUri,
    string? Arguments,
    string? IconUrl,
    int SortOrder,
    bool Enabled,
    IReadOnlyList<string> AllowedProcessNames,
    IReadOnlyList<string> GroupRestrictions,
    int? MinAge);

/// <summary>F5: age restriction field is stored but never evaluated in this slice.</summary>
public static class GameCatalogFilter
{
    public static IEnumerable<GameCatalogItem> ForStation(IEnumerable<GameCatalogItem> items, string stationGroupId)
        => items
            .Where(i => i.Enabled)
            .Where(i => GroupApplies(i.GroupRestrictions, stationGroupId))
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Name);

    public static bool GroupApplies(IReadOnlyList<string> restrictions, string stationGroupId)
    {
        if (restrictions.Count == 0)
        {
            return true;
        }

        return restrictions.Any(r => string.Equals(r, stationGroupId, StringComparison.Ordinal));
    }
}
