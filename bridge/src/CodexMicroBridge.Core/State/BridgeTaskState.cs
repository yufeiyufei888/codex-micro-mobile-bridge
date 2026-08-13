using System.Text.Json;

namespace CodexMicroBridge.Core.State;

public enum BridgeTaskState
{
    Unassigned,
    Idle,
    Running,
    NeedsApproval,
    NeedsReply,
    Completed,
    Failed,
    Interrupted,
    RecoveryUnknown,
}

public sealed record BridgePlanStep(string Text, string Status);

public sealed record BridgeTaskSnapshot
{
    public required string ThreadId { get; init; }

    public string Title { get; init; } = string.Empty;

    public string? ProjectId { get; init; }

    public string WorkingDirectory { get; init; } = string.Empty;

    public BridgeTaskState State { get; init; } = BridgeTaskState.Unassigned;

    public string? ActiveTurnId { get; init; }

    public string? LastTurnId { get; init; }

    public bool IsUnread { get; init; }

    public IReadOnlyList<BridgePlanStep> Plan { get; init; } = [];

    public string? LastError { get; init; }

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class BridgeStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, BridgeTaskSnapshot> _tasks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _turnThreads = new(StringComparer.Ordinal);

    public event Action<BridgeTaskSnapshot>? SnapshotChanged;

    public IReadOnlyList<BridgeTaskSnapshot> GetAll()
    {
        lock (_gate)
        {
            return _tasks.Values
                .OrderByDescending(task => task.UpdatedAt)
                .ToArray();
        }
    }

    public BridgeTaskSnapshot? Get(string threadId)
    {
        lock (_gate)
        {
            return _tasks.TryGetValue(threadId, out var snapshot) ? snapshot : null;
        }
    }

    public bool Remove(string threadId)
    {
        lock (_gate)
        {
            if (!_tasks.Remove(threadId))
            {
                return false;
            }

            foreach (var turnId in _turnThreads
                         .Where(pair => string.Equals(pair.Value, threadId, StringComparison.Ordinal))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _turnThreads.Remove(turnId);
            }

            return true;
        }
    }

