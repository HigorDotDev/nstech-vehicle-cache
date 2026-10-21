using System.Globalization;
using VehicleCache.Projection;
using VehicleCache.Verifier;
using Xunit.Abstractions;

namespace VehicleCache.IntegrationTests;

/// <summary>
/// TB-10 — the property behind TB-03…TB-09: any permutation of the event set, with random duplicates,
/// converges to the same cache, equal to a reference projection computed by sorting on the ordering keys.
/// A naive "consume → HSET" worker fails this on the first permutation.
/// </summary>
[Collection("valkey")]
public sealed class ConvergenceTests(ValkeyFixture valkey, ITestOutputHelper output)
{
    private const int Seed = 20261021;
    private const int Vehicles = 50;
    private const int Permutations = 200;
    private const double DuplicateRate = 0.2;

    [Fact]
    [Trait("TB", "10")]
    public async Task Any_permutation_with_duplicates_converges()
    {
        var rng = new Random(Seed);
        var history = GenerateHistory(rng);
        var expected = Reference(history).Format();
        output.WriteLine($"seed={Seed} events={history.Count} vehicles={Vehicles} permutations={Permutations}");

        for (var run = 0; run < Permutations; run++)
        {
            await valkey.FlushAsync();

            var events = WithDuplicates(history, rng);
            rng.Shuffle(events);

            // Issued in order without awaiting each one: StackExchange.Redis pipelines them in that order.
            await Task.WhenAll(events.Select(e => valkey.Writer.ApplyAsync(e)));

            var actual = await valkey.DumpAsync();
            actual.ShouldBe(expected, $"seed={Seed} run={run}");
        }
    }

    /// <summary>A plausible DB history: versions 1..n per vehicle, late/duplicate/orphan positions, some deletes.</summary>
    private static List<CacheWrite> GenerateHistory(Random rng)
    {
        string[] states = ["REGISTERED", "ACTIVE", "IDLE", "MAINTENANCE", "DECOMMISSIONED"];
        var baseUs = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds() * 1000;
        var events = new List<CacheWrite>();
        long positionId = 0;

        for (long vehicle = 1; vehicle <= Vehicles; vehicle++)
        {
            var registered = rng.NextDouble() < 0.85; // the rest only ever have orphan positions
            var versions = registered ? rng.Next(1, 6) : 0;
            for (var version = 1; version <= versions; version++)
            {
                events.Add(new UpsertState(vehicle, version, states[rng.Next(states.Length)], $"PLT{vehicle:D4}",
                    Iso(baseUs + version * 60_000_000L)));
            }

            var positions = rng.Next(0, 8);
            ApplyPosition? previous = null;
            for (var i = 0; i < positions; i++)
            {
                // 15%: duplicate insert of the previous fix (same recorded_at, new id) → tie-break on id.
                var recordedAtUs = previous is not null && rng.NextDouble() < 0.15
                    ? previous.RecordedAtUs
                    : baseUs + rng.Next(0, 600) * 1_000_000L + rng.Next(0, 1_000_000);
                var position = new ApplyPosition(vehicle, ++positionId, recordedAtUs, Iso(recordedAtUs),
                    Math.Round(-18.9 - rng.NextDouble(), 6), Math.Round(-48.2 - rng.NextDouble(), 6));
                events.Add(position);
                previous = position;
            }

            if (registered && rng.NextDouble() < 0.2)
            {
                events.Add(new DeleteVehicle(vehicle));
            }
        }

        return events;
    }

    private static CacheWrite[] WithDuplicates(List<CacheWrite> history, Random rng) =>
        history.Concat(history.Where(_ => rng.NextDouble() < DuplicateRate)).ToArray();

    /// <summary>Independent oracle: what the cache must hold, by spec §2/§5 — not by running the scripts.</summary>
    private static CacheView Reference(List<CacheWrite> history)
    {
        var expected = new CacheView();
        foreach (var vehicle in history.GroupBy(e => e.VehicleId))
        {
            var hash = expected[CacheKeys.Vehicle(vehicle.Key)];
            if (vehicle.OfType<DeleteVehicle>().Any())
            {
                hash["deleted"] = "1";
            }
            else
            {
                if (vehicle.OfType<UpsertState>().MaxBy(s => s.Version) is { } state)
                {
                    hash["state"] = state.State;
                    hash["plate"] = state.Plate;
                    hash["stateChangedAt"] = state.StateChangedAt;
                    hash["stateVersion"] = Invariant(state.Version);
                }

                if (vehicle.OfType<ApplyPosition>().MaxBy(p => (p.RecordedAtUs, p.PositionId)) is { } position)
                {
                    hash["lat"] = position.Lat.ToString(CultureInfo.InvariantCulture);
                    hash["lng"] = position.Lng.ToString(CultureInfo.InvariantCulture);
                    hash["recordedAt"] = position.RecordedAt;
                    hash["recordedAtUs"] = Invariant(position.RecordedAtUs);
                    hash["positionId"] = Invariant(position.PositionId);
                }
            }
        }

        return expected;
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Iso(long epochMicros) =>
        DateTimeOffset.UnixEpoch.AddTicks(epochMicros * TimeSpan.TicksPerMicrosecond)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
}
