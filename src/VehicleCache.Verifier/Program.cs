using System.CommandLine;
using System.Diagnostics;
using Npgsql;
using StackExchange.Redis;
using VehicleCache.Verifier;

// e2e verifier (spec §9). `dump` writes ONLY the canonical view to stdout (captured by `just`); diagnostics go to stderr.
var postgres = new Option<string>("--postgres")
{
    Recursive = true,
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
        ?? "Host=localhost;Username=postgres;Password=postgres;Database=fleet",
};
var valkey = new Option<string>("--valkey")
{
    Recursive = true,
    DefaultValueFactory = _ => Environment.GetEnvironmentVariable("ConnectionStrings__Valkey") ?? "localhost:6379",
};

var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for convergence", DefaultValueFactory = _ => 180 };
var expect = new Command("expect", "Poll Valkey until it equals the Postgres oracle (TB-15)") { timeout };
expect.SetAction(async (parse, ct) =>
{
    await using var db = NpgsqlDataSource.Create(parse.GetValue(postgres)!);
    await using var redis = await ConnectionMultiplexer.ConnectAsync(parse.GetValue(valkey)!);
    var deadline = Stopwatch.StartNew();
    var limit = TimeSpan.FromSeconds(parse.GetValue(timeout));

    while (true)
    {
        // Oracle first, cache second: the cache can only catch up with the oracle, never overtake it.
        var expected = await Oracle.BuildAsync(db, ct);
        var actual = await CacheView.ReadAsync(redis);
        if (expected.Format() == actual.Format())
        {
            Console.WriteLine($"cache == oracle: {expected.Count} vehicles after {deadline.Elapsed.TotalSeconds:F1}s");
            return 0;
        }

        if (deadline.Elapsed > limit)
        {
            var differences = CacheView.Diff(expected, actual);
            Console.Error.WriteLine($"cache != oracle after {limit.TotalSeconds}s (expected {expected.Count}, actual {actual.Count} keys):");
            foreach (var difference in differences)
            {
                Console.Error.WriteLine(difference);
            }

            Console.WriteLine($"FAILED: {differences.Count}+ differing vehicles");
            return 1;
        }

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }
});

var dump = new Command("dump", "Print the canonical cache view (one sorted line per vehicle)");
dump.SetAction(async (parse, ct) =>
{
    await using var redis = await ConnectionMultiplexer.ConnectAsync(parse.GetValue(valkey)!);
    Console.Write((await CacheView.ReadAsync(redis)).Format());
    return 0;
});

var root = new RootCommand("Vehicle cache e2e verifier") { postgres, valkey, expect, dump };
return await root.Parse(args).InvokeAsync();
