namespace CodexMicroBridge.App;

internal static class MobileEnvelopeLimits
{
    public const int MaximumBytes = 1024 * 1024;

    public static void EnsureWithinLimit(long byteCount)
    {
        if (byteCount > MaximumBytes)
        {
            throw new MobileEnvelopeSizeException(byteCount, MaximumBytes);
        }
    }
}

internal sealed class MobileEnvelopeSizeException(long actualBytes, long maximumBytes)
    : Exception($"Mobile WSS envelope is {actualBytes} bytes; the maximum is {maximumBytes} bytes.")
{
    public long ActualBytes { get; } = actualBytes;

    public long MaximumBytes { get; } = maximumBytes;
}
