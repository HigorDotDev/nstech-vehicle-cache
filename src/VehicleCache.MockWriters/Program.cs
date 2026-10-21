using System.CommandLine;
using Npgsql;
using VehicleCache.MockWriters;
using VehicleCache.MockWriters.Scenarios;

// Mock writers (spec §8): they write ONLY to PostgreSQL. CDC carries everything else.
var connection = new Option<string>("--connection")
{
    Description = "Npgsql connection string",
    Recursive = true,
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? "Host=localhost;Username=postgres;Password=postgres;Database=fleet",
};
var rate = new Option<double>("--rate") { Description = "Statements per second (0 = unlimited)", Recursive = true };
var seed = new Option<int>("--seed") { Description = "Random seed", Recursive = true, DefaultValueFactory = _ => 1 };

var scenarioFile = new Argument<string>("file") { Description = "Scenario JSON (scenarios/*.json)" };
var scenarioCommand = new Command("scenario", "Run a deterministic scenario file") { scenarioFile };
scenarioCommand.SetAction((parse, ct) => RunScenarioAsync(parse, parse.GetValue(scenarioFile)!, ct));

var seedFile = new Option<string>("--file") { DefaultValueFactory = _ => "scenarios/seed.json" };
var seedCommand = new Command("seed", "Pre-snapshot data set (no deletes): scenario scenarios/seed.json") { seedFile };
seedCommand.SetAction((parse, ct) => RunScenarioAsync(parse, parse.GetValue(seedFile)!, ct));

var count = new Option<int>("--count") { Description = "Statements to write, then exit", DefaultValueFactory = _ => 500 };
var firstId = new Option<long>("--first-id") { DefaultValueFactory = _ => 100_000 };
var outOfOrder = new Option<int>("--out-of-order-pct") { DefaultValueFactory = _ => 20 };
var deletePct = new Option<int>("--delete-pct") { DefaultValueFactory = _ => 5 };
var vehiclesCommand = new Command("vehicles", "Random vehicle inserts, state transitions (some late) and deletes")
{
    count, firstId, outOfOrder, deletePct,
};
vehiclesCommand.SetAction(async (parse, ct) =>
{
    await using var dataSource = NpgsqlDataSource.Create(parse.GetValue(connection)!);
    await LoadWriters.VehiclesAsync(new FleetDb(dataSource), new Throttle(parse.GetValue(rate), TimeProvider.System),
        TimeProvider.System, new Random(parse.GetValue(seed)), parse.GetValue(count), parse.GetValue(firstId),
        parse.GetValue(outOfOrder), parse.GetValue(deletePct), ct);
    return 0;
});

var vehicles = new Option<int>("--vehicles") { DefaultValueFactory = _ => 50 };
var latePct = new Option<int>("--late-pct") { DefaultValueFactory = _ => 20 };
var dupPct = new Option<int>("--dup-pct") { DefaultValueFactory = _ => 10 };
var orphanPct = new Option<int>("--orphan-pct") { DefaultValueFactory = _ => 10 };
var positionsCommand = new Command("positions", "Random GPS inserts: late, duplicated and orphan fixes")
{
    count, firstId, vehicles, latePct, dupPct, orphanPct,
};
positionsCommand.SetAction(async (parse, ct) =>
{
    await using var dataSource = NpgsqlDataSource.Create(parse.GetValue(connection)!);
    await LoadWriters.PositionsAsync(new FleetDb(dataSource), new Throttle(parse.GetValue(rate), TimeProvider.System),
        TimeProvider.System, new Random(parse.GetValue(seed)), parse.GetValue(count), parse.GetValue(firstId),
        parse.GetValue(vehicles), parse.GetValue(latePct), parse.GetValue(dupPct), parse.GetValue(orphanPct), ct);
    return 0;
});

var root = new RootCommand("Vehicle cache mock writers — PostgreSQL only")
{
    connection, rate, seed, scenarioCommand, seedCommand, vehiclesCommand, positionsCommand,
};
return await root.Parse(args).InvokeAsync();

async Task<int> RunScenarioAsync(ParseResult parse, string path, CancellationToken ct)
{
    var scenario = Scenario.Load(path);
    await using var dataSource = NpgsqlDataSource.Create(parse.GetValue(connection)!);
    var runner = new ScenarioRunner(new FleetDb(dataSource), new Throttle(parse.GetValue(rate), TimeProvider.System));
    var stats = await runner.RunAsync(scenario, ct);
    Console.WriteLine($"scenario '{scenario.Name}' done: {stats}");
    return 0;
}
