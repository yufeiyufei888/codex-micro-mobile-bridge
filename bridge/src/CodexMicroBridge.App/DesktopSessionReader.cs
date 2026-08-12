using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexMicroBridge.App;

internal sealed record DesktopSessionMessage(
    string MessageId,
    string TurnId,
    string Role,
    string Text,
    DateTimeOffset Timestamp);

internal sealed class DesktopSessionReader
{
    private const int TailBudgetBytes = 8 * 1024 * 1024;
    private readonly string _sessionsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex",
        "sessions");

    public string? FindSessionContainingPrompt(string prompt, DateTimeOffset notBefore)
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(_sessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
                     .Select(path => new FileInfo(path))
                     .Where(file => file.LastWriteTimeUtc >= notBefore.UtcDateTime.AddSeconds(-5))
                     .OrderByDescending(file => file.LastWriteTimeUtc)
                     .Take(24)
                     .Select(file => file.FullName))
        {
            if (TailContainsUserMessage(path, prompt))
            {
                return path;
            }
        }

        return null;
    }

    public string? FindMostRecentRootSession(DateTimeOffset notBefore)
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            return null;
        }

        return Directory.EnumerateFiles(_sessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTimeUtc >= notBefore.UtcDateTime.AddMinutes(-1))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(24)
            .FirstOrDefault(file => IsRootSession(file.FullName))
            ?.FullName;
    }

    public DesktopSessionMessage? ReadLatestAssistantMessage(string path)
    {
        return ReadConversationMessages(path).LastOrDefault(message => message.Role == "assistant");
    }

    public IReadOnlyList<DesktopSessionMessage> ReadConversationMessages(string path)
    {
        var messages = new List<(DesktopSessionMessage Message, bool IsCanonical)>();
        foreach (var line in ReadTailLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var timestamp = ReadTimestamp(root);
                if (!root.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                var outerType = OptionalString(root, "type");
                var payloadType = OptionalString(payload, "type");
                string? role = null;
                string? text = null;
                string? messageId = null;
                string? turnId = null;
                var canonical = false;
                if (outerType == "response_item" && payloadType == "message")
                {
                    role = OptionalString(payload, "role");
                    if (role is not ("user" or "assistant"))
                    {
                        continue;
                    }

                    text = ReadContentText(payload, role == "user" ? "input_text" : "output_text");
                    messageId = OptionalString(payload, "id");
                    turnId = ReadNestedString(payload, "internal_chat_message_metadata_passthrough", "turn_id");
                    canonical = true;
                }
                else if (outerType == "event_msg" && payloadType == "agent_message")
                {
                    role = "assistant";
                    text = OptionalString(payload, "message");
                }
                else if (outerType == "event_msg" && payloadType == "user_message")
                {
                    role = "user";
                    text = OptionalString(payload, "message");
                }

                if (role is null || string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var duplicateIndex = messages.FindLastIndex(existing =>
                    string.Equals(existing.Message.Role, role, StringComparison.Ordinal) &&
                    string.Equals(existing.Message.Text.Trim(), text.Trim(), StringComparison.Ordinal) &&
                    Math.Abs((existing.Message.Timestamp - timestamp).TotalSeconds) <= 10);
                var stableId = messageId ?? CreateStableMessageId(role, text, timestamp);
                var candidate = new DesktopSessionMessage(
                    stableId,
                    turnId ?? $"desktop-turn-{timestamp.ToUnixTimeMilliseconds()}",
                    role,
                    text,
                    timestamp);
                if (duplicateIndex < 0)
                {
                    messages.Add((candidate, canonical));
                }
                else if (canonical && !messages[duplicateIndex].IsCanonical)
                {
                    messages[duplicateIndex] = (candidate, true);
                }
            }
            catch (JsonException)
            {
                // Ignore a partial tail line while Codex is still appending it.
            }
        }

        return messages
            .OrderBy(message => message.Message.Timestamp)
            .Select(message => message.Message)
            .ToArray();
    }

    private static string CreateStableMessageId(string role, string text, DateTimeOffset timestamp)
    {
        var material = $"{role}\0{timestamp.ToUnixTimeMilliseconds()}\0{text}";
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"desktop-{Convert.ToHexString(digest)[..24].ToLowerInvariant()}";
    }

    private static bool TailContainsUserMessage(string path, string prompt)
    {
        foreach (var line in ReadTailLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!root.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                var outerType = OptionalString(root, "type");
                var payloadType = OptionalString(payload, "type");
                var text = outerType switch
                {
                    "response_item" when payloadType == "message" && OptionalString(payload, "role") == "user" =>
                        ReadContentText(payload, "input_text"),
                    "event_msg" when payloadType == "user_message" => OptionalString(payload, "message"),
                    _ => null,
                };
                if (string.Equals(text?.Trim(), prompt.Trim(), StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Ignore incomplete lines.
            }
        }

        return false;
    }

    private static bool IsRootSession(string path)
    {
        try
        {
            var firstLine = File.ReadLines(path).FirstOrDefault();
            if (firstLine is null)
            {
                return false;
            }

            using var document = JsonDocument.Parse(firstLine);
            var root = document.RootElement;
            if (OptionalString(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var payload))
            {
                return false;
            }

            return (!payload.TryGetProperty("parent_thread_id", out var parent) || parent.ValueKind == JsonValueKind.Null) &&
                (!payload.TryGetProperty("thread_source", out var source) || source.GetString() != "subagent");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> ReadTailLines(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - TailBudgetBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            if (start > 0)
            {
                _ = reader.ReadLine();
            }

            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root) =>
        DateTimeOffset.TryParse(OptionalString(root, "timestamp"), out var timestamp)
            ? timestamp
            : DateTimeOffset.UtcNow;

    private static string? ReadContentText(JsonElement payload, string expectedType)
    {
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return string.Join("\n", content.EnumerateArray()
            .Where(item => OptionalString(item, "type") == expectedType)
            .Select(item => OptionalString(item, "text"))
            .Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(objectName, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? OptionalString(nested, propertyName)
            : null;
}
