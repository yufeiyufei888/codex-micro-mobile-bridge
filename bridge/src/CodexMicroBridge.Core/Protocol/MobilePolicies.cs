using CodexMicroBridge.Core.State;

namespace CodexMicroBridge.Core.Protocol;

public enum TurnRoute
{
    Start,
    Steer,
}

public sealed record TurnRoutingResult(TurnRoute? Route, string? ErrorCode, string? TurnId)
{
    public bool IsAccepted => Route is not null;
}

public static class TurnRoutingPolicy
{
    public static TurnRoutingResult Evaluate(
        BridgeTaskSnapshot snapshot,
        string? expectedTurnId,
        bool hasModelOrEffortOverride)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var active = snapshot.State is BridgeTaskState.Running or BridgeTaskState.NeedsApproval or BridgeTaskState.NeedsReply &&
            !string.IsNullOrWhiteSpace(snapshot.ActiveTurnId);
        if (!active)
        {
            return new TurnRoutingResult(TurnRoute.Start, null, null);
        }

        if (hasModelOrEffortOverride)
        {
            return new TurnRoutingResult(null, "ACTIVE_TURN_OVERRIDE_NOT_ALLOWED", snapshot.ActiveTurnId);
        }

        if (string.IsNullOrWhiteSpace(expectedTurnId) ||
            !string.Equals(expectedTurnId, snapshot.ActiveTurnId, StringComparison.Ordinal))
        {
            return new TurnRoutingResult(null, "STALE_TURN", snapshot.ActiveTurnId);
        }

        return new TurnRoutingResult(TurnRoute.Steer, null, snapshot.ActiveTurnId);
    }
}

public static class ApprovalBindingPolicy
{
    public static string? Validate(
        string expectedEpoch,
        long expectedSequence,
        string expectedThreadId,
        string expectedTurnId,
        string epoch,
        long sequence,
        string threadId,
        string turnId)
    {
        if (!string.Equals(epoch, expectedEpoch, StringComparison.Ordinal) || sequence != expectedSequence)
        {
            return "APPROVAL_STALE";
        }

        return string.Equals(threadId, expectedThreadId, StringComparison.Ordinal) &&
            string.Equals(turnId, expectedTurnId, StringComparison.Ordinal)
                ? null
                : "APPROVAL_BINDING_MISMATCH";
    }
}
