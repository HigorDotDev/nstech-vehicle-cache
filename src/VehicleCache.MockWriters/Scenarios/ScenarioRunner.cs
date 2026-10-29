namespace VehicleCache.MockWriters.Scenarios;

public sealed record ScenarioStats(int VehicleInserts, int VehicleUpdates, int VehicleDeletes, int PositionInserts)
{
    public override string ToString() =>
        $"vehicleInserts={VehicleInserts} vehicleUpdates={VehicleUpdates} vehicleDeletes={VehicleDeletes} positionInserts={PositionInserts}";
}

/// <summary>Executes a scenario step by step, in order, one statement per CDC event.</summary>
public sealed class ScenarioRunner(FleetDb db, Throttle throttle)
{
    public static IEnumerable<ScenarioStep> Expand(IEnumerable<ScenarioStep> steps) =>
        steps.SelectMany(step => step is BurstStep burst ? BurstGenerator.Expand(burst) : [step]);

    public async Task<ScenarioStats> RunAsync(Scenario scenario, CancellationToken ct)
    {
        int inserts = 0, updates = 0, deletes = 0, positions = 0;

        foreach (var step in Expand(scenario.Steps))
        {
            switch (step)
            {
                case InsertVehicleStep s:
                    await throttle.WaitAsync(ct);
                    await db.InsertVehicleAsync(s.Id, s.Plate, s.State, s.At, ct);
                    inserts++;
                    break;
                case UpdateVehicleStep s:
                    await throttle.WaitAsync(ct);
                    await db.UpdateVehicleAsync(s.Id, s.State, s.At, ct);
                    updates++;
                    break;
                case DeleteVehicleStep s:
                    await throttle.WaitAsync(ct);
                    await db.DeleteVehicleAsync(s.Id, ct);
                    deletes++;
                    break;
                case InsertPositionStep s:
                    for (var i = 0; i < s.Times; i++)
                    {
                        await throttle.WaitAsync(ct);
                        await db.InsertPositionAsync(s.VehicleId, s.Lat, s.Lng, s.RecordedAt, ct);
                        positions++;
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unsupported step {step.GetType().Name}.");
            }
        }

        return new ScenarioStats(inserts, updates, deletes, positions);
    }
}
