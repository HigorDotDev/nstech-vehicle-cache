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

    [Theory]
    [Trait("TB", "17")]
    [InlineData("src/VehicleCache.Worker/VehicleCache.Worker.csproj")]
    [InlineData("src/VehicleCache.Projection/VehicleCache.Projection.csproj")]
    public void Project_files_declare_no_database_client(string projectFile)
    {
        // Assembly references only show what the compiled code uses; an unused package would slip through.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "VehicleCache.slnx")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull("repository root (VehicleCache.slnx) not found above the test output");
        var references = System.Xml.Linq.XDocument.Load(Path.Combine(root.FullName, projectFile))
            .Descendants()
            .Where(e => e.Name.LocalName is "PackageReference" or "ProjectReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .ToList();
        foreach (var forbidden in new[] { "Npgsql", "Dapper", "EntityFrameworkCore", "MockWriters", "Verifier" })
        {
            references.ShouldNotContain(r => r.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"{projectFile} must not reference {forbidden}");
        }
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
