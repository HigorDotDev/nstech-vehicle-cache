using StackExchange.Redis;
using VehicleCache.Worker.Cache;

// Phase 2: projection core only. Kafka consumers (one BackgroundService per topic) arrive in Phase 3.
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString("Valkey") ?? "localhost:6379"));
builder.Services.AddSingleton<IVehicleCacheWriter, ValkeyVehicleCacheWriter>();

var host = builder.Build();
host.Run();
