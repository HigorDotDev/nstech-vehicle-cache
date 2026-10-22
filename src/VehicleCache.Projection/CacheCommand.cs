using System.Globalization;

namespace VehicleCache.Projection;

/// <summary>What a single CDC record asks the cache to do. Produced by the change mappers (spec §7).</summary>
public abstract record CacheCommand;

/// <summary>Nothing to write (e.g. Kafka tombstone). The offset is still stored.</summary>
public sealed record Skip(string Reason) : CacheCommand;

/// <summary>A conditional write executed atomically by one Lua script on <see cref="Key"/>.</summary>
public abstract record CacheWrite(long VehicleId) : CacheCommand
{
    public string Key => CacheKeys.Vehicle(VehicleId);

    public abstract string Script { get; }

    /// <summary>ARGV for <see cref="Script"/>, already formatted exactly as stored in Valkey.</summary>
    public abstract string[] Arguments();

    /// <summary>Short, log-friendly form, e.g. <c>UpsertState vehicle:7 [2, IDLE, ABC1D23, 2026-…Z]</c> (no script body).</summary>
    public sealed override string ToString() => $"{GetType().Name} {Key} [{string.Join(", ", Arguments())}]";

    protected static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    // Shortest round-trip representation: identical across runs, so replay dumps are byte-identical.
    protected static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}

public sealed record UpsertState(long VehicleId, long Version, string State, string Plate, string StateChangedAt)
    : CacheWrite(VehicleId)
{
    public override string Script => LuaScripts.ApplyState;

    public override string[] Arguments() => [Format(Version), State, Plate, StateChangedAt];
}

public sealed record ApplyPosition(long VehicleId, long PositionId, long RecordedAtUs, string RecordedAt, double Lat, double Lng)
    : CacheWrite(VehicleId)
{
    public override string Script => LuaScripts.ApplyPosition;

    public override string[] Arguments() => [Format(RecordedAtUs), Format(PositionId), Format(Lat), Format(Lng), RecordedAt];
}

public sealed record DeleteVehicle(long VehicleId) : CacheWrite(VehicleId)
{
    public override string Script => LuaScripts.DeleteVehicle;

    public override string[] Arguments() => [];
}

public static class CacheKeys
{
    public const string VehiclePattern = "vehicle:*";

    public static string Vehicle(long id) => string.Create(CultureInfo.InvariantCulture, $"vehicle:{id}");
}
