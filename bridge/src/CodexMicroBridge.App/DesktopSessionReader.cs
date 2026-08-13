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

internal enum DesktopSessionLifecycleKind
{
    Running,
    Completed,
    Interrupted,
}

internal sealed record DesktopSessionLifecycle(
    DesktopSessionLifecycleKind Kind,
    string TurnId,
    DateTimeOffset Timestamp);

internal sealed record DesktopSessionFileStamp(
    long Length,
    DateTime LastWriteTimeUtc);

internal sealed record DesktopSessionDescriptor(
    string Path,
    string SessionId,
    string ThreadId,
    string Title,
    string WorkingDirectory,
    DesktopSessionFileStamp Stamp);

internal sealed class DesktopSessionReader
{
    private const int TailBudgetBytes = 8 * 1024 * 1024;
    private const int LiveTailBudgetBytes = 2 * 1024 * 1024;
    private const int SessionCandidateLimit = 256;
    private readonly string _sessionsDirectory;
    private readonly string _sessionIndexPath;
    private readonly Dictionary<string, DesktopSessionDescriptor> _descriptorCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _indexedTitles = new(StringComparer.Ordinal);
    private DesktopSessionFileStamp? _sessionIndexStamp;

    public DesktopSessionReader(string? sessionsDirectory = null, string? sessionIndexPath = null)
    {
        var codexDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
        _sessionsDirectory = sessionsDirectory ?? Path.Combine(codexDirectory, "sessions");
        _sessionIndexPath = sessionIndexPath ?? Path.Combine(codexDirectory, "session_index.jsonl");
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

    public IReadOnlyList<DesktopSessionDescriptor> FindRecentRootSessions(
        DateTimeOffset notBefore,
        int maximumCount = 6)
    {
        if (maximumCount <= 0 || !Directory.Exists(_sessionsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(_sessionsDirectory, "*.jsonl", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => file.LastWriteTimeUtc >= notBefore.UtcDateTime)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(SessionCandidateLimit)
            .Select(file => TryReadDescriptor(file.FullName))
            .Where(descriptor => descriptor is not null)
            .Select(descriptor => descriptor!)
            .Take(maximumCount)
            .ToArray();
    }

    public DesktopSessionDescriptor? ReadDescriptor(string path) => TryReadDescriptor(path);

    public DesktopSessionDescriptor? FindByVisibleTitle(
        string? visibleTitle,
        DateTimeOffset notBefore,
        int maximumCount = 32)
    {
        var sessions = FindRecentRootSessions(notBefore, maximumCount);
        return MatchVisibleTitle(visibleTitle, sessions);
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

    public DesktopSessionLifecycle? ReadLatestLifecycle(string path) =>
        ParseLatestLifecycle(ReadTailLines(path, TailBudgetBytes));

    public DesktopSessionLifecycle? ReadLatestLifecycleSince(string path, long previousLength)
    {
        var lines = ReadLinesFromOffset(path, previousLength);
        return lines is null ? null : ParseLatestLifecycle(lines);
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

    private static DesktopSessionLifecycle? ParseLatestLifecycle(IEnumerable<string> lines)
    {
        DesktopSessionLifecycle? latest = null;
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (OptionalString(root, "type") != "event_msg" ||
                    !root.TryGetProperty("payload", out var payload))
                {
                    continue;
                }

                var kind = OptionalString(payload, "type") switch
                {
                    "task_started" => DesktopSessionLifecycleKind.Running,
                    "task_complete" => DesktopSessionLifecycleKind.Completed,
                    "turn_aborted" => DesktopSessionLifecycleKind.Interrupted,
                    _ => (DesktopSessionLifecycleKind?)null,
                };
                var turnId = OptionalString(payload, "turn_id") ?? OptionalString(payload, "turnId");
                if (kind is null || string.IsNullOrWhiteSpace(turnId))
                {
                    continue;
                }

                var candidate = new DesktopSessionLifecycle(kind.Value, turnId, ReadTimestamp(root));
                if (latest is null || candidate.Timestamp >= latest.Timestamp)
                {
                    latest = candidate;
                }
            }
            catch (JsonException)
            {
                // The active rollout is append-only; retry an incomplete tail record later.
            }
        }

        return latest;
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

    private DesktopSessionDescriptor? TryReadDescriptor(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                _descriptorCache.Remove(path);
                return null;
            }
            if (_descriptorCache.TryGetValue(path, out var cached))
            {
                var cachedIndexedTitle = ReadIndexedTitle(cached.SessionId);
                var refreshed = cached with
                {
                    Title = cachedIndexedTitle ?? cached.Title,
                    Stamp = new DesktopSessionFileStamp(file.Length, file.LastWriteTimeUtc),
                };
                if (cached.Stamp.Length == file.Length ||
                    cachedIndexedTitle is not null ||
                    !cached.Title.StartsWith("Codex 对话 · ", StringComparison.Ordinal))
                {
                    _descriptorCache[path] = refreshed;
                    return refreshed;
                }
            }

            var firstLine = ReadFirstLineShared(path);
            if (firstLine is null)
            {
                return null;
            }

            using var document = JsonDocument.Parse(firstLine);
            var root = document.RootElement;
            if (OptionalString(root, "type") != "session_meta" ||
                !root.TryGetProperty("payload", out var payload))
            {
                return null;
            }

            var source = OptionalString(payload, "thread_source");
            if ((payload.TryGetProperty("parent_thread_id", out var parent) && parent.ValueKind != JsonValueKind.Null) ||
                string.Equals(source, "subagent", StringComparison.Ordinal))
            {
                return null;
            }

            var sessionId = OptionalString(payload, "id")?.Trim();
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                sessionId = Path.GetFileNameWithoutExtension(path);
            }

            var cwd = OptionalString(payload, "cwd") ?? string.Empty;
            var indexedTitle = ReadIndexedTitle(sessionId);
            var firstUserMessage = indexedTitle is null
                ? ReadConversationMessages(path, TailBudgetBytes, includeEventMirrors: false)
                    .FirstOrDefault(message => message.Role == "user")?.Text
                : null;
            var title = indexedTitle ?? CreateConversationTitle(firstUserMessage, file.LastWriteTimeUtc);
            var descriptor = new DesktopSessionDescriptor(
                path,
                sessionId,
                CreateThreadId(sessionId),
                title,
                cwd,
                new DesktopSessionFileStamp(file.Length, file.LastWriteTimeUtc));
            _descriptorCache[path] = descriptor;
            return descriptor;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal static string CreateThreadId(string sessionId)
    {
        var safe = new string(sessionId.Where(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        if (safe.Length is > 0 and <= 108)
        {
            return $"desktop-{safe}";
        }

        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sessionId));
        return $"desktop-{Convert.ToHexString(digest)[..32].ToLowerInvariant()}";
    }

    internal static string CreateConversationTitle(string? firstUserMessage, DateTime lastWriteTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return $"Codex 对话 · {lastWriteTimeUtc.ToLocalTime():MM-dd HH:mm}";
        }

        var compact = firstUserMessage
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        compact = System.Text.RegularExpressions.Regex.Replace(compact, "<[^>]+>", " ");
        compact = System.Text.RegularExpressions.Regex.Replace(compact, "\\s+", " ").Trim();
        return compact.Length <= 48 ? compact : $"{compact[..48]}…";
    }

    internal static DesktopSessionDescriptor? MatchVisibleTitle(
        string? visibleTitle,
        IEnumerable<DesktopSessionDescriptor> sessions)
    {
        var visible = NormalizeConversationTitle(visibleTitle);
        if (visible.Length == 0)
        {
            return null;
        }

        var candidates = sessions
            .Select(session => (Session: session, Title: NormalizeConversationTitle(session.Title)))
            .Where(candidate => candidate.Title.Length > 0)
            .ToArray();
        var exact = candidates
            .Where(candidate => string.Equals(candidate.Title, visible, StringComparison.OrdinalIgnoreCase))
            .Select(candidate => candidate.Session)
            .ToArray();
        if (exact.Length == 1)
        {
            return exact[0];
        }

        if (visible.Length < 8)
        {
            return null;
        }

        var unambiguousPrefix = candidates
            .Where(candidate => candidate.Title.Length >= 8 &&
                (candidate.Title.StartsWith(visible, StringComparison.OrdinalIgnoreCase) ||
                 visible.StartsWith(candidate.Title, StringComparison.OrdinalIgnoreCase)))
            .Select(candidate => candidate.Session)
            .DistinctBy(session => session.ThreadId, StringComparer.Ordinal)
            .ToArray();
        return unambiguousPrefix.Length == 1 ? unambiguousPrefix[0] : null;
    }

    internal static string NormalizeConversationTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var normalized = title.Normalize(NormalizationForm.FormKC)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, "\\s+", " ").Trim();
        return normalized.TrimEnd('.', '…').Trim();
    }

