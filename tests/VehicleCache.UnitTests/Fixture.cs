namespace VehicleCache.UnitTests;

/// <summary>Loads a captured Debezium record value (see Fixtures/README.md). Empty file = Kafka tombstone (null).</summary>
internal static class Fixture
{
    public static byte[]? Value(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{name}.json"));
        return bytes.Length == 0 ? null : bytes;
    }
}
