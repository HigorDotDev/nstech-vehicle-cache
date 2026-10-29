namespace VehicleCache.Projection.Debezium;

/// <summary>Maps a <c>fleet.public.vehicles</c> record value to a cache command (spec §4 table).</summary>
public static class VehicleChangeMapper
{
    public const string Table = "vehicles";

    /// <param name="value">Record value; <c>null</c> is a Kafka tombstone.</param>
    /// <exception cref="CdcFormatException">The value is not a valid vehicles change event.</exception>
    public static CacheCommand Map(byte[]? value)
    {
        if (value is null)
        {
            return new Skip("Kafka tombstone");
        }

        var envelope = DebeziumEnvelopeParser.Parse(value, DebeziumJsonContext.Default.VehicleEnvelope, Table);

        return envelope.Op switch
        {
            // Snapshot reads are ordinary upserts: the row's version makes them safe (ADR-0006).
            "r" or "c" or "u" => envelope.After is { } row
                ? new UpsertState(row.Id, row.Version, row.State, row.Plate, CdcTimestamps.Normalize(row.StateChangedAt))
                : throw new CdcFormatException($"op '{envelope.Op}' without 'after'."),
            "d" => new DeleteVehicle(DebeziumEnvelopeParser.ReadBeforeId(envelope.Before)),
            _ => throw new CdcFormatException($"Unsupported op '{envelope.Op}' on {Table}."),
        };
    }
}
