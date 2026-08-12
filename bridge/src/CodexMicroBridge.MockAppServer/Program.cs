using System.Text.Json;

namespace CodexMicroBridge.MockAppServer;

public sealed class MockAppServerMarker;

public static class MockAppServerProgram
{
    private static readonly Dictionary<string, MockThread> Threads = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string ThreadId, string TurnId)> ApprovalRequests = new(StringComparer.Ordinal);
    private static long _threadSequence;
    private static long _turnSequence;
    private static long _requestSequence = 10_000;

    public static async Task<int> Main()
    {
        string? line;
        while ((line = await Console.In.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("method", out var methodElement))
            {
                if (!root.TryGetProperty("id", out var id))
                {
                    continue;
                }

                var method = methodElement.GetString() ?? string.Empty;
                var parameters = root.TryGetProperty("params", out var paramsElement)
                    ? paramsElement
                    : default;
                await HandleRequestAsync(id, method, parameters).ConfigureAwait(false);
            }
            else if (root.TryGetProperty("id", out var responseId))
            {
                var key = responseId.GetRawText();
                if (ApprovalRequests.Remove(key, out var pending))
                {
                    await NotifyAsync("serverRequest/resolved", new
                    {
                        threadId = pending.ThreadId,
                        turnId = pending.TurnId,
                        requestId = responseId.Clone(),
                    }).ConfigureAwait(false);
                }
            }
        }

        return 0;
    }

    private static async Task HandleRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        switch (method)
        {
            case "initialize":
                await RespondAsync(id, new { protocolVersion = 1, userAgent = "codex-micro-mock/1.0" }).ConfigureAwait(false);
                break;
            case "account/read":
                await RespondAsync(id, new
                {
                    account = new { type = "chatgpt", email = "mock@example.invalid" },
                    requiresOpenaiAuth = false,
                }).ConfigureAwait(false);
                break;
            case "thread/list":
                await RespondAsync(id, new
                {
                    data = Threads.Values.Select(thread => new
                    {
                        id = thread.Id,
                        cwd = thread.Cwd,
                        status = new { type = thread.ActiveTurnId is null ? "idle" : "active", activeFlags = Array.Empty<string>() },
                    }),
                    nextCursor = (string?)null,
                }).ConfigureAwait(false);
                break;
            case "thread/start":
                await StartThreadAsync(id, parameters).ConfigureAwait(false);
                break;
            case "thread/resume":
            case "thread/read":
                await ReadThreadAsync(id, parameters).ConfigureAwait(false);
                break;
            case "turn/start":
                await StartTurnAsync(id, parameters).ConfigureAwait(false);
                break;
            case "turn/steer":
                await RespondAsync(id, new { turnId = ReadString(parameters, "expectedTurnId") }).ConfigureAwait(false);
                break;
            case "turn/interrupt":
                await InterruptTurnAsync(id, parameters).ConfigureAwait(false);
                break;
            default:
                await RejectAsync(id, -32601, $"Mock method '{method}' is not implemented.").ConfigureAwait(false);
                break;
        }
    }

    private static async Task StartThreadAsync(JsonElement id, JsonElement parameters)
    {
        var threadId = $"mock-thread-{Interlocked.Increment(ref _threadSequence)}";
        var thread = new MockThread(threadId, ReadString(parameters, "cwd") ?? Environment.CurrentDirectory, null);
        Threads[threadId] = thread;
        await RespondAsync(id, new { thread = new { id = thread.Id, cwd = thread.Cwd } }).ConfigureAwait(false);
        await NotifyAsync("thread/started", new { thread = new { id = thread.Id, cwd = thread.Cwd } }).ConfigureAwait(false);
        await NotifyAsync("thread/status/changed", new
        {
            threadId,
            status = new { type = "idle", activeFlags = Array.Empty<string>() },
        }).ConfigureAwait(false);
    }

    private static Task ReadThreadAsync(JsonElement id, JsonElement parameters)
    {
        var threadId = ReadString(parameters, "threadId") ?? string.Empty;
        if (!Threads.TryGetValue(threadId, out var thread))
        {
            return RejectAsync(id, -32004, "Mock thread not found.");
        }

        return RespondAsync(id, new
        {
            thread = new
            {
                id = thread.Id,
                cwd = thread.Cwd,
                turns = Array.Empty<object>(),
            },
        });
    }

    private static async Task StartTurnAsync(JsonElement id, JsonElement parameters)
    {
        var threadId = ReadString(parameters, "threadId") ?? string.Empty;
        if (!Threads.TryGetValue(threadId, out var thread))
        {
            await RejectAsync(id, -32004, "Mock thread not found.").ConfigureAwait(false);
            return;
        }

        var turnId = $"mock-turn-{Interlocked.Increment(ref _turnSequence)}";
        Threads[threadId] = thread with { ActiveTurnId = turnId };
        await RespondAsync(id, new { turn = new { id = turnId, status = "inProgress", items = Array.Empty<object>() } }).ConfigureAwait(false);
        await NotifyAsync("turn/started", new
        {
            threadId,
            turn = new { id = turnId, status = "inProgress", items = Array.Empty<object>() },
        }).ConfigureAwait(false);
        await NotifyAsync("thread/status/changed", new
        {
            threadId,
            status = new { type = "active", activeFlags = Array.Empty<string>() },
        }).ConfigureAwait(false);
        await NotifyAsync("turn/plan/updated", new
        {
            threadId,
            turnId,
            plan = new[]
            {
                new { step = "Inspect request", status = "completed" },
                new { step = "Apply change", status = "inProgress" },
                new { step = "Verify result", status = "pending" },
            },
        }).ConfigureAwait(false);

        if (ReadInputText(parameters).Contains("[approval]", StringComparison.OrdinalIgnoreCase))
        {
            var requestId = Interlocked.Increment(ref _requestSequence);
            ApprovalRequests[requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = (threadId, turnId);
            await WriteAsync(new
            {
                id = requestId,
                method = "item/commandExecution/requestApproval",
                @params = new
                {
                    threadId,
                    turnId,
                    itemId = "mock-command-1",
                    command = "echo mock",
                    reason = "Mock approval requested by the test prompt.",
                },
            }).ConfigureAwait(false);
        }
    }

    private static async Task InterruptTurnAsync(JsonElement id, JsonElement parameters)
    {
        var threadId = ReadString(parameters, "threadId") ?? string.Empty;
        var turnId = ReadString(parameters, "turnId") ?? string.Empty;
        if (Threads.TryGetValue(threadId, out var thread))
        {
            Threads[threadId] = thread with { ActiveTurnId = null };
        }

        await RespondAsync(id, new { }).ConfigureAwait(false);
        await NotifyAsync("turn/completed", new
        {
            threadId,
            turn = new { id = turnId, status = "interrupted", items = Array.Empty<object>() },
        }).ConfigureAwait(false);
        await NotifyAsync("thread/status/changed", new
        {
            threadId,
            status = new { type = "idle", activeFlags = Array.Empty<string>() },
        }).ConfigureAwait(false);
    }

    private static string ReadInputText(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("input", out var input) ||
            input.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(' ', input.EnumerateArray()
            .Select(item => ReadString(item, "text"))
            .Where(text => text is not null));
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static Task RespondAsync(JsonElement id, object result) =>
        WriteAsync(new { id = id.Clone(), result });

    private static Task RejectAsync(JsonElement id, int code, string message) =>
        WriteAsync(new { id = id.Clone(), error = new { code, message } });

    private static Task NotifyAsync(string method, object parameters) =>
        WriteAsync(new { method, @params = parameters });

    private static async Task WriteAsync(object envelope)
    {
        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(envelope)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
    }

    private sealed record MockThread(string Id, string Cwd, string? ActiveTurnId);
}
