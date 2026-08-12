using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace CodexMicroBridge.Core.AppServer;

public sealed class AppServerClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private Process? _process;
    private StreamWriter? _stdin;
    private WindowsJobObject? _job;
    private Task? _stdoutTask;
    private Task? _stderrTask;
    private long _nextRequestId;
    private int _disposed;

    public event Func<AppServerNotification, Task>? NotificationReceived;

    public event Func<AppServerRequest, Task>? ServerRequestReceived;

    public event Action<string>? DiagnosticReceived;

    public event Action<int?>? Exited;

    public JsonElement? Account { get; private set; }

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAndInitializeAsync(AppServerStartOptions options, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_process is not null)
        {
            throw new InvalidOperationException("App Server has already been started.");
        }

        var startInfo = new ProcessStartInfo(options.ExecutablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in options.EffectiveArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("The Codex App Server process did not start.");
        }

        try
        {
            _job = WindowsJobObject.Attach(process);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            process.Dispose();
            throw;
        }

        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        process.Exited += OnProcessExited;
        _stdoutTask = Task.Run(() => ReadStdoutAsync(process.StandardOutput, _shutdown.Token), CancellationToken.None);
        _stderrTask = Task.Run(() => ReadStderrAsync(process.StandardError, _shutdown.Token), CancellationToken.None);

        try
        {
            await SendRequestAsync(
                "initialize",
                new
                {
                    clientInfo = new
                    {
                        name = "codex_micro_bridge",
                        title = "Codex Micro Bridge",
                        version = typeof(AppServerClient).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                    },
                },
                cancellationToken).ConfigureAwait(false);
            await SendNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
            Account = await SendRequestAsync("account/read", new { refreshToken = false }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var id = Interlocked.Increment(ref _nextRequestId);
        var pending = new PendingRequest(method);
        if (!_pending.TryAdd(id, pending))
        {
            throw new InvalidOperationException("Could not allocate an App Server request ID.");
        }

        try
        {
            await WriteEnvelopeAsync(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new { },
            }, cancellationToken).ConfigureAwait(false);

            using var registration = cancellationToken.Register(() =>
            {
                if (_pending.TryRemove(id, out var removed))
                {
                    removed.Completion.TrySetCanceled(cancellationToken);
                }
            });
            return await pending.Completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    public Task SendNotificationAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return WriteEnvelopeAsync(new Dictionary<string, object?>
        {
            ["method"] = method,
            ["params"] = parameters ?? new { },
        }, cancellationToken);
    }

    public Task RespondToServerRequestAsync(
        JsonElement id,
        object? result,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return WriteEnvelopeAsync(new Dictionary<string, object?>
        {
            ["id"] = id.Clone(),
            ["result"] = result ?? new { },
        }, cancellationToken);
    }

    public Task RejectServerRequestAsync(
        JsonElement id,
        int code,
        string message,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return WriteEnvelopeAsync(new Dictionary<string, object?>
        {
            ["id"] = id.Clone(),
            ["error"] = new { code, message },
        }, cancellationToken);
    }

    private async Task WriteEnvelopeAsync(object envelope, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(envelope);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await (_stdin ?? throw new InvalidOperationException("App Server stdin is unavailable."))
                .WriteLineAsync(line.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadStdoutAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    await DispatchAsync(document.RootElement).ConfigureAwait(false);
                }
                catch (JsonException exception)
                {
                    DiagnosticReceived?.Invoke($"Invalid App Server JSONL: {exception.Message}");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticReceived?.Invoke($"App Server stdout reader failed: {exception.Message}");
            FailPending(exception);
        }
    }

    private async Task DispatchAsync(JsonElement root)
    {
        if (root.TryGetProperty("id", out var id) &&
            (root.TryGetProperty("result", out var result) || root.TryGetProperty("error", out _)))
        {
            if (id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var numericId) &&
                _pending.TryRemove(numericId, out var pending))
            {
                if (root.TryGetProperty("error", out var error))
                {
                    pending.Completion.TrySetException(new AppServerRpcException(pending.Method, error));
                }
                else
                {
                    pending.Completion.TrySetResult(result.Clone());
                }
            }

            return;
        }

        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            DiagnosticReceived?.Invoke($"Ignored App Server envelope: {root.GetRawText()}");
            return;
        }

        var method = methodElement.GetString() ?? string.Empty;
        var parameters = root.TryGetProperty("params", out var paramsElement)
            ? paramsElement.Clone()
            : JsonSerializer.SerializeToElement(new { });

        if (root.TryGetProperty("id", out id))
        {
            await InvokeAsync(ServerRequestReceived, new AppServerRequest(id.Clone(), method, parameters)).ConfigureAwait(false);
        }
        else
        {
            try
            {
                await InvokeAsync(NotificationReceived, new AppServerNotification(method, parameters)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                DiagnosticReceived?.Invoke(
                    $"App Server notification handler failed for '{method}' ({exception.GetType().Name}); continuing with later notifications.");
            }
        }
    }

    private async Task ReadStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                DiagnosticReceived?.Invoke(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task InvokeAsync<T>(Func<T, Task>? handlers, T message)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Func<T, Task>>())
        {
            await handler(message).ConfigureAwait(false);
        }
    }

    private void OnProcessExited(object? sender, EventArgs eventArgs)
    {
        int? exitCode = _process is { HasExited: true } process ? process.ExitCode : null;
        var exception = new EndOfStreamException($"Codex App Server exited with code {exitCode?.ToString() ?? "unknown"}.");
        FailPending(exception);
        Exited?.Invoke(exitCode);
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_stdin is null || _process is not { HasExited: false })
        {
            throw new InvalidOperationException("Codex App Server is not connected.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        if (_stdin is not null)
        {
            await _stdin.DisposeAsync().ConfigureAwait(false);
        }

        _job?.Dispose();
        if (_process is { HasExited: false } process)
        {
            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        var readers = new[] { _stdoutTask, _stderrTask }.Where(task => task is not null).Cast<Task>();
        try
        {
            await Task.WhenAll(readers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _process?.Dispose();
        _writeLock.Dispose();
        _shutdown.Dispose();
        FailPending(new ObjectDisposedException(nameof(AppServerClient)));
    }

    private sealed record PendingRequest(string Method)
    {
        public TaskCompletionSource<JsonElement> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
