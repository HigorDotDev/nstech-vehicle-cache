using Npgsql;
using Testcontainers.PostgreSql;
using VehicleCache.MockWriters;
using VehicleCache.MockWriters.Scenarios;
using VehicleCache.Verifier;

namespace VehicleCache.IntegrationTests;

/// <summary>The real scenarios against the real schema (triggers included): the writers produce what the e2e expects.</summary>
public sealed class ScenarioRunnerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17.6").WithDatabase("fleet").Build();
    private NpgsqlDataSource _db = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var result = await _postgres.ExecScriptAsync(
            await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "infra", "001_schema.sql")));
        result.ExitCode.ShouldBe(0, result.Stderr);
        _db = NpgsqlDataSource.Create(_postgres.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var cmd = _db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private Task<ScenarioStats> RunAsync(string file) =>
        new ScenarioRunner(new FleetDb(_db), new Throttle(0, TimeProvider.System))
            .RunAsync(Scenario.Load(Path.Combine(AppContext.BaseDirectory, "scenarios", file)), CancellationToken.None);

    [Fact]
    public async Task Seed_then_e2e_produce_the_documented_source_state()
    {
        await RunAsync("seed.json");
        var stats = await RunAsync("e2e.json");

        // Vehicle 1: seed v1→v2, stream v3 (MAINTENANCE), last step v4 (ACTIVE).
        (await ScalarAsync<long>("SELECT version FROM vehicles WHERE id = 1")).ShouldBe(4);
        (await ScalarAsync<string>("SELECT state FROM vehicles WHERE id = 1")).ShouldBe("ACTIVE");

        // Vehicle 2: out-of-order business update is accepted by the DB and still bumps the version.
        (await ScalarAsync<long>("SELECT version FROM vehicles WHERE id = 2")).ShouldBe(2);
        (await ScalarAsync<DateTime>("SELECT state_changed_at FROM vehicles WHERE id = 2"))
            .ShouldBe(new DateTime(2026, 10, 21, 7, 30, 0, DateTimeKind.Utc));

        // Deletes are retired by trigger; 21's late position was still inserted (append-only facts, no FK).
        (await ScalarAsync<long>("SELECT count(*) FROM retired_vehicle_ids WHERE vehicle_id IN (21, 22)")).ShouldBe(2);
        (await ScalarAsync<long>("SELECT count(*) FROM positions WHERE vehicle_id = 21")).ShouldBe(2);
        (await ScalarAsync<long>("SELECT count(*) FROM retired_vehicle_ids")).ShouldBe(stats.VehicleDeletes); // seed has none

        // Duplicate fix: two rows with the same recorded_at for vehicle 3.
        (await ScalarAsync<long>("SELECT count(*) FROM positions WHERE vehicle_id = 3 AND recorded_at = '2026-10-21T13:15:00Z'"))
            .ShouldBe(2);

        // Enough volume for the e2e chaos steps to land mid-stream.
        (stats.VehicleUpdates + stats.PositionInserts).ShouldBeGreaterThan(1000);
    }

    [Fact]
    [Trait("TB", "15")]
    public async Task Oracle_follows_spec_section_2_on_the_hard_cases()
    {
        await RunAsync("seed.json");
        await RunAsync("e2e.json");

        var oracle = await Oracle.BuildAsync(_db, CancellationToken.None);

        // One key per id that ever appeared: live vehicles, retired ids, position-only ids.
        oracle.Count.ShouldBe((int)await ScalarAsync<long>(
            "SELECT count(*) FROM (SELECT id FROM vehicles UNION SELECT vehicle_id FROM retired_vehicle_ids UNION SELECT vehicle_id FROM positions) ids"));

        var duplicateWinner = await ScalarAsync<long>(
            "SELECT max(id) FROM positions WHERE vehicle_id = 3 AND recorded_at = '2026-10-21T13:15:00Z'");
        oracle["vehicle:3"]["positionId"].ShouldBe(duplicateWinner.ToString(System.Globalization.CultureInfo.InvariantCulture)); // TB-03 + TB-06
        oracle["vehicle:3"]["recordedAt"].ShouldBe("2026-10-21T13:15:00.000000Z");

        oracle["vehicle:2"]["stateChangedAt"].ShouldBe("2026-10-21T07:30:00.000000Z"); // mirrors the out-of-order update
        oracle["vehicle:6"].Keys.ShouldContain("state");                                // orphan joined (TB-07)
        oracle["vehicle:6"].Keys.ShouldContain("positionId");
        oracle["vehicle:21"].ShouldBe(new SortedDictionary<string, string> { ["deleted"] = "1" }); // TB-08/09
        oracle["vehicle:22"].ShouldBe(new SortedDictionary<string, string> { ["deleted"] = "1" });
        oracle["vehicle:23"].Keys.ShouldNotContain("state");                            // never registered
    }
}