    private string? ReadIndexedTitle(string sessionId)
    {
        RefreshIndexedTitles();
        return _indexedTitles.TryGetValue(sessionId, out var title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : null;
    }

    private void RefreshIndexedTitles()
    {
        try
        {
            var file = new FileInfo(_sessionIndexPath);
            if (!file.Exists)
            {
                _sessionIndexStamp = null;
                _indexedTitles.Clear();
                return;
            }

            var stamp = new DesktopSessionFileStamp(file.Length, file.LastWriteTimeUtc);
            if (_sessionIndexStamp == stamp)
            {
                return;
            }

            var refreshed = new Dictionary<string, string>(StringComparer.Ordinal);
            using var stream = new FileStream(
                _sessionIndexPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (reader.ReadLine() is { } line)
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var id = OptionalString(root, "id")?.Trim();
                    var title = OptionalString(root, "thread_name")?.Trim();
                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
                    {
                        // The index is append-only; the last record for an id is its
                        // current user-visible desktop title.
                        refreshed[id] = title;
                    }
                }
                catch (JsonException)
                {
                    // Ignore a partially written tail record and retry on the next stamp.
                }
            }

            _indexedTitles.Clear();
            foreach (var entry in refreshed)
            {
                _indexedTitles[entry.Key] = entry.Value;
            }
            _sessionIndexStamp = stamp;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Keep the last known index snapshot while Codex is replacing the file.
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
