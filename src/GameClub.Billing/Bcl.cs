namespace GameClub.Billing;

/// <summary>Small stand-ins for APIs that netstandard2.0 does not have.</summary>
internal static class Bcl
{
    public static bool TryAdd<TKey, TValue>(IDictionary<TKey, TValue> map, TKey key, TValue value)
    {
        if (map.ContainsKey(key))
        {
            return false;
        }

        map.Add(key, value);
        return true;
    }
}
