namespace VehicleCache.MockWriters.Scenarios;

/// <summary>
/// Expands a <see cref="BurstStep"/> into primitive steps. Same seed ⇒ same steps, byte for byte (no clock, no I/O).
/// Per vehicle: optional orphan position before registration, insert, a random mix of updates (some with an older
/// business time) and positions (some late, some duplicated), optional delete followed by a late position.
/// Vehicles are interleaved randomly so both topics see concurrent, cross-vehicle traffic.
/// </summary>
public static class BurstGenerator
{
    private static readonly string[] States = ["ACTIVE", "IDLE", "MAINTENANCE", "ACTIVE", "IDLE", "DECOMMISSIONED"];

    public static IReadOnlyList<ScenarioStep> Expand(BurstStep burst)
    {
        var rng = new Random(burst.Seed);
        var perVehicle = new List<Queue<ScenarioStep>>(burst.Vehicles);

        for (var i = 0; i < burst.Vehicles; i++)
        {
            perVehicle.Add(new Queue<ScenarioStep>(VehicleHistory(burst, burst.FirstVehicleId + i, rng)));
        }

        var steps = new List<ScenarioStep>();
        while (perVehicle.Count > 0)
        {
            var index = rng.Next(perVehicle.Count);
            steps.Add(perVehicle[index].Dequeue());
            if (perVehicle[index].Count == 0)
            {
                perVehicle.RemoveAt(index);
            }
        }

        return steps;
    }

    private static List<ScenarioStep> VehicleHistory(BurstStep burst, long id, Random rng)
    {
        var steps = new List<ScenarioStep>();
        var stateTime = burst.BaseTime.AddSeconds(rng.Next(0, 600));
        var latestFix = stateTime;
        var (lat, lng) = (-18.9 - rng.NextDouble() * 0.2, -48.2 - rng.NextDouble() * 0.2);

        ScenarioStep Position(DateTimeOffset at, int times = 1)
        {
            lat += (rng.NextDouble() - 0.5) * 0.01;
            lng += (rng.NextDouble() - 0.5) * 0.01;
            return new InsertPositionStep(id, Math.Round(lat, 6), Math.Round(lng, 6), at, times);
        }

        if (Chance(rng, burst.OrphanPct))
        {
            steps.Add(Position(latestFix) with { Note = "orphan: before registration (TB-07)" });
        }

        steps.Add(new InsertVehicleStep(id, $"BRS{id:D5}", "REGISTERED", stateTime));

        var updates = burst.UpdatesPerVehicle;
        var positions = burst.PositionsPerVehicle;
        while (updates + positions > 0)
        {
            if (rng.Next(updates + positions) < updates)
            {
                updates--;
                var state = States[rng.Next(States.Length)];
                if (Chance(rng, burst.LatePct))
                {
                    // Late business report: older than the current state_changed_at; the DB accepts it (ADR-0002).
                    steps.Add(new UpdateVehicleStep(id, state, stateTime.AddMinutes(-rng.Next(5, 120))) { Note = "out-of-order update" });
                }
                else
                {
                    stateTime = stateTime.AddSeconds(rng.Next(30, 600));
                    steps.Add(new UpdateVehicleStep(id, state, stateTime));
                }
            }
            else
            {
                positions--;
                if (Chance(rng, burst.LatePct))
                {
                    steps.Add(Position(latestFix.AddSeconds(-rng.Next(1, 1800))) with { Note = "late fix (TB-03)" });
                }
                else
                {
                    latestFix = latestFix.AddSeconds(rng.Next(5, 120));
                    var duplicate = Chance(rng, burst.DupPct);
                    steps.Add(Position(latestFix, duplicate ? 2 : 1) with { Note = duplicate ? "duplicate fix (TB-06)" : null });
                }
            }
        }

        if (Chance(rng, burst.DeletePct))
        {
            steps.Add(new DeleteVehicleStep(id));
            steps.Add(Position(latestFix.AddSeconds(-rng.Next(1, 600))) with { Note = "late fix after delete (TB-09)" });
        }

        return steps;
    }

    private static bool Chance(Random rng, int percent) => rng.Next(100) < percent;
}
