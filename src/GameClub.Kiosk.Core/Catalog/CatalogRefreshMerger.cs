using System.Text.Json;

namespace GameClub.Kiosk.Core.Catalog;

/// <summary>Merge server catalog deltas with durable SQLite cache (offline / partial responses).</summary>
public static class CatalogRefreshMerger
{
    public static async Task<CatalogRefreshResult> RefreshAsync(
        Func<long, Task<CatalogHttpOutcome>> fetchAsync,
        SqliteCatalogCache store,
        IReadOnlyList<GameCatalogItem> memoryCache,
        long memoryVersion,
        CancellationToken ct = default)
    {
        var since = memoryVersion > 0 ? memoryVersion : await store.LastVersionAsync(ct);
        try
        {
            var outcome = await fetchAsync(since);
            if (!outcome.Success)
            {
                return await LoadDiskIntoMemoryAsync(store, memoryCache, ct);
            }

            if (outcome.Entries.Count == 0)
            {
                var persistedVersion = await store.LastVersionAsync(ct);
                if (since > 0 && persistedVersion > 0)
                {
                    return await LoadDiskIntoMemoryAsync(store, memoryCache, ct);
                }

                if (since == 0)
                {
                    return await LoadDiskIntoMemoryAsync(store, memoryCache, ct);
                }

                return new CatalogRefreshResult(memoryCache.ToList(), since);
            }

            var baseMemory = memoryCache.Count > 0
                ? memoryCache
                : await store.LoadAsync(ct);
            var merged = MergeEntries(baseMemory, outcome.Entries, since, outcome.CatalogVersion);
            await store.SaveAsync(merged.Items, merged.Version, ct);
            return merged;
        }
        catch
        {
            return await LoadDiskIntoMemoryAsync(store, memoryCache, ct);
        }
    }

    public static CatalogRefreshResult MergeEntries(
        IReadOnlyList<GameCatalogItem> memoryCache,
        IReadOnlyList<GameCatalogItem> deltaEntries,
        long since,
        long catalogVersion)
    {
        if (since > 0 && memoryCache.Count > 0)
        {
            var list = memoryCache.ToList();
            foreach (var item in deltaEntries)
            {
                var idx = list.FindIndex(g => g.Id == item.Id);
                if (idx >= 0)
                {
                    list[idx] = item;
                }
                else
                {
                    list.Add(item);
                }
            }

            return new CatalogRefreshResult(list, catalogVersion);
        }

        if (since > 0 && memoryCache.Count == 0)
        {
            var diskLoaded = deltaEntries.ToList();
            return new CatalogRefreshResult(diskLoaded, catalogVersion);
        }

        return new CatalogRefreshResult(deltaEntries.ToList(), catalogVersion);
    }

    public static GameCatalogItem ParseCatalogItem(JsonElement entry)
        => new(
            entry.GetProperty("id").GetInt32(),
            entry.GetProperty("name").GetString() ?? "",
            entry.TryGetProperty("categoryId", out var c) ? c.GetInt32() : 1,
            entry.GetProperty("executableOrUri").GetString() ?? "",
            entry.TryGetProperty("arguments", out var a) ? a.GetString() : null,
            entry.TryGetProperty("iconUrl", out var i) ? i.GetString() : null,
            entry.TryGetProperty("sortOrder", out var o) ? o.GetInt32() : 0,
            entry.GetProperty("enabled").GetBoolean(),
            entry.TryGetProperty("allowedProcessNames", out var al)
                ? al.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [],
            entry.TryGetProperty("groupRestrictions", out var gr)
                ? gr.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [],
            null);

    private static async Task<CatalogRefreshResult> LoadDiskIntoMemoryAsync(
        SqliteCatalogCache store,
        IReadOnlyList<GameCatalogItem> memoryCache,
        CancellationToken ct)
    {
        var cached = await store.LoadAsync(ct);
        if (cached.Count > 0)
        {
            var version = await store.LastVersionAsync(ct);
            return new CatalogRefreshResult(cached.ToList(), version);
        }

        return new CatalogRefreshResult(memoryCache.ToList(), await store.LastVersionAsync(ct));
    }
}

public sealed record CatalogHttpOutcome(bool Success, long CatalogVersion, IReadOnlyList<GameCatalogItem> Entries);

public sealed record CatalogRefreshResult(IReadOnlyList<GameCatalogItem> Items, long Version);
