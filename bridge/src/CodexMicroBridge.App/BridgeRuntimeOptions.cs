namespace CodexMicroBridge.App;

public sealed record BridgeRuntimeOptions
{
    public string? DataDirectory { get; init; }

    public string? CodexExecutablePath { get; init; }

    public bool AllowUnverifiedCodexVersion { get; init; }

    public int Port { get; init; } = BridgeRuntime.DefaultPort;
}
