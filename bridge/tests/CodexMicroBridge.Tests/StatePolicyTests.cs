using System.Net;
using System.Text.Json;
using CodexMicroBridge.Core.AppServer;
using CodexMicroBridge.Core.Protocol;
using CodexMicroBridge.Core.Security;
using CodexMicroBridge.Core.State;

namespace CodexMicroBridge.Tests;

public sealed class StatePolicyTests
{
    [Fact]
    public void StaleTurn_IsRejectedWithoutFallingBackToStart()
    {
        var snapshot = new BridgeTaskSnapshot
        {
            ThreadId = "thread-one",
            State = BridgeTaskState.Running,
            ActiveTurnId = "turn-current",
        };

        var missing = TurnRoutingPolicy.Evaluate(snapshot, null, false);
        var stale = TurnRoutingPolicy.Evaluate(snapshot, "turn-old", false);
        Assert.False(missing.IsAccepted);
        Assert.Equal("STALE_TURN", missing.ErrorCode);
        Assert.Null(missing.Route);
        Assert.False(stale.IsAccepted);
        Assert.Equal("STALE_TURN", stale.ErrorCode);
        Assert.Null(stale.Route);
    }

    [Fact]
    public void ApprovalBinding_DetectsStaleAndMismatchedResponses()
    {
        Assert.Null(ApprovalBindingPolicy.Validate(
            "epoch-1234567890", 7, "thread-one", "turn-one",
            "epoch-1234567890", 7, "thread-one", "turn-one"));
        Assert.Equal("APPROVAL_STALE", ApprovalBindingPolicy.Validate(
            "epoch-1234567890", 7, "thread-one", "turn-one",
            "epoch-1234567890", 8, "thread-one", "turn-one"));
        Assert.Equal("APPROVAL_BINDING_MISMATCH", ApprovalBindingPolicy.Validate(
            "epoch-1234567890", 7, "thread-one", "turn-one",
            "epoch-1234567890", 7, "thread-two", "turn-one"));
    }

    [Fact]
    public void ResolvedApprovalLeavesAttentionStateImmediately()
    {
        var state = new BridgeStateStore();
        state.Register("thread-one", "Test task");
        state.MarkNeedsInput("thread-one", "turn-one", isUserInput: false);

        state.ApplyNotification("serverRequest/resolved", Element(new
        {
            threadId = "thread-one",
            turnId = "turn-one",
        }));

        var snapshot = Assert.IsType<BridgeTaskSnapshot>(state.Get("thread-one"));
        Assert.Equal(BridgeTaskState.Running, snapshot.State);
        Assert.NotEqual(BridgeTaskState.NeedsApproval, snapshot.State);
    }

    [Fact]
    public void StateReducer_PreservesInterrupted_AndComputesPlanProgressInputs()
    {
        var state = new BridgeStateStore();
        state.Register("thread-one", "Test task");
        state.ApplyNotification("turn/started", Element(new
        {
            threadId = "thread-one",
            turn = new { id = "turn-one" },
        }));
        state.ApplyNotification("turn/plan/updated", Element(new
        {
            threadId = "thread-one",
            turnId = "turn-one",
            plan = new[]
            {
                new { step = "First", status = "completed" },
                new { step = "Second", status = "inProgress" },
            },
        }));
        state.ApplyNotification("turn/completed", Element(new
        {
            threadId = "thread-one",
            turn = new { id = "turn-one", status = "interrupted" },
        }));
        state.ApplyNotification("thread/status/changed", Element(new
        {
            threadId = "thread-one",
            status = new { type = "idle" },
        }));

        var snapshot = Assert.IsType<BridgeTaskSnapshot>(state.Get("thread-one"));
        Assert.Equal(BridgeTaskState.Interrupted, snapshot.State);
        Assert.Equal(2, snapshot.Plan.Count);
        Assert.Single(snapshot.Plan, step => step.Status == "completed");
    }

    [Fact]
    public void AuthoritativeCompletion_BecomesUnreadUntilAcknowledged()
    {
        var state = new BridgeStateStore();
        state.Register("thread-one", "Test task");
        state.ApplyNotification("turn/started", Element(new
        {
            threadId = "thread-one",
            turn = new { id = "turn-one" },
        }));

        var completed = state.ReconcileAuthoritative(
            "thread-one",
            BridgeTaskState.Completed,
            activeTurnId: null,
            lastTurnId: "turn-one");
        Assert.True(completed.IsUnread);

        var acknowledged = state.MarkRead("thread-one");
        Assert.False(acknowledged.IsUnread);
        Assert.Equal(BridgeTaskState.Idle, acknowledged.State);
    }

    [Fact]
    public void VersionGate_FailsClosedUnlessExplicitlyOverridden()
    {
        CodexCliVersionVerifier.ValidateOutput($"codex-cli {CodexCliVersionVerifier.PinnedVersion}");
        Assert.Throws<InvalidOperationException>(() => CodexCliVersionVerifier.ValidateOutput("codex-cli 0.0.0"));
        CodexCliVersionVerifier.ValidateOutput("codex-cli dev-mock", allowUnverifiedVersion: true);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.2.3.4", true)]
    [InlineData("::ffff:192.168.2.5", true)]
    [InlineData("fd12::1", true)]
    [InlineData("fe80::1", true)]
    public void PrivateNetworkPolicy_FailsClosedAndAcceptsOnlyLocalRanges(string? text, bool expected)
    {
        var address = text is null ? null : IPAddress.Parse(text);
        Assert.Equal(expected, PrivateNetworkPolicy.IsAllowedRemote(address));
    }

    [Fact]
    public void IdleThreadStatus_DoesNotEraseAnActiveTurn()
    {
        var state = new BridgeStateStore();
        state.Register("thread-one", "Test task");
        state.ApplyNotification("turn/started", Element(new
        {
            threadId = "thread-one",
            turn = new { id = "turn-one", status = "inProgress" },
        }));

        state.ApplyNotification("thread/status/changed", Element(new
        {
            threadId = "thread-one",
            status = new { type = "idle" },
        }));

        var snapshot = Assert.IsType<BridgeTaskSnapshot>(state.Get("thread-one"));
        Assert.Equal(BridgeTaskState.Running, snapshot.State);
        Assert.Equal("turn-one", snapshot.ActiveTurnId);
    }

    [Fact]
    public void NotLoadedThreadStatus_DoesNotEraseTerminalEvidence()
    {
        var state = new BridgeStateStore();
        state.Register("thread-one", "Test task");
        state.ApplyNotification("turn/completed", Element(new
        {
            threadId = "thread-one",
            turn = new { id = "turn-one", status = "completed" },
        }));

        state.ApplyNotification("thread/status/changed", Element(new
        {
            threadId = "thread-one",
            status = new { type = "notLoaded" },
        }));

        var snapshot = Assert.IsType<BridgeTaskSnapshot>(state.Get("thread-one"));
        Assert.Equal(BridgeTaskState.Completed, snapshot.State);
        Assert.True(snapshot.IsUnread);
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);
}
