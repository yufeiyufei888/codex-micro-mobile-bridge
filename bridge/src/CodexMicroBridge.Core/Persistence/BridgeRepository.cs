using System.Text.Json;
using CodexMicroBridge.Core.Security;
using CodexMicroBridge.Core.State;
using Microsoft.Data.Sqlite;

namespace CodexMicroBridge.Core.Persistence;

public sealed record PairedDevice(
    string DeviceId,
    string DisplayName,
    byte[] PublicKeySpki,
    DateTimeOffset AddedAt,
    DateTimeOffset LastSeenAt);

public sealed record SlotAssignment(int Slot, string ThreadId, DateTimeOffset UpdatedAt);

public sealed record AllowedProject(string ProjectId, string Path, string DisplayName)
{
    public override string ToString() => $"{DisplayName} — {Path}";
}

public sealed record BridgeMessage(
    string MessageId,
    string ThreadId,
    string TurnId,
    string ItemId,
    string Role,
    string Text,
    DateTimeOffset CompletedAt);

public sealed class BridgeRepository
{
    private readonly string _connectionString;
    private readonly IFieldProtector _fieldProtector;

    public BridgeRepository(string databasePath, IFieldProtector? fieldProtector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        _fieldProtector = fieldProtector ?? new PassthroughFieldProtector();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS managed_threads (
                thread_id TEXT PRIMARY KEY,
                snapshot_json TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS command_results (
                command_id TEXT PRIMARY KEY,
                operation TEXT NOT NULL,
                response_json TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS idempotent_commands (
                device_id TEXT NOT NULL,
                command_id TEXT NOT NULL,
                operation TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                response_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(device_id, command_id)
            );
            CREATE TABLE IF NOT EXISTS projects (
                project_id TEXT PRIMARY KEY,
                path TEXT NOT NULL,
                display_name TEXT NOT NULL,
                added_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS paired_devices (
                device_id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                public_key_spki BLOB NOT NULL,
                added_utc TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS bridge_meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS slot_assignments (
                slot INTEGER PRIMARY KEY,
                thread_id TEXT NOT NULL UNIQUE,
                updated_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS completed_messages (
                message_id TEXT PRIMARY KEY,
                thread_id TEXT NOT NULL,
                turn_id TEXT NOT NULL,
                item_id TEXT NOT NULL,
                role TEXT NOT NULL,
                text TEXT NOT NULL,
                completed_utc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveTaskAsync(BridgeTaskSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO managed_threads(thread_id, snapshot_json, updated_utc)
            VALUES($threadId, $snapshot, $updated)
            ON CONFLICT(thread_id) DO UPDATE SET
                snapshot_json = excluded.snapshot_json,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$threadId", snapshot.ThreadId);
        command.Parameters.AddWithValue("$snapshot", _fieldProtector.Protect(JsonSerializer.Serialize(snapshot)));
        command.Parameters.AddWithValue("$updated", snapshot.UpdatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BridgeTaskSnapshot>> GetTasksAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<BridgeTaskSnapshot>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT snapshot_json FROM managed_threads ORDER BY updated_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = JsonSerializer.Deserialize<BridgeTaskSnapshot>(_fieldProtector.Unprotect(reader.GetString(0)));
            if (snapshot is not null)
            {
                result.Add(snapshot);
            }
        }

        return result;
    }

    public async Task<string?> GetCommandResultAsync(
        string deviceId,
        string commandId,
        string operation,
        string requestHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT response_json, operation, request_hash
            FROM idempotent_commands WHERE device_id = $deviceId AND command_id = $id;
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$id", commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var storedOperation = reader.GetString(1);
        var storedHash = reader.GetString(2);
        if (!string.Equals(operation, storedOperation, StringComparison.Ordinal) ||
            !string.Equals(requestHash, storedHash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException(commandId);
        }

        return _fieldProtector.Unprotect(reader.GetString(0));
    }

    public async Task SaveCommandResultAsync(
        string deviceId,
        string commandId,
        string operation,
        string requestHash,
        string responseJson,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO idempotent_commands(device_id, command_id, operation, request_hash, response_json, created_utc)
            VALUES($deviceId, $id, $operation, $requestHash, $response, $created);
            """;
        command.Parameters.AddWithValue("$deviceId", deviceId);
        command.Parameters.AddWithValue("$id", commandId);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$response", _fieldProtector.Protect(responseJson));
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var prune = connection.CreateCommand();
        prune.CommandText = """
            DELETE FROM idempotent_commands WHERE created_utc < $cutoff;
            DELETE FROM idempotent_commands
            WHERE device_id = $deviceId AND rowid NOT IN (
                SELECT rowid FROM idempotent_commands
                WHERE device_id = $deviceId
                ORDER BY created_utc DESC LIMIT 4096
            );
            """;
        prune.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddHours(-24).ToString("O"));
        prune.Parameters.AddWithValue("$deviceId", deviceId);
        await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddAllowedProjectAsync(AllowedProject project, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO projects(project_id, path, display_name, added_utc)
            VALUES($id, $path, $name, $added)
            ON CONFLICT(project_id) DO UPDATE SET
                path = excluded.path,
                display_name = excluded.display_name;
            """;
        command.Parameters.AddWithValue("$id", project.ProjectId);
        command.Parameters.AddWithValue("$path", _fieldProtector.Protect(project.Path));
        command.Parameters.AddWithValue("$name", _fieldProtector.Protect(project.DisplayName));
        command.Parameters.AddWithValue("$added", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAllowedProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM projects WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AllowedProject>> GetAllowedProjectsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AllowedProject>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id, path, display_name FROM projects ORDER BY added_utc;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AllowedProject(
                reader.GetString(0),
                _fieldProtector.Unprotect(reader.GetString(1)),
                _fieldProtector.Unprotect(reader.GetString(2))));
        }

        return result;
    }

    public async Task<AllowedProject?> GetAllowedProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, display_name FROM projects WHERE project_id = $id;";
        command.Parameters.AddWithValue("$id", projectId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new AllowedProject(
                projectId,
                _fieldProtector.Unprotect(reader.GetString(0)),
                _fieldProtector.Unprotect(reader.GetString(1)))
            : null;
    }

    public async Task UpsertPairedDeviceAsync(PairedDevice device, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO paired_devices(device_id, display_name, public_key_spki, added_utc, last_seen_utc)
            VALUES($id, $name, $key, $added, $seen)
            ON CONFLICT(device_id) DO UPDATE SET
                display_name = excluded.display_name,
                public_key_spki = excluded.public_key_spki,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$id", device.DeviceId);
        command.Parameters.AddWithValue("$name", device.DisplayName);
        command.Parameters.AddWithValue("$key", device.PublicKeySpki);
        command.Parameters.AddWithValue("$added", device.AddedAt.ToString("O"));
        command.Parameters.AddWithValue("$seen", device.LastSeenAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PairedDevice?> GetPairedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT display_name, public_key_spki, added_utc, last_seen_utc
            FROM paired_devices WHERE device_id = $id;
            """;
        command.Parameters.AddWithValue("$id", deviceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new PairedDevice(
            deviceId,
            reader.GetString(0),
            (byte[])reader[1],
            DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public async Task<IReadOnlyList<PairedDevice>> GetPairedDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<PairedDevice>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, display_name, public_key_spki, added_utc, last_seen_utc
            FROM paired_devices ORDER BY last_seen_utc DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new PairedDevice(
                reader.GetString(0),
                reader.GetString(1),
                (byte[])reader[2],
                DateTimeOffset.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public async Task<bool> DeletePairedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM paired_devices WHERE device_id = $id;";
        command.Parameters.AddWithValue("$id", deviceId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<string?> GetMetaAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM bridge_meta WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    public async Task SetMetaAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bridge_meta(key, value) VALUES($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AssignSlotAsync(int slot, string threadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var removeExisting = connection.CreateCommand())
        {
            removeExisting.Transaction = (SqliteTransaction)transaction;
            removeExisting.CommandText = "DELETE FROM slot_assignments WHERE slot = $slot OR thread_id = $threadId;";
            removeExisting.Parameters.AddWithValue("$slot", slot);
            removeExisting.Parameters.AddWithValue("$threadId", threadId);
            await removeExisting.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = "INSERT INTO slot_assignments(slot, thread_id, updated_utc) VALUES($slot, $threadId, $updated);";
            insert.Parameters.AddWithValue("$slot", slot);
            insert.Parameters.AddWithValue("$threadId", threadId);
            insert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SlotAssignment>> GetSlotAssignmentsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SlotAssignment>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT slot, thread_id, updated_utc FROM slot_assignments ORDER BY slot;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SlotAssignment(
                reader.GetInt32(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return result;
    }

    public async Task ClearSlotAsync(int slot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM slot_assignments WHERE slot = $slot;";
        command.Parameters.AddWithValue("$slot", slot);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveMessageAsync(BridgeMessage message, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO completed_messages(message_id, thread_id, turn_id, item_id, role, text, completed_utc)
            VALUES($messageId, $threadId, $turnId, $itemId, $role, $text, $completed)
            ON CONFLICT(message_id) DO UPDATE SET
                text = excluded.text,
                completed_utc = excluded.completed_utc;
            """;
        command.Parameters.AddWithValue("$messageId", message.MessageId);
        command.Parameters.AddWithValue("$threadId", message.ThreadId);
        command.Parameters.AddWithValue("$turnId", message.TurnId);
        command.Parameters.AddWithValue("$itemId", message.ItemId);
        command.Parameters.AddWithValue("$role", message.Role);
        command.Parameters.AddWithValue("$text", _fieldProtector.Protect(message.Text));
        command.Parameters.AddWithValue("$completed", message.CompletedAt.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> MessageExistsAsync(string messageId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM completed_messages WHERE message_id = $messageId);";
        command.Parameters.AddWithValue("$messageId", messageId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) != 0;
    }

    public async Task<IReadOnlyList<BridgeMessage>> GetMessagesAsync(string threadId, CancellationToken cancellationToken = default)
    {
        var messages = new List<BridgeMessage>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, turn_id, item_id, role, text, completed_utc
            FROM completed_messages WHERE thread_id = $threadId ORDER BY completed_utc;
            """;
        command.Parameters.AddWithValue("$threadId", threadId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            messages.Add(new BridgeMessage(
                reader.GetString(0),
                threadId,
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                _fieldProtector.Unprotect(reader.GetString(4)),
                DateTimeOffset.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }

        return messages;
    }

    public async Task<string?> GetLatestMessageIdAsync(string threadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id FROM completed_messages
            WHERE thread_id = $threadId
            ORDER BY completed_utc DESC, rowid DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$threadId", threadId);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
