using System.Text.Json;
using System.Text.Json.Serialization;

namespace VehicleCache.MockWriters.Scenarios;

/// <summary>A deterministic list of source-DB writes (spec §8). <c>kind</c> must be the first property of each step.</summary>
public sealed record Scenario(string Name, string? Description, IReadOnlyList<ScenarioStep> Steps)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        AllowOutOfOrderMetadataProperties = true,
    };

    public static Scenario Load(string path) =>
        JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Empty scenario file '{path}'.");
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(InsertVehicleStep), "insertVehicle")]
[JsonDerivedType(typeof(UpdateVehicleStep), "updateVehicle")]
[JsonDerivedType(typeof(DeleteVehicleStep), "deleteVehicle")]
[JsonDerivedType(typeof(InsertPositionStep), "insertPosition")]
[JsonDerivedType(typeof(BurstStep), "burst")]
public abstract record ScenarioStep
{
    /// <summary>Why the step exists — which TB it exercises. Ignored at runtime.</summary>
    public string? Note { get; init; }
}

public sealed record InsertVehicleStep(long Id, string Plate, string State, DateTimeOffset At) : ScenarioStep;

/// <summary><see cref="At"/> may be older than the row's current business time: an out-of-order (late) report.</summary>
public sealed record UpdateVehicleStep(long Id, string State, DateTimeOffset At) : ScenarioStep;

public sealed record DeleteVehicleStep(long Id) : ScenarioStep;

/// <summary><see cref="Times"/> &gt; 1 inserts identical rows (duplicate GPS fix → equal recorded_at, different ids).</summary>
public sealed record InsertPositionStep(long VehicleId, double Lat, double Lng, DateTimeOffset RecordedAt, int Times = 1)
    : ScenarioStep;

/// <summary>Seeded, deterministic bulk of hard cases for many vehicles; expanded by <see cref="BurstGenerator"/>.</summary>
public sealed record BurstStep(
    int Seed,
    long FirstVehicleId,
    int Vehicles,
    int UpdatesPerVehicle,
    int PositionsPerVehicle,
    int LatePct,
    int DupPct,
    int OrphanPct,
    int DeletePct,
    DateTimeOffset BaseTime) : ScenarioStep;
