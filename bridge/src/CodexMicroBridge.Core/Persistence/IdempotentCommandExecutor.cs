using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexMicroBridge.Core.Persistence;

public sealed class IdempotentCommandExecutor
{
    private readonly BridgeRepository _repository;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public IdempotentCommandExecutor(BridgeRepository repository)
    {
        _repository = repository;
    }

    public async Task<JsonElement> ExecuteAsync(
        string deviceId,
        string commandId,
        string operation,
        JsonElement parameters,
        Func<CancellationToken, Task<JsonElement>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(action);
        if (commandId.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(commandId), "Command IDs are limited to 128 characters.");
        }

        if (commandId.Length < 16)
        {
            throw new ArgumentOutOfRangeException(nameof(commandId), "Command IDs must contain at least 16 characters.");
        }

        var requestHash = ComputeRequestHash(operation, parameters);
        var lockKey = $"{deviceId}\0{commandId}";
        var commandLock = _locks.GetOrAdd(lockKey, static _ => new SemaphoreSlim(1, 1));
        await commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await _repository.GetCommandResultAsync(
                deviceId,
                commandId,
                operation,
                requestHash,
                cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                using var document = JsonDocument.Parse(stored);
                return document.RootElement.Clone();
            }

            var result = await action(cancellationToken).ConfigureAwait(false);
            await _repository.SaveCommandResultAsync(
                deviceId,
                commandId,
                operation,
                requestHash,
                result.GetRawText(),
                cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            commandLock.Release();
        }
    }

    private static string ComputeRequestHash(string operation, JsonElement parameters)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(writer, parameters, skipClientCommandId: true);
        }

        var operationBytes = Encoding.UTF8.GetBytes(operation + "\n");
        var combined = new byte[operationBytes.Length + stream.Length];
        operationBytes.CopyTo(combined, 0);
        stream.GetBuffer().AsSpan(0, checked((int)stream.Length)).CopyTo(combined.AsSpan(operationBytes.Length));
        return Convert.ToHexString(SHA256.HashData(combined));
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool skipClientCommandId = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (skipClientCommandId && string.Equals(property.Name, "clientCommandId", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}

public sealed class IdempotencyConflictException(string commandId)
    : Exception($"clientCommandId '{commandId}' was reused with a different canonical operation body.");
