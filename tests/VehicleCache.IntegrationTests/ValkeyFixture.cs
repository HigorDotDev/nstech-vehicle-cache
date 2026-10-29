using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using StackExchange.Redis;
using VehicleCache.Projection;
using VehicleCache.Verifier;
using VehicleCache.Worker.Cache;

namespace VehicleCache.IntegrationTests;

/// <summary>One real Valkey (same image as k8s/infra/valkey.yaml) shared by the "valkey" collection.</summary>
public sealed class ValkeyFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("valkey/valkey:8.1.3")
        .WithPortBinding(6379, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Ready to accept connections"))
        .Build();

    public IConnectionMultiplexer Redis { get; private set; } = null!;

    public IVehicleCacheWriter Writer { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Redis = await ConnectionMultiplexer.ConnectAsync(
            $"{_container.Hostname}:{_container.GetMappedPublicPort(6379)},allowAdmin=true");
        Writer = new ValkeyVehicleCacheWriter(Redis);
    }

    public async Task DisposeAsync()
    {
        await Redis.DisposeAsync();
        await _container.DisposeAsync();
    }

    public Task FlushAsync() => Server.FlushAllDatabasesAsync();

    public async Task<Dictionary<string, string>> HashAsync(long vehicleId)
    {
        var entries = await Redis.GetDatabase().HashGetAllAsync(CacheKeys.Vehicle(vehicleId));
        return entries.ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());
    }

    /// <summary>The verifier's canonical dump (spec §9). Byte-equality = identical cache.</summary>
    public async Task<string> DumpAsync() => (await CacheView.ReadAsync(Redis)).Format();

    private IServer Server => Redis.GetServer(Redis.GetEndPoints()[0]);
}

[CollectionDefinition("valkey")]
public sealed class ValkeyCollection : ICollectionFixture<ValkeyFixture>;
