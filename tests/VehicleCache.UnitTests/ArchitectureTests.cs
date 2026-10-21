using System.Reflection;
using VehicleCache.Projection;
using VehicleCache.Worker.Cache;

namespace VehicleCache.UnitTests;

public class ArchitectureTests
{
    private static string[] References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();

    [Fact]
    [Trait("TB", "17")]
    public void Worker_has_no_database_client_reference()
    {
        References(typeof(ValkeyVehicleCacheWriter).Assembly)
            .ShouldNotContain(name => name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Projection_is_pure()
    {
        References(typeof(CacheCommand).Assembly).ShouldAllBe(name =>
            !name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("StackExchange", StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith("Confluent", StringComparison.OrdinalIgnoreCase));
    }
}
