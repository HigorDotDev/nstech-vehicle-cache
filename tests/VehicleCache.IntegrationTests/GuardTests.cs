using VehicleCache.Projection;
using VehicleCache.Projection.Debezium;
using VehicleCache.Worker.Cache;

namespace VehicleCache.IntegrationTests;

/// <summary>TB-01…TB-09, TB-11 against the real Lua scripts. Values mirror docs/03-test-behaviors.md.</summary>
[Collection("valkey")]
public sealed class GuardTests(ValkeyFixture valkey) : IAsyncLifetime
{
    private static readonly string FixturesDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public Task InitializeAsync() => valkey.FlushAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static UpsertState State(long id, long version, string state = "ACTIVE") =>
        new(id, version, state, $"PLATE{id}", "2026-10-21T10:00:00.000000Z");

    private static ApplyPosition Position(long vehicleId, long positionId, string recordedAt, double lat = -18.91, double lng = -48.27) =>
        new(vehicleId, positionId, CdcTimestamps.ToEpochMicros(recordedAt), recordedAt, lat, lng);

    private static CacheCommand FromFixture(string name, Func<byte[]?, CacheCommand> mapper)
    {
        var bytes = File.ReadAllBytes(Path.Combine(FixturesDir, $"{name}.json"));
        return mapper(bytes.Length == 0 ? null : bytes);
    }

    private Task<WriteOutcome> Apply(CacheCommand command) => valkey.Writer.ApplyAsync(command);

    [Fact]
    [Trait("TB", "01")]
    public async Task Newer_version_is_applied()
    {
        await Apply(State(7, 1, "ACTIVE"));

        (await Apply(State(7, 2, "MAINTENANCE"))).ShouldBe(WriteOutcome.Applied);

        var hash = await valkey.HashAsync(7);
        hash["state"].ShouldBe("MAINTENANCE");
        hash["stateVersion"].ShouldBe("2");
    }

    [Fact]
    [Trait("TB", "04")]
    public async Task Older_version_is_rejected()
    {
        await Apply(State(7, 3, "IDLE"));

        (await Apply(State(7, 2, "ACTIVE"))).ShouldBe(WriteOutcome.Rejected);

        (await valkey.HashAsync(7))["state"].ShouldBe("IDLE");
    }

    [Fact]
    [Trait("TB", "02")]
    public async Task Newer_position_is_applied()
    {
        await Apply(Position(7, 5, "2026-10-21T10:00:00.000000Z"));

        (await Apply(Position(7, 6, "2026-10-21T10:05:00.000000Z", -18.91, -48.27))).ShouldBe(WriteOutcome.Applied);

        var hash = await valkey.HashAsync(7);
        hash["lat"].ShouldBe("-18.91");
        hash["lng"].ShouldBe("-48.27");
        hash["recordedAt"].ShouldBe("2026-10-21T10:05:00.000000Z");
        hash["positionId"].ShouldBe("6");
    }

    [Fact]
    [Trait("TB", "03")]
    public async Task Older_recorded_at_is_rejected_even_with_higher_id()
    {
        // Real captured events: id 1 recorded 10:06:00.123456, then id 2 recorded 10:01 (late GPS).
        await Apply(FromFixture("positions.insert", PositionChangeMapper.Map));

        (await Apply(FromFixture("positions.insert-late", PositionChangeMapper.Map))).ShouldBe(WriteOutcome.Rejected);

        var hash = await valkey.HashAsync(1);
        hash["positionId"].ShouldBe("1");
        hash["recordedAt"].ShouldBe("2026-10-21T10:06:00.123456Z");
    }

    [Theory]
    [Trait("TB", "06")]
    [InlineData(20, 21)]
    [InlineData(21, 20)]
    public async Task Tie_breaks_on_position_id(long first, long second)
    {
        await Apply(Position(7, first, "2026-10-21T10:00:00.000000Z"));
        await Apply(Position(7, second, "2026-10-21T10:00:00.000000Z"));

        (await valkey.HashAsync(7))["positionId"].ShouldBe("21");
    }

