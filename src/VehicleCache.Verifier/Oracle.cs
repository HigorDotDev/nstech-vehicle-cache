using System.Globalization;
using Npgsql;
using VehicleCache.Projection;
using VehicleCache.Projection.Debezium;

namespace VehicleCache.Verifier;

/// <summary>
/// What the cache MUST contain, computed from the source tables with SQL (spec §2) — independent of the worker:
/// <code>
/// state    = vehicles row, or {deleted:1} if the id is in retired_vehicle_ids
/// position = argmax over positions of (recorded_at, id), per vehicle_id
/// </code>
/// Values use the spec §5 canonical formats.
/// </summary>
public static class Oracle
{
    public static async Task<CacheView> BuildAsync(NpgsqlDataSource db, CancellationToken ct)
    {
        var view = new CacheView();
        var retired = new HashSet<long>();

        // One snapshot for all three queries, so the oracle is a consistent point in time even while writers run.
        await using var connection = await db.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);

        await using (var cmd = new NpgsqlCommand("SELECT vehicle_id FROM retired_vehicle_ids", connection, transaction))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt64(0);
                retired.Add(id);
                view[CacheKeys.Vehicle(id)]["deleted"] = "1";
            }
        }

        await using (var cmd = new NpgsqlCommand("SELECT id, plate, state, state_changed_at, version FROM vehicles", connection, transaction))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var hash = view[CacheKeys.Vehicle(reader.GetInt64(0))];
                hash["plate"] = reader.GetString(1);
                hash["state"] = reader.GetString(2);
                hash["stateChangedAt"] = CdcTimestamps.Format(reader.GetFieldValue<DateTimeOffset>(3));
                hash["stateVersion"] = Invariant(reader.GetInt64(4));
            }
        }

        const string latestPositions = """
            SELECT DISTINCT ON (vehicle_id) vehicle_id, id, lat, lng, recorded_at
            FROM positions
            ORDER BY vehicle_id, recorded_at DESC, id DESC
            """;
        await using (var cmd = new NpgsqlCommand(latestPositions, connection, transaction))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var vehicleId = reader.GetInt64(0);
                if (retired.Contains(vehicleId))
                {
                    continue; // a tombstone absorbs everything (ADR-0004)
                }

                var recordedAt = reader.GetFieldValue<DateTimeOffset>(4);
                var hash = view[CacheKeys.Vehicle(vehicleId)];
                hash["positionId"] = Invariant(reader.GetInt64(1));
                hash["lat"] = reader.GetDouble(2).ToString(CultureInfo.InvariantCulture);
                hash["lng"] = reader.GetDouble(3).ToString(CultureInfo.InvariantCulture);
                hash["recordedAt"] = CdcTimestamps.Format(recordedAt);
                hash["recordedAtUs"] = Invariant((recordedAt.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMicrosecond);
            }
        }

        return view;
    }

    private static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
