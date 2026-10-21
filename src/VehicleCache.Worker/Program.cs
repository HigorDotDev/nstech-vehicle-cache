using Microsoft.Extensions.Options;
using StackExchange.Redis;
using VehicleCache.Projection.Debezium;
using VehicleCache.Worker.Cache;
using VehicleCache.Worker.Consumers;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss.fff ";
    o.UseUtcTimestamp = true;
});

builder.Services.AddOptions<KafkaOptions>().BindConfiguration(KafkaOptions.Section).ValidateOnStart();

// Valkey only — this process has no database client and no Postgres configuration (CLAUDE.md invariant 1).
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Valkey")
        ?? throw new InvalidOperationException("ConnectionStrings:Valkey is required.")));
builder.Services.AddSingleton<IVehicleCacheWriter, ValkeyVehicleCacheWriter>();
builder.Services.AddSingleton<ProjectionMetrics>();

// One consumer per topic, so a burst of positions never delays state changes (spec §7).
AddConsumer(o => o.VehiclesTopic, VehicleChangeMapper.Map);
AddConsumer(o => o.PositionsTopic, PositionChangeMapper.Map);

await builder.Build().RunAsync();

void AddConsumer(Func<KafkaOptions, string> topic, Func<byte[]?, VehicleCache.Projection.CacheCommand> map) =>
    builder.Services.AddSingleton<IHostedService>(sp =>
    {
        var options = sp.GetRequiredService<IOptions<KafkaOptions>>();
        var handler = new CdcRecordHandler(
            topic(options.Value),
            map,
            sp.GetRequiredService<IVehicleCacheWriter>(),
            sp.GetRequiredService<ProjectionMetrics>(),
            sp.GetRequiredService<ILogger<CdcRecordHandler>>());
        return ActivatorUtilities.CreateInstance<KafkaCdcConsumer>(sp, handler);
    });
