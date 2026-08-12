using CodexMicroBridge.Core.AppServer;
using CodexMicroBridge.MockAppServer;

namespace CodexMicroBridge.Tests;

public sealed class AppServerIntegrationTests
{
    [Fact]
    public async Task MockStdio_InitializesAndRunsStartSteerInterruptRoundTrip()
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
        {
            dotnetHost = Path.Combine(FindRepositoryRoot(), "work", "dotnet10", "dotnet.exe");
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new AppServerClient();
        client.NotificationReceived += notification =>
        {
            if (notification.Method == "turn/completed")
            {
                completed.TrySetResult();
            }

            return Task.CompletedTask;
        };
        await client.StartAndInitializeAsync(new AppServerStartOptions(
            dotnetHost,
            [typeof(MockAppServerMarker).Assembly.Location]));

        Assert.True(client.IsRunning);
        Assert.Equal("chatgpt", client.Account?.GetProperty("account").GetProperty("type").GetString());

        var threadResult = await client.SendRequestAsync("thread/start", new { cwd = Path.GetTempPath() });
        var threadId = threadResult.GetProperty("thread").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(threadId));

        var turnResult = await client.SendRequestAsync("turn/start", new
        {
            threadId,
            input = new[] { new { type = "text", text = "mock integration" } },
        });
        var turnId = turnResult.GetProperty("turn").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(turnId));

        var steerResult = await client.SendRequestAsync("turn/steer", new
        {
            threadId,
            expectedTurnId = turnId,
            input = new[] { new { type = "text", text = "continue" } },
        });
        Assert.Equal(turnId, steerResult.GetProperty("turnId").GetString());

        _ = await client.SendRequestAsync("turn/interrupt", new { threadId, turnId });
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotificationHandlerFailure_DoesNotStopLaterRpcResponses()
    {
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnetHost))
        {
            dotnetHost = Path.Combine(FindRepositoryRoot(), "work", "dotnet10", "dotnet.exe");
        }

        var diagnostic = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = new AppServerClient();
        client.DiagnosticReceived += message =>
        {
            if (message.Contains("notification handler failed", StringComparison.Ordinal))
            {
                diagnostic.TrySetResult();
            }
        };
        client.NotificationReceived += notification =>
            notification.Method == "thread/started"
                ? Task.FromException(new InvalidOperationException("synthetic handler failure"))
                : Task.CompletedTask;
        await client.StartAndInitializeAsync(new AppServerStartOptions(
            dotnetHost,
            [typeof(MockAppServerMarker).Assembly.Location]));

        var threadResult = await client.SendRequestAsync("thread/start", new { cwd = Path.GetTempPath() });
        var threadId = threadResult.GetProperty("thread").GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(threadId));
        await diagnostic.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var turnResult = await client.SendRequestAsync("turn/start", new
        {
            threadId,
            input = new[] { new { type = "text", text = "reader still alive" } },
        }).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("inProgress", turnResult.GetProperty("turn").GetProperty("status").GetString());
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "shared", "protocol-v1", "schema.json")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
