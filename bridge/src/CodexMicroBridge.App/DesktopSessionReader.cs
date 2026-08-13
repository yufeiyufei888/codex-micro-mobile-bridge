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

internal sealed record DesktopSessionFileStamp(
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed class DesktopSessionReader
{
    private const int TailBudgetBytes = 8 * 1024 * 1024;
    private const int LiveTailBudgetBytes = 2 * 1024 * 1024;
    private const int SessionCandidateLimit = 256;
    private readonly string _sessionsDirectory;

    public DesktopSessionReader(string? sessionsDirectory = null)
    {
        _sessionsDirectory = sessionsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex",
            "sessions");
    }

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
                     .Take(SessionCandidateLimit)
                     .Where(file => IsRootSession(file.FullName))
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
            .Take(SessionCandidateLimit)
            .FirstOrDefault(file => IsRootSession(file.FullName))
            ?.FullName;
    }

    public DesktopSessionMessage? ReadLatestAssistantMessage(string path)
    {
        return ReadConversationMessages(path).LastOrDefault(message => message.Role == "assistant");
    }

    public DesktopSessionFileStamp? ReadFileStamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists
                ? new DesktopSessionFileStamp(file.Length, file.LastWriteTimeUtc)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<DesktopSessionMessage> ReadConversationMessages(string path)
    {
        return ReadConversationMessages(path, TailBudgetBytes, includeEventMirrors: true);
    }

    public IReadOnlyList<DesktopSessionMessage> ReadCanonicalLiveMessages(string path)
    {
        return ReadConversationMessages(path, LiveTailBudgetBytes, includeEventMirrors: false);
    }

    public IReadOnlyList<DesktopSessionMessage> ReadCanonicalConversationMessages(string path)
    {
        return ReadConversationMessages(path, TailBudgetBytes, includeEventMirrors: false);
    }

    public IReadOnlyList<DesktopSessionMessage>? ReadCanonicalMessagesSince(string path, long previousLength)
    {
        var lines = ReadLinesFromOffset(path, previousLength);
        return lines is null
            ? null
            : ParseConversationMessages(lines, includeEventMirrors: false);
    }

    private static IReadOnlyList<DesktopSessionMessage> ReadConversationMessages(
        string path,
        int tailBudgetBytes,
        bool includeEventMirrors)
    {
        return ParseConversationMessages(ReadTailLines(path, tailBudgetBytes), includeEventMirrors);
    }

    private static IReadOnlyList<DesktopSessionMessage> ParseConversationMessages(
        IEnumerable<string> lines,
        bool includeEventMirrors)
    {
        var messages = new List<(DesktopSessionMessage Message, bool IsCanonical)>();
        foreach (var line in lines)
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
                else if (includeEventMirrors && outerType == "event_msg" && payloadType == "agent_message")
                {
                    role = "assistant";
                    text = OptionalString(payload, "message");
                }
                else if (includeEventMirrors && outerType == "event_msg" && payloadType == "user_message")
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

    private static IReadOnlyList<string>? ReadLinesFromOffset(string path, long previousLength)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var offset = Math.Clamp(previousLength, 0, stream.Length);
            var start = FindContainingLineStart(stream, offset);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: false);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return lines;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long FindContainingLineStart(FileStream stream, long offset)
    {
        if (offset <= 0)
        {
            return 0;
        }

        stream.Seek(offset - 1, SeekOrigin.Begin);
        if (stream.ReadByte() == '\n')
        {
            return offset;
        }

        const int blockSize = 4096;
        var buffer = new byte[blockSize];
        var cursor = offset;
        while (cursor > 0)
        {
            var blockStart = Math.Max(0, cursor - blockSize);
            var count = checked((int)(cursor - blockStart));
            stream.Seek(blockStart, SeekOrigin.Begin);
            var read = stream.Read(buffer, 0, count);
            for (var index = read - 1; index >= 0; index--)
            {
                if (buffer[index] == (byte)'\n')
                {
                    return blockStart + index + 1;
                }
            }

            cursor = blockStart;
        }

        return 0;
    }

    private static string CreateStableMessageId(string role, string text, DateTimeOffset timestamp)
    {
        var material = $"{role}\0{timestamp.ToUnixTimeMilliseconds()}\0{text}";
        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $"desktop-{Convert.ToHexString(digest)[..24].ToLowerInvariant()}";
    }

    private static bool TailContainsUserMessage(string path, string prompt)
    {
        foreach (var line in ReadTailLines(path, TailBudgetBytes))
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
            // Codex keeps the active rollout open for append. File.ReadLines opens with
            // FileShare.Read only, which conflicts with that writer on Windows even though
            // the contents are otherwise readable. Use the same sharing contract as the
            // tail reader so an active conversation is not misclassified as a non-root
            // session merely because Codex is currently writing it.
            var firstLine = ReadFirstLineShared(path);
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

            var source = OptionalString(payload, "thread_source");
            return (!payload.TryGetProperty("parent_thread_id", out var parent) || parent.ValueKind == JsonValueKind.Null) &&
                !string.Equals(source, "subagent", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string? ReadFirstLineShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            leaveOpen: false);
        return reader.ReadLine();
    }

    private static IReadOnlyList<string> ReadTailLines(string path, int tailBudgetBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - tailBudgetBytes);
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
