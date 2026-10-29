namespace VehicleCache.MockWriters;

/// <summary>
/// Continuous random load (the brief's "configurable rate" writers). Seeded, but timestamps come from the clock,
/// so runs are not byte-reproducible — use <c>scenario</c> for deterministic replay.
/// </summary>
public static class LoadWriters
{
    private static readonly string[] States = ["ACTIVE", "IDLE", "MAINTENANCE", "DECOMMISSIONED"];

    public static async Task VehiclesAsync(
        FleetDb db, Throttle throttle, TimeProvider time, Random rng,
        int count, long firstId, int outOfOrderPct, int deletePct, CancellationToken ct)
    {
        var live = new List<long>();
        var nextId = firstId;

        for (var i = 0; i < count; i++)
        {
            await throttle.WaitAsync(ct);
            if (live.Count < 5 || rng.Next(100) < 30)
            {
                await db.InsertVehicleAsync(nextId, $"LD{nextId:D6}", "REGISTERED", time.GetUtcNow(), ct);
                live.Add(nextId++); // ids are sequential: never reused after a delete
            }
            else if (rng.Next(100) < deletePct)
            {
                var index = rng.Next(live.Count);
                await db.DeleteVehicleAsync(live[index], ct);
                live.RemoveAt(index);
            }
            else
            {
                var id = live[rng.Next(live.Count)];
                var state = States[rng.Next(States.Length)];
                if (rng.Next(100) < outOfOrderPct)
                {
                    await db.UpdateVehicleLateAsync(id, state, TimeSpan.FromMinutes(rng.Next(1, 60)), ct);
                }
                else
                {
                    await db.UpdateVehicleAsync(id, state, time.GetUtcNow(), ct);
                }
            }
        }
    }

    public static async Task PositionsAsync(
        FleetDb db, Throttle throttle, TimeProvider time, Random rng,
        int count, long firstId, int vehicles, int latePct, int dupPct, int orphanPct, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
        {
            // Orphans target ids the vehicles writer has not reached yet (it registers them sequentially later).
            var vehicleId = rng.Next(100) < orphanPct
                ? firstId + vehicles + rng.Next(vehicles)
                : firstId + rng.Next(vehicles);
            var recordedAt = rng.Next(100) < latePct
                ? time.GetUtcNow().AddMinutes(-rng.Next(1, 30))
                : time.GetUtcNow();
            var (lat, lng) = (Math.Round(-18.9 - rng.NextDouble() * 0.2, 6), Math.Round(-48.2 - rng.NextDouble() * 0.2, 6));

            var times = rng.Next(100) < dupPct ? 2 : 1;
            for (var t = 0; t < times; t++)
            {
                await throttle.WaitAsync(ct);
                await db.InsertPositionAsync(vehicleId, lat, lng, recordedAt, ct);
            }
        }
    }
}