    [Fact]
    [Trait("TB", "05")]
    public async Task Reapplying_any_event_does_not_change_dump()
    {
        CacheCommand[] events =
        [
            State(7, 1), State(7, 2, "IDLE"), Position(7, 1, "2026-10-21T10:00:00.000000Z"),
            Position(7, 2, "2026-10-21T10:01:00.000000Z"), Position(9, 3, "2026-10-21T10:02:00.000000Z"),
            State(8, 1), new DeleteVehicle(8),
        ];
        foreach (var e in events)
        {
            await Apply(e);
        }

        var before = await valkey.DumpAsync();

        foreach (var e in events)
        {
            (await Apply(e)).ShouldBe(WriteOutcome.Rejected);
        }

        (await valkey.DumpAsync()).ShouldBe(before);
    }

    [Fact]
    [Trait("TB", "07")]
    public async Task Position_before_vehicle_is_joined_and_order_independent()
    {
        var position = FromFixture("positions.insert-orphan", PositionChangeMapper.Map); // vehicle 2, real event
        var vehicle = State(2, 1, "REGISTERED");

        await Apply(position);
        var orphan = await valkey.HashAsync(2);
        orphan.ShouldNotContainKey("state");
        orphan["positionId"].ShouldBe("3");

        await Apply(vehicle);
        var joined = await valkey.DumpAsync();
        (await valkey.HashAsync(2))["state"].ShouldBe("REGISTERED");

        await valkey.FlushAsync();
        await Apply(vehicle);
        await Apply(position);

        (await valkey.DumpAsync()).ShouldBe(joined);
    }

    [Fact]
    [Trait("TB", "08")]
    public async Task Delete_leaves_only_tombstone()
    {
        await Apply(State(7, 1));
        await Apply(Position(7, 1, "2026-10-21T10:00:00.000000Z"));

        (await Apply(new DeleteVehicle(7))).ShouldBe(WriteOutcome.Applied);
        (await Apply(VehicleChangeMapper.Map(null))).ShouldBe(WriteOutcome.Skipped); // the Kafka tombstone that follows

        (await valkey.HashAsync(7)).ShouldBe(new Dictionary<string, string> { ["deleted"] = "1" });
    }

    [Fact]
    [Trait("TB", "09")]
    public async Task Deleted_is_absorbing()
    {
        await Apply(State(7, 1));
        await Apply(new DeleteVehicle(7));

        (await Apply(State(7, 99))).ShouldBe(WriteOutcome.Rejected);
        (await Apply(Position(7, 50, "2030-01-01T00:00:00.000000Z"))).ShouldBe(WriteOutcome.Rejected);
        (await Apply(new DeleteVehicle(7))).ShouldBe(WriteOutcome.Rejected);

        (await valkey.HashAsync(7)).ShouldBe(new Dictionary<string, string> { ["deleted"] = "1" });
    }

    [Fact]
    [Trait("TB", "09")]
    public async Task Position_and_delete_converge_in_either_order()
    {
        var position = Position(7, 1, "2026-10-21T10:00:00.000000Z");
        var delete = new DeleteVehicle(7);

        await Apply(position);
        await Apply(delete);
        var positionThenDelete = await valkey.DumpAsync();

        await valkey.FlushAsync();
        await Apply(delete);
        await Apply(position);

        (await valkey.DumpAsync()).ShouldBe(positionThenDelete);
    }

    [Fact]
    [Trait("TB", "11")]
    public async Task Snapshot_event_older_than_stream_is_rejected()
    {
        // Stream update v2 (real fixture), then a re-snapshot of the same row (v2, real fixture) and an older read.
        await Apply(FromFixture("vehicles.update", VehicleChangeMapper.Map));

        (await Apply(FromFixture("vehicles.snapshot-first", VehicleChangeMapper.Map))).ShouldBe(WriteOutcome.Rejected);
        (await Apply(FromFixture("vehicles.insert", VehicleChangeMapper.Map))).ShouldBe(WriteOutcome.Rejected);

        var hash = await valkey.HashAsync(1);
        hash["state"].ShouldBe("ACTIVE");
        hash["stateVersion"].ShouldBe("2");
    }
}
