using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VehicleCache.Projection.Debezium;

/// <summary>A record whose value is not a valid Debezium envelope for the expected table. Poison: never skipped (ADR-0005).</summary>
public sealed class CdcFormatException(string message, Exception? inner = null) : Exception(message, inner);

// Only the fields the projection relies on (spec §4). ts_ms, lsn, txId, sequence are deliberately not bound:
// they are never used for ordering (ADR-0002).
internal sealed record DebeziumSource(string Table, string? Snapshot);

internal sealed record VehicleRow(long Id, string Plate, string State, string StateChangedAt, long Version);

internal sealed record PositionRow(long Id, long VehicleId, double Lat, double Lng, string RecordedAt);

// `before` stays a JsonElement: on deletes only its PK is real, the other columns are zero-filled
// under REPLICA IDENTITY DEFAULT (see tests/VehicleCache.UnitTests/Fixtures/README.md).
internal sealed record VehicleEnvelope(string Op, JsonElement? Before, VehicleRow? After, DebeziumSource Source);

internal sealed record PositionEnvelope(string Op, JsonElement? Before, PositionRow? After, DebeziumSource Source);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(VehicleEnvelope))]
[JsonSerializable(typeof(PositionEnvelope))]
internal sealed partial class DebeziumJsonContext : JsonSerializerContext;

internal static class DebeziumEnvelopeParser
{
    public static T Parse<T>(byte[] value, JsonTypeInfo<T> typeInfo, string expectedTable)
        where T : class
    {
        T envelope;
        try
        {
            envelope = JsonSerializer.Deserialize(value, typeInfo)
                ?? throw new CdcFormatException("Envelope is JSON null.");
        }
        catch (JsonException ex)
        {
            throw new CdcFormatException($"Not a valid Debezium envelope: {ex.Message}", ex);
        }

        var table = envelope switch
        {
            VehicleEnvelope v => v.Source.Table,
            PositionEnvelope p => p.Source.Table,
            _ => throw new InvalidOperationException($"Unsupported envelope type {typeof(T).Name}."),
        };
        if (table != expectedTable)
        {
            throw new CdcFormatException($"Expected table '{expectedTable}' but the event is for '{table}'.");
        }

        return envelope;
    }

    public static long ReadBeforeId(JsonElement? before)
    {
        if (before is { ValueKind: JsonValueKind.Object } row
            && row.TryGetProperty("id", out var id)
            && id.TryGetInt64(out var value))
        {
            return value;
        }

        throw new CdcFormatException("Delete event without before.id.");
    }
}
