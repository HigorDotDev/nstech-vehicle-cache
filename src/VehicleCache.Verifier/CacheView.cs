using System.Text;
using System.Text.Json;
using StackExchange.Redis;
using VehicleCache.Projection;

namespace VehicleCache.Verifier;

/// <summary>
/// A canonical view of the read model: keys and fields sorted ordinally. Two caches are identical iff their
/// <see cref="Format"/> strings are byte-equal (TB-05, TB-13, TB-14). Valkey's own field order is irrelevant.
/// </summary>
public sealed class CacheView
{
    private readonly SortedDictionary<string, SortedDictionary<string, string>> _entries = new(StringComparer.Ordinal);

    public int Count => _entries.Count;

    public SortedDictionary<string, string> this[string key]
    {
        get
        {
            if (!_entries.TryGetValue(key, out var hash))
            {
                _entries[key] = hash = new SortedDictionary<string, string>(StringComparer.Ordinal);
            }

            return hash;
        }
    }

    public static async Task<CacheView> ReadAsync(IConnectionMultiplexer redis)
    {
        var view = new CacheView();
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints()[0]);
        await foreach (var key in server.KeysAsync(pattern: CacheKeys.VehiclePattern, pageSize: 1000))
        {
            var hash = view[key.ToString()];
            foreach (var entry in await db.HashGetAllAsync(key))
            {
                hash[entry.Name.ToString()] = entry.Value.ToString();
            }
        }

        return view;
    }

    /// <summary>One line per vehicle, <c>key {json}</c>, sorted — diff-friendly.</summary>
    public string Format()
    {
        var text = new StringBuilder();
        foreach (var (key, hash) in _entries)
        {
            text.Append(key).Append(' ').Append(JsonSerializer.Serialize(hash)).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Human-readable differences, expected vs actual (first <paramref name="limit"/>).</summary>
    public static IReadOnlyList<string> Diff(CacheView expected, CacheView actual, int limit = 20)
    {
        var differences = new List<string>();
        foreach (var key in expected._entries.Keys.Union(actual._entries.Keys).Order(StringComparer.Ordinal))
        {
            var want = expected._entries.GetValueOrDefault(key);
            var got = actual._entries.GetValueOrDefault(key);
            var wantJson = want is null ? "(missing)" : JsonSerializer.Serialize(want);
            var gotJson = got is null ? "(missing)" : JsonSerializer.Serialize(got);
            if (wantJson != gotJson)
            {
                differences.Add($"{key}\n  expected {wantJson}\n  actual   {gotJson}");
                if (differences.Count == limit)
                {
                    break;
                }
            }
        }

        return differences;
    }
}
