using StackExchange.Redis;
using VehicleCache.Projection;

namespace VehicleCache.Worker.Cache;

public enum WriteOutcome
{
    Applied,
    Rejected,
    Skipped,
}

public interface IVehicleCacheWriter
{
    Task<WriteOutcome> ApplyAsync(CacheCommand command);
}

/// <summary>
/// The only component that mutates Valkey: every write is one of the three Lua guards (CLAUDE.md invariant 3).
/// Never fire-and-forget — the outcome gates the Kafka offset store (ADR-0005).
/// </summary>
public sealed class ValkeyVehicleCacheWriter(IConnectionMultiplexer redis) : IVehicleCacheWriter
{
    private readonly IDatabase _db = redis.GetDatabase();

    public async Task<WriteOutcome> ApplyAsync(CacheCommand command)
    {
        if (command is not CacheWrite write)
        {
            return WriteOutcome.Skipped;
        }

        var arguments = Array.ConvertAll(write.Arguments(), a => (RedisValue)a);
        var result = await _db.ScriptEvaluateAsync(write.Script, [write.Key], arguments);

        return (long)result == 1 ? WriteOutcome.Applied : WriteOutcome.Rejected;
    }
}
