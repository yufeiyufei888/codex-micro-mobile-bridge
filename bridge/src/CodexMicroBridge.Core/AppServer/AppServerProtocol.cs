using System.Text.Json;

namespace CodexMicroBridge.Core.AppServer;

public sealed record AppServerStartOptions(
    string ExecutablePath,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null)
{
    public IReadOnlyList<string> EffectiveArguments =>
        Arguments ?? ["app-server", "--listen", "stdio://"];
}

public sealed record AppServerNotification(string Method, JsonElement Parameters);

public sealed record AppServerRequest(JsonElement Id, string Method, JsonElement Parameters);

public sealed class AppServerRpcException : Exception
{
    public AppServerRpcException(string method, JsonElement error)
        : base($"App Server method '{method}' failed: {error.GetRawText()}")
    {
        Method = method;
        Error = error.Clone();
    }

    public string Method { get; }

    public JsonElement Error { get; }
}