    public BridgeTaskSnapshot Register(
        string threadId,
        string? title = null,
        string? workingDirectory = null,
        string? projectId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        BridgeTaskSnapshot changed;
        var notify = false;
        lock (_gate)
        {
            var current = GetOrCreateLocked(threadId);
            var nextTitle = string.IsNullOrWhiteSpace(title) ? current.Title : title;
            var nextWorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? current.WorkingDirectory
                : workingDirectory;
            var nextProjectId = projectId ?? current.ProjectId;
            if (string.Equals(nextTitle, current.Title, StringComparison.Ordinal) &&
                string.Equals(nextWorkingDirectory, current.WorkingDirectory, StringComparison.Ordinal) &&
                string.Equals(nextProjectId, current.ProjectId, StringComparison.Ordinal))
            {
                return current;
            }

            changed = current with
            {
                Title = nextTitle,
                WorkingDirectory = nextWorkingDirectory,
                ProjectId = nextProjectId,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            _tasks[threadId] = changed;
            notify = true;
        }

        if (notify)
        {
            SnapshotChanged?.Invoke(changed);
        }
        return changed;
    }

    public void Restore(IEnumerable<BridgeTaskSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        lock (_gate)
        {
            foreach (var snapshot in snapshots)
            {
                _tasks[snapshot.ThreadId] = snapshot;
                if (!string.IsNullOrWhiteSpace(snapshot.ActiveTurnId))
                {
                    _turnThreads[snapshot.ActiveTurnId] = snapshot.ThreadId;
                }
            }
        }
    }

    public BridgeTaskSnapshot? ApplyNotification(string method, JsonElement parameters)
    {
        var threadId = ReadString(parameters, "threadId")
            ?? ReadNestedString(parameters, "thread", "id");
        var turnId = ReadString(parameters, "turnId")
            ?? ReadNestedString(parameters, "turn", "id");

        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(turnId))
            {
                _turnThreads[turnId] = threadId;
            }
            else if (string.IsNullOrWhiteSpace(threadId) && !string.IsNullOrWhiteSpace(turnId))
            {
                _turnThreads.TryGetValue(turnId, out threadId);
            }
        }

        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        return method switch
        {
            "thread/started" => Mutate(threadId, current => current with { State = BridgeTaskState.Idle }),
            "thread/status/changed" => ApplyThreadStatus(threadId, parameters),
            "turn/started" => Mutate(threadId, current => current with
            {
                State = BridgeTaskState.Running,
                ActiveTurnId = turnId,
                LastTurnId = turnId ?? current.LastTurnId,
                IsUnread = false,
                LastError = null,
            }),
            "turn/completed" => ApplyTurnCompleted(threadId, turnId, parameters),
            "turn/plan/updated" => ApplyPlan(threadId, parameters),
            "serverRequest/resolved" => Mutate(threadId, current => current with
            {
                State = string.IsNullOrWhiteSpace(current.ActiveTurnId) ? BridgeTaskState.Idle : BridgeTaskState.Running,
            }),
            _ => null,
        };
    }

    public BridgeTaskSnapshot MarkNeedsInput(string threadId, string? turnId, bool isUserInput)
    {
        return Mutate(threadId, current => current with
        {
            State = isUserInput ? BridgeTaskState.NeedsReply : BridgeTaskState.NeedsApproval,
            ActiveTurnId = turnId ?? current.ActiveTurnId,
            LastTurnId = turnId ?? current.LastTurnId,
        });
    }

    public BridgeTaskSnapshot MarkRecoveryUnknown(string threadId, string reason)
    {
        return Mutate(threadId, current => current with
        {
            State = BridgeTaskState.RecoveryUnknown,
            LastError = reason,
        });
    }

    public BridgeTaskSnapshot ReconcileAuthoritative(
        string threadId,
        BridgeTaskState state,
        string? activeTurnId,
        string? lastTurnId,
        string? title = null,
        string? workingDirectory = null,
        string? lastError = null)
    {
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(activeTurnId))
            {
                _turnThreads[activeTurnId] = threadId;
            }

            if (!string.IsNullOrWhiteSpace(lastTurnId))
            {
                _turnThreads[lastTurnId] = threadId;
            }
        }

        return Mutate(threadId, current => current with
        {
            State = state,
            ActiveTurnId = activeTurnId,
            LastTurnId = lastTurnId ?? current.LastTurnId,
            Title = string.IsNullOrWhiteSpace(title) ? current.Title : title,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? current.WorkingDirectory : workingDirectory,
            LastError = lastError,
            IsUnread = state is BridgeTaskState.Completed or BridgeTaskState.Failed,
        });
    }

    public BridgeTaskSnapshot ReconcileDesktopLifecycle(
        string threadId,
        BridgeTaskState state,
        string turnId,
        string? title = null,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);
        lock (_gate)
        {
            _turnThreads[turnId] = threadId;
        }

        return Mutate(threadId, current =>
        {
            var sameTurn = string.Equals(current.LastTurnId, turnId, StringComparison.Ordinal);
            var preserveWaiting = state == BridgeTaskState.Running &&
                sameTurn &&
                current.State is BridgeTaskState.NeedsApproval or BridgeTaskState.NeedsReply;
            var terminal = state is BridgeTaskState.Completed or BridgeTaskState.Failed;
            var terminalWasRead = terminal && sameTurn && !current.IsUnread && current.State == BridgeTaskState.Idle;
            return current with
            {
                State = preserveWaiting
                    ? current.State
                    : terminalWasRead
                        ? BridgeTaskState.Idle
                        : state,
                ActiveTurnId = state == BridgeTaskState.Running ? turnId : null,
                LastTurnId = turnId,
                Title = string.IsNullOrWhiteSpace(title) ? current.Title : title,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                    ? current.WorkingDirectory
                    : workingDirectory,
                LastError = null,
                IsUnread = terminal
                    ? terminalWasRead ? false : !sameTurn || current.IsUnread || current.State != BridgeTaskState.Idle
                    : false,
            };
        });
    }

    public BridgeTaskSnapshot MarkRead(string threadId)
    {
        return Mutate(threadId, current => current with
        {
            IsUnread = false,
            State = current.State == BridgeTaskState.Completed ? BridgeTaskState.Idle : current.State,
        });
    }

    public BridgeTaskSnapshot MarkMessageUnread(string threadId)
    {
        return Mutate(threadId, current => current with { IsUnread = true });
    }

    private BridgeTaskSnapshot ApplyThreadStatus(string threadId, JsonElement parameters)
    {
        var statusElement = parameters.TryGetProperty("status", out var status) ? status : default;
        var statusName = statusElement.ValueKind switch
        {
            JsonValueKind.String => statusElement.GetString(),
            JsonValueKind.Object => ReadString(statusElement, "type"),
            _ => null,
        };
        var flags = default(JsonElement);
        var waiting = statusElement.ValueKind == JsonValueKind.Object &&
            statusElement.TryGetProperty("activeFlags", out flags) &&
            flags.ValueKind == JsonValueKind.Array &&
            flags.EnumerateArray().Any(flag =>
                flag.ValueKind == JsonValueKind.String &&
                flag.GetString() is { } text &&
                text.Contains("waiting", StringComparison.OrdinalIgnoreCase));

        return Mutate(threadId, current => current with
        {
            State = statusName switch
            {
                "active" when waiting => flags.EnumerateArray().Any(flag => flag.GetString() == "waitingOnUserInput")
                    ? BridgeTaskState.NeedsReply
                    : BridgeTaskState.NeedsApproval,
                "active" => BridgeTaskState.Running,
                "idle" when current.State is BridgeTaskState.Completed or BridgeTaskState.Failed or BridgeTaskState.Interrupted => current.State,
                "idle" when !string.IsNullOrWhiteSpace(current.ActiveTurnId) => current.State,
                "idle" => BridgeTaskState.Idle,
                "notLoaded" when !string.IsNullOrWhiteSpace(current.ActiveTurnId) => BridgeTaskState.RecoveryUnknown,
                "notLoaded" when current.State is BridgeTaskState.Completed or BridgeTaskState.Failed or BridgeTaskState.Interrupted => current.State,
                "notLoaded" => BridgeTaskState.Idle,
                "systemError" => BridgeTaskState.Failed,
                _ => BridgeTaskState.RecoveryUnknown,
            },
            LastError = statusName == "systemError" ? "Codex App Server reported a system error." : current.LastError,
        });
    }

    private BridgeTaskSnapshot ApplyTurnCompleted(string threadId, string? turnId, JsonElement parameters)
    {
        var status = ReadNestedString(parameters, "turn", "status") ?? ReadString(parameters, "status");
        var error = parameters.TryGetProperty("turn", out var turn) && turn.ValueKind == JsonValueKind.Object
            ? ReadNestedString(turn, "error", "message")
            : null;
        return Mutate(threadId, current => current with
        {
            State = status switch
            {
                "completed" => BridgeTaskState.Completed,
                "interrupted" => BridgeTaskState.Interrupted,
                "failed" => BridgeTaskState.Failed,
                _ => BridgeTaskState.RecoveryUnknown,
            },
            ActiveTurnId = null,
            LastTurnId = turnId ?? current.LastTurnId,
            IsUnread = status is "completed" or "failed",
            LastError = error,
        });
    }

    private BridgeTaskSnapshot ApplyPlan(string threadId, JsonElement parameters)
    {
        var steps = new List<BridgePlanStep>();
        if (parameters.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in plan.EnumerateArray())
            {
                var text = ReadString(item, "step") ?? ReadString(item, "text") ?? string.Empty;
                var status = ReadString(item, "status") ?? "pending";
                steps.Add(new BridgePlanStep(text, status));
            }
        }

        return Mutate(threadId, current => current with { Plan = steps });
    }

    private BridgeTaskSnapshot Mutate(string threadId, Func<BridgeTaskSnapshot, BridgeTaskSnapshot> mutation)
    {
        BridgeTaskSnapshot changed;
        lock (_gate)
        {
            var current = GetOrCreateLocked(threadId);
            changed = mutation(current) with { UpdatedAt = DateTimeOffset.UtcNow };
            _tasks[threadId] = changed;
        }

        SnapshotChanged?.Invoke(changed);
        return changed;
    }

    private BridgeTaskSnapshot GetOrCreateLocked(string threadId)
    {
        if (_tasks.TryGetValue(threadId, out var snapshot))
        {
            return snapshot;
        }

        snapshot = new BridgeTaskSnapshot { ThreadId = threadId, Title = threadId };
        _tasks[threadId] = snapshot;
        return snapshot;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(objectName, out var nested) &&
            nested.ValueKind == JsonValueKind.Object
                ? ReadString(nested, propertyName)
                : null;
    }
}
