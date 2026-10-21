using VehicleCache.MockWriters.Scenarios;

namespace VehicleCache.UnitTests;

public class BurstGeneratorTests
{
    private static readonly BurstStep Burst = new(
        Seed: 2026, FirstVehicleId: 1000, Vehicles: 60, UpdatesPerVehicle: 5, PositionsPerVehicle: 15,
        LatePct: 25, DupPct: 10, OrphanPct: 15, DeletePct: 10, BaseTime: new DateTimeOffset(2026, 10, 21, 14, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Same_seed_expands_to_identical_steps()
    {
        BurstGenerator.Expand(Burst).ShouldBe(BurstGenerator.Expand(Burst));
    }

    [Fact]
    public void Every_vehicle_history_is_valid_for_the_source_triggers()
    {
        // The DB rejects updates/deletes of unknown ids and inserts of retired ids: the generator must never emit them.
        var registered = new HashSet<long>();
        var retired = new HashSet<long>();
        foreach (var step in BurstGenerator.Expand(Burst))
        {
            switch (step)
            {
                case InsertVehicleStep s:
                    retired.ShouldNotContain(s.Id);
                    registered.Add(s.Id).ShouldBeTrue();
                    break;
                case UpdateVehicleStep s:
                    registered.ShouldContain(s.Id);
                    break;
                case DeleteVehicleStep s:
                    registered.Remove(s.Id).ShouldBeTrue();
                    retired.Add(s.Id);
                    break;
            }
        }

        retired.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void Burst_contains_every_hard_case()
    {
        var steps = BurstGenerator.Expand(Burst);
        var notes = steps.Select(s => s.Note).OfType<string>().ToList();

        notes.ShouldContain(n => n.Contains("TB-03"));
        notes.ShouldContain(n => n.Contains("TB-06"));
        notes.ShouldContain(n => n.Contains("TB-07"));
        notes.ShouldContain(n => n.Contains("TB-09"));
        notes.ShouldContain("out-of-order update");
        steps.OfType<InsertPositionStep>().ShouldContain(p => p.Times == 2);
    }
}
