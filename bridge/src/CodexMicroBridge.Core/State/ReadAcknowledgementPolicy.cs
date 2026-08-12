namespace CodexMicroBridge.Core.State;

public static class ReadAcknowledgementPolicy
{
    public static bool CoversLatest(string throughMessageId, string? latestMessageId) =>
        !string.IsNullOrWhiteSpace(latestMessageId) &&
        string.Equals(throughMessageId, latestMessageId, StringComparison.Ordinal);
}
