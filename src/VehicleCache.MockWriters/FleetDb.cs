using Npgsql;

namespace VehicleCache.MockWriters;

/// <summary>
/// The writers' only side effect: plain SQL against the source tables. One statement = one transaction = one CDC event.
/// Versions, retirement of deleted ids and append-only positions are enforced by triggers (spec §3), not here.
/// </summary>
public sealed class FleetDb(NpgsqlDataSource dataSource)
{
    public async Task InsertVehicleAsync(long id, string plate, string state, DateTimeOffset stateChangedAt, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO vehicles (id, plate, state, state_changed_at) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(plate);
        cmd.Parameters.AddWithValue(state);
        cmd.Parameters.AddWithValue(stateChangedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Sets an explicit business time — it may be older than the current one (late report); the DB accepts it.</summary>
    public async Task UpdateVehicleAsync(long id, string state, DateTimeOffset stateChangedAt, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE vehicles SET state = $2, state_changed_at = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(state);
        cmd.Parameters.AddWithValue(stateChangedAt);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException($"Vehicle {id} does not exist (update).");
        }
    }

    /// <summary>Out-of-order business update: the new state_changed_at is older than the row's current one.</summary>
    public async Task UpdateVehicleLateAsync(long id, string state, TimeSpan olderBy, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "UPDATE vehicles SET state = $2, state_changed_at = state_changed_at - $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(state);
        cmd.Parameters.AddWithValue(olderBy);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException($"Vehicle {id} does not exist (late update).");
        }
    }

    public async Task DeleteVehicleAsync(long id, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand("DELETE FROM vehicles WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        {
            throw new InvalidOperationException($"Vehicle {id} does not exist (delete).");
        }
    }

    public async Task InsertPositionAsync(long vehicleId, double lat, double lng, DateTimeOffset recordedAt, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "INSERT INTO positions (vehicle_id, lat, lng, recorded_at) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(vehicleId);
        cmd.Parameters.AddWithValue(lat);
        cmd.Parameters.AddWithValue(lng);
        cmd.Parameters.AddWithValue(recordedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
