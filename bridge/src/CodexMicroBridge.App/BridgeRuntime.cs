using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CodexMicroBridge.Core.AppServer;
using CodexMicroBridge.Core.Persistence;
using CodexMicroBridge.Core.Projects;
using CodexMicroBridge.Core.Protocol;
using CodexMicroBridge.Core.Security;
using CodexMicroBridge.Core.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CodexMicroBridge.App;

public sealed class BridgeRuntime : IAsyncDisposable
{
    public const int DefaultPort = 47127;
    internal const string DesktopThreadId = "desktop-current";
    private const int TaskReadMessageBudgetBytes = 512 * 1024;
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly HashSet<string> SupportedServerRequests =
    [
        "item/commandExecution/requestApproval",
        "item/fileChange/requestApproval",
        "item/permissions/requestApproval",
        "item/tool/requestUserInput",
    ];

    private readonly object _diagnosticGate = new();
    private readonly List<string> _diagnostics = [];
    private readonly ConcurrentDictionary<Guid, ClientConnection> _clients = new();
    private readonly ConcurrentDictionary<string, PendingApproval> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _approvalIdsByRequest = new(StringComparer.Ordinal);
    private readonly object _catalogGate = new();
    private IReadOnlyList<ModelCatalogItem> _modelCatalog = [];
    private readonly string _dataDirectory;
    private readonly BridgeRuntimeOptions _options;
    private readonly IPAddress _listenAddress;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly SemaphoreSlim _eventGate = new(1, 1);
    private readonly SemaphoreSlim _taskCapacityGate = new(1, 1);
    private readonly SemaphoreSlim _messageGate = new(1, 1);
    private readonly Channel<BridgeTaskSnapshot> _stateChanges = Channel.CreateUnbounded<BridgeTaskSnapshot>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly DesktopCodexAutomation _desktopAutomation = new();
    private readonly DesktopSessionReader _desktopSessions = new();
    private BridgeRepository? _repository;
    private IdempotentCommandExecutor? _commands;
    private ProjectAllowList? _projects;
    private PairingService? _pairing;
    private BridgeCertificate? _certificate;
    private AppServerClient? _appServer;
    private BridgeStateStore? _state;
    private WebApplication? _webApplication;
    private MdnsAdvertisement? _mdns;
    private volatile bool _desktopAvailable;
    private volatile string? _desktopStatusReason = "正在检查 Codex 桌面窗口。";
    private bool _desktopWasRunning;
    private string? _desktopSessionPath;
    private string? _expectedDesktopPrompt;
    private DateTimeOffset _desktopPromptSentAt;
    private string? _desktopApprovalFingerprint;
    private string? _desktopApprovalId;
    private string? _desktopTurnId;
    private long _eventSequence;
    private int _disposed;

    public BridgeRuntime(BridgeRuntimeOptions? options = null)
    {
        _options = options ?? new BridgeRuntimeOptions();
        if (_options.Port is < 1024 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Bridge port must be between 1024 and 65535.");
        }

        _dataDirectory = _options.DataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexMicroBridge");
        _listenAddress = PrivateNetworkPolicy.SelectAdvertisedAddress();
    }

    public event Action? RuntimeChanged;

    public string Epoch { get; private set; } = Guid.NewGuid().ToString("N");

    public string CertificateFingerprint => _certificate?.CertificateSha256Fingerprint ?? "Not initialized";

    public string CertificateSpkiSha256 => _certificate?.SpkiSha256Fingerprint ?? "Not initialized";

    public string HostId { get; private set; } = "Not initialized";

    public string WssUrl { get; private set; } = "Not initialized";

    public bool IsAppServerConnected => _desktopAvailable;

    public bool IsDesktopCodexAvailable => _desktopAvailable;

    public string? DesktopStatusReason => _desktopStatusReason;

    public PairingWindow? CurrentPairingWindow { get; private set; }

    public IReadOnlyList<string> Diagnostics
    {
        get
        {
            lock (_diagnosticGate)
            {
                return _diagnostics.ToArray();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_dataDirectory);
        _repository = new BridgeRepository(
            Path.Combine(_dataDirectory, "bridge.db"),
            new DpapiFieldProtector("CodexMicroBridge.Database.v1"));
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        HostId = await _repository.GetMetaAsync("host_id", cancellationToken).ConfigureAwait(false) ?? Guid.NewGuid().ToString("N");
        await _repository.SetMetaAsync("host_id", HostId, cancellationToken).ConfigureAwait(false);
        WssUrl = BuildWssUrl(_listenAddress, _options.Port);
        _commands = new IdempotentCommandExecutor(_repository);
        _projects = new ProjectAllowList(_repository);

        var secrets = new DpapiSecretStore(Path.Combine(_dataDirectory, "secrets"));
        _certificate = new BridgeCertificateProvider(secrets).GetOrCreate(new Uri(WssUrl).Host);
        _pairing = new PairingService(_repository, _certificate.SpkiSha256Fingerprint);

        _state = new BridgeStateStore();
        _state.SnapshotChanged += OnSnapshotChanged;
        _state.Register(
            DesktopThreadId,
            "当前 Codex 桌面对话",
            Environment.CurrentDirectory,
            projectId: null);
        await _repository.AssignSlotAsync(1, DesktopThreadId, cancellationToken).ConfigureAwait(false);
        TrackBackground(StateBroadcastLoopAsync(), "broadcast ordered task state");

        await StartWebHostAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _mdns = new MdnsAdvertisement();
            _mdns.Start(HostId, _options.Port, CertificateSpkiSha256);
            AddDiagnostic("mDNS advertised _codexmicro._tcp (discovery hints remain untrusted until TLS pinning and pairing)." );
        }
        catch (Exception exception)
        {
            _mdns?.Dispose();
            _mdns = null;
            AddDiagnostic($"mDNS advertisement unavailable ({exception.GetType().Name}); manual WSS address still works.");
        }

        TrackBackground(DesktopSyncLoopAsync(), "monitor Codex desktop sync");
        AddDiagnostic($"Secure bridge started on TCP/{_options.Port} at /v1/mobile.");
        AddDiagnostic($"TLS certificate SHA-256: {_certificate.CertificateSha256Fingerprint}");
        AddDiagnostic($"TLS SPKI SHA-256 pin: {_certificate.SpkiSha256Fingerprint}");
        if (_certificate.ReissuedForHostChange)
        {
            AddDiagnostic("LAN address changed: TLS certificate was reissued. Re-pair the phone and accept the new SPKI pin; never disable hostname verification.");
        }
        if (IPAddress.IsLoopback(_listenAddress))
        {
            AddDiagnostic("No active RFC1918 Wi-Fi/Ethernet address was found. The bridge is loopback-only; choose a private network before pairing.");
        }
        AddDiagnostic("Desktop sync mode enabled: phone input is sent only to the verified current Codex desktop composer.");
    }

    private async Task DesktopSyncLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var inspection = await Task.Run(_desktopAutomation.Inspect, _lifetime.Token).ConfigureAwait(false);
                var availabilityChanged = _desktopAvailable != inspection.Available;
                var nextStatusReason = inspection.Available
                    ? null
                    : inspection.Error ?? "Codex 桌面窗口不可用。";
                var reasonChanged = !string.Equals(_desktopStatusReason, nextStatusReason, StringComparison.Ordinal);
                _desktopAvailable = inspection.Available;
                _desktopStatusReason = nextStatusReason;
                if (availabilityChanged || reasonChanged)
                {
                    AddDiagnostic(inspection.Available
                        ? "Verified Codex desktop window and composer are available for desktop sync."
                        : $"Codex desktop sync unavailable: {inspection.Error ?? "window not found"}");
                    await BroadcastEventAsync("bridge.status", CreateBridgeStatus()).ConfigureAwait(false);
                }

                if (!inspection.Available)
                {
                    var current = RequireState().Get(DesktopThreadId);
                    if (current is not null && current.State != BridgeTaskState.RecoveryUnknown)
                    {
                        RequireState().MarkRecoveryUnknown(DesktopThreadId, inspection.Error ?? "Codex 桌面窗口不可用。");
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token).ConfigureAwait(false);
                    continue;
                }

                var currentTitle = RequireState().Get(DesktopThreadId)?.Title;
                if (!string.Equals(currentTitle, inspection.ConversationTitle, StringComparison.Ordinal))
                {
                    RequireState().Register(DesktopThreadId, inspection.ConversationTitle);
                }

                if (_expectedDesktopPrompt is not null && _desktopSessionPath is null)
                {
                    _desktopSessionPath = _desktopSessions.FindSessionContainingPrompt(_expectedDesktopPrompt, _desktopPromptSentAt);
                    if (_desktopSessionPath is not null)
                    {
                        AddDiagnostic($"Bound desktop sync to session {Path.GetFileName(_desktopSessionPath)} after matching the phone prompt.");
                        _expectedDesktopPrompt = null;
                    }
                }

                if (inspection.Approval is not null)
                {
                    if (!string.Equals(_desktopApprovalFingerprint, inspection.Approval.Fingerprint, StringComparison.Ordinal))
                    {
                        await PublishDesktopApprovalAsync(inspection.Approval).ConfigureAwait(false);
                    }
                }
                else if (_desktopApprovalId is not null)
                {
                    await ResolveDesktopApprovalElsewhereAsync("resolved_elsewhere").ConfigureAwait(false);
                }

                if (inspection.Approval is null && inspection.IsRunning && !_desktopWasRunning)
                {
                    _desktopPromptSentAt = DateTimeOffset.UtcNow;
                    _desktopSessionPath = null;
                    _desktopTurnId = $"desktop-turn-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                    RequireState().ApplyNotification("turn/started", WireElement(new
                    {
                        threadId = DesktopThreadId,
                        turn = new { id = _desktopTurnId },
                    }));
                }
                else if (inspection.Approval is null && !inspection.IsRunning && _desktopWasRunning)
                {
                    RequireState().ApplyNotification("turn/completed", WireElement(new
                    {
                        threadId = DesktopThreadId,
                        turn = new { id = _desktopTurnId ?? DesktopThreadId, status = "completed" },
                    }));
                    await CaptureLatestDesktopResponseAsync().ConfigureAwait(false);
                }
                else if (inspection.Approval is null && !inspection.IsRunning)
                {
                    var snapshot = RequireState().Get(DesktopThreadId);
                    if (snapshot is not null && snapshot.State == BridgeTaskState.RecoveryUnknown)
                    {
                        RequireState().ReconcileAuthoritative(
                            DesktopThreadId,
                            BridgeTaskState.Idle,
                            activeTurnId: null,
                            lastTurnId: snapshot.LastTurnId,
                            title: inspection.ConversationTitle,
                            lastError: null);
                    }
                }

                _desktopWasRunning = inspection.IsRunning;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AddDiagnostic($"Desktop sync inspection failed ({exception.GetType().Name}); retrying.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(750), _lifetime.Token).ConfigureAwait(false);
        }
    }

    private async Task PublishDesktopApprovalAsync(DesktopApprovalTarget target)
    {
        if (_desktopApprovalId is not null)
        {
            await ResolveDesktopApprovalElsewhereAsync("expired").ConfigureAwait(false);
        }

        await _eventGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            var sequence = Interlocked.Increment(ref _eventSequence);
            var approvalId = $"desktop-{Epoch}-{sequence}";
            var turnId = _desktopTurnId ?? $"desktop-turn-{sequence}";
            var allowedDecisions = target.DeclineName is null
                ? new[] { "approve_once" }
                : new[] { "approve_once", "decline" };
            var request = new AppServerRequest(
                WireElement(approvalId),
                "desktop/ui/approval",
                WireElement(new { fingerprint = target.Fingerprint }));
            var approval = new PendingApproval(
                approvalId,
                DesktopThreadId,
                turnId,
                Epoch,
                sequence,
                "command",
                "确认当前桌面权限",
                target.Summary,
                new
                {
                    type = "command",
                    command = "在当前 Codex 桌面审批界面执行确认",
                    cwd = RequireState().Get(DesktopThreadId)?.WorkingDirectory ?? Environment.CurrentDirectory,
                    reason = target.Summary,
                    allowedDecisions,
                },
                allowedDecisions,
                new Dictionary<string, PermissionGrantPart>(StringComparer.Ordinal),
                request);
            _pendingApprovals[approvalId] = approval;
            _desktopApprovalId = approvalId;
            _desktopApprovalFingerprint = target.Fingerprint;
            RequireState().MarkNeedsInput(DesktopThreadId, turnId, isUserInput: false);
            await SendToReadyClientsAsync("approval.requested", new { approval = approval.ToWire() }, sequence)
                .ConfigureAwait(false);
            AddDiagnostic("Detected a verified approval control in the current Codex desktop conversation.");
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private async Task ResolveDesktopApprovalElsewhereAsync(string resolution)
    {
        var approvalId = _desktopApprovalId;
        _desktopApprovalId = null;
        _desktopApprovalFingerprint = null;
        if (approvalId is null || !_pendingApprovals.TryRemove(approvalId, out var approval))
        {
            return;
        }

        approval.Resolution = resolution;
        await BroadcastEventAsync("approval.resolved", new
        {
            approvalId = approval.ApprovalId,
            threadId = approval.ThreadId,
            turnId = approval.TurnId,
            epoch = approval.Epoch,
            seq = approval.Sequence,
            resolution,
        }).ConfigureAwait(false);
    }

    private async Task CaptureLatestDesktopResponseAsync()
    {
        var path = _desktopSessionPath ?? _desktopSessions.FindMostRecentRootSession(_desktopPromptSentAt);
        if (path is null)
        {
            AddDiagnostic("Desktop turn completed, but its session file could not be identified; phone status remains authoritative from the UI.");
            return;
        }

        IReadOnlyList<DesktopSessionMessage> messages = [];
        for (var attempt = 0; attempt < 5; attempt++)
        {
            messages = _desktopSessions.ReadConversationMessages(path);
            if (messages.Any(message =>
                    message.Role == "assistant" &&
                    message.Timestamp >= _desktopPromptSentAt.AddSeconds(-2)))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), _lifetime.Token).ConfigureAwait(false);
        }

        if (messages.Count == 0)
        {
            AddDiagnostic("Desktop turn completed without readable conversation messages yet; task.read can retry after the file flushes.");
            return;
        }

        foreach (var message in messages)
        {
            await PersistCompletedMessageAsync(new BridgeMessage(
                message.MessageId,
                DesktopThreadId,
                message.TurnId,
                message.MessageId,
                message.Role,
                message.Text,
                message.Timestamp)).ConfigureAwait(false);
        }
        _desktopSessionPath = path;
    }

    public PairingWindow OpenPairingWindow()
    {
        var pairing = _pairing ?? throw new InvalidOperationException("The bridge has not initialized.");
        CurrentPairingWindow = pairing.OpenWindow();
        AddDiagnostic($"Pairing opened until {CurrentPairingWindow.ExpiresAt:HH:mm:ss}.");
        RuntimeChanged?.Invoke();
        return CurrentPairingWindow;
    }

    public async Task PublishApprovalTestAsync(CancellationToken cancellationToken = default)
    {
        if (!_clients.Values.Any(connection => connection.BusinessReady))
        {
            throw new InvalidOperationException("请先让手机保持已连接，再启动审批测试。");
        }

        await _eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = Interlocked.Increment(ref _eventSequence);
            var approvalId = $"desktop-test-{Epoch}-{sequence}";
            var turnId = $"desktop-test-turn-{sequence}";
            var allowedDecisions = new[] { "approve_once", "decline" };
            var request = new AppServerRequest(
                WireElement(approvalId),
                "desktop/test/approval",
                WireElement(new { }));
            var approval = new PendingApproval(
                approvalId,
                DesktopThreadId,
                turnId,
                Epoch,
                sequence,
                "command",
                "审批链路测试",
                "批准后由桌面桥接启动 Windows 记事本；拒绝不会执行任何操作。",
                new
                {
                    type = "command",
                    command = "启动 Windows 记事本（Codex Micro 审批测试）",
                    cwd = Environment.CurrentDirectory,
                    reason = "验证手机审批、一次性绑定和桌面执行链路",
                    allowedDecisions,
                },
                allowedDecisions,
                new Dictionary<string, PermissionGrantPart>(StringComparer.Ordinal),
                request);
            _pendingApprovals[approvalId] = approval;
            RequireState().MarkNeedsInput(DesktopThreadId, turnId, isUserInput: false);
            await SendToReadyClientsAsync("approval.requested", new { approval = approval.ToWire() }, sequence)
                .ConfigureAwait(false);
            AddDiagnostic("Sent a safe approval-chain test to the connected phone; Notepad starts only after phone approval.");
        }
        finally
        {
            _eventGate.Release();
        }
    }

    public string CreatePairingQrPayload(PairingWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return JsonSerializer.Serialize(new
        {
            v = 1,
            hostId = HostId,
            wssUrl = WssUrl,
            certSpkiSha256 = CertificateSpkiSha256,
            nonce = window.ServerNonce,
            pairingCode = window.Code,
            expiresAt = window.ExpiresAt.ToUnixTimeSeconds(),
        });
    }

    public Task<IReadOnlyList<AllowedProject>> GetAllowedProjectsAsync(CancellationToken cancellationToken = default) =>
        RequireProjects().ListAsync(cancellationToken);

    public async Task AddAllowedProjectAsync(string path, CancellationToken cancellationToken = default)
    {
        var project = await RequireProjects().AddAsync(path, cancellationToken).ConfigureAwait(false);
        AddDiagnostic($"Allowed project catalog entry: {project.ProjectId}.");
        RuntimeChanged?.Invoke();
    }

    public async Task RemoveAllowedProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        await RequireProjects().RemoveAsync(projectId, cancellationToken).ConfigureAwait(false);
        AddDiagnostic($"Removed project allow-list entry: {projectId}");
        RuntimeChanged?.Invoke();
    }

    public Task<IReadOnlyList<PairedDevice>> GetPairedDevicesAsync(CancellationToken cancellationToken = default) =>
        RequirePairing().GetPairedDevicesAsync(cancellationToken);

    public async Task<bool> RevokePairedDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var revoked = await RequirePairing().RevokeDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
        foreach (var connection in _clients.Values.Where(candidate =>
                     string.Equals(candidate.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            connection.BusinessReady = false;
            connection.DeviceId = null;
            if (connection.Socket.State == WebSocketState.Open)
            {
                try
                {
                    await connection.Socket.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "This paired device was revoked",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }

        if (revoked)
        {
            AddDiagnostic($"Revoked paired device: {deviceId}.");
            RuntimeChanged?.Invoke();
        }

        return revoked;
    }

    private async Task<bool> StartAppServerAsync(CancellationToken cancellationToken)
    {
        var client = new AppServerClient();
        client.DiagnosticReceived += message =>
            AddDiagnostic($"App Server stderr received ({Encoding.UTF8.GetByteCount(message)} bytes); content suppressed.");
        client.NotificationReceived += OnAppServerNotificationAsync;
        client.ServerRequestReceived += OnAppServerRequestAsync;
        client.Exited += code =>
        {
            if (Volatile.Read(ref _disposed) == 0 && ReferenceEquals(Interlocked.CompareExchange(ref _appServer, null, client), client))
            {
                TrackBackground(HandleAppServerExitAsync(client, code), "recover App Server");
            }
        };

        var executable = ResolveCodexExecutable();

        try
        {
            ValidateCodexRuntimeCompanions(executable);
            var version = await CodexCliVersionVerifier.VerifyAsync(
                executable,
                _options.AllowUnverifiedCodexVersion,
                cancellationToken).ConfigureAwait(false);
            AddDiagnostic($"Verified Codex CLI: {version}");
            await client.StartAndInitializeAsync(new AppServerStartOptions(executable), cancellationToken).ConfigureAwait(false);
            var modelList = await client.SendRequestAsync("model/list", new
            {
                limit = 100,
                includeHidden = false,
            }, cancellationToken).ConfigureAwait(false);
            lock (_catalogGate)
            {
                _modelCatalog = NormalizeModelCatalog(modelList);
            }

            if (_modelCatalog.Count == 0)
            {
                throw new InvalidOperationException("model/list returned no V1-compatible models.");
            }

            _appServer = client;
            AddDiagnostic($"Codex App Server initialized; account/read and model/list completed ({_modelCatalog.Count} models).");
            RuntimeChanged?.Invoke();
            return true;
        }
        catch (Exception exception)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            AddDiagnostic($"Codex App Server unavailable ({exception.GetType().Name}); details suppressed.");
            RuntimeChanged?.Invoke();
            return false;
        }
    }

    private string ResolveCodexExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_options.CodexExecutablePath))
        {
            return _options.CodexExecutablePath;
        }

        var configured = Environment.GetEnvironmentVariable("CODEX_BRIDGE_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var bundled = Path.Combine(AppContext.BaseDirectory, "codex.exe");
        return File.Exists(bundled) ? bundled : "codex";
    }

    private void ValidateCodexRuntimeCompanions(string executable)
    {
        if (!Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetFileName(executable), "codex.exe", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executable))
        {
            return;
        }

        var codeModeHost = Path.Combine(Path.GetDirectoryName(executable)!, "codex-code-mode-host.exe");
        if (File.Exists(codeModeHost) && new FileInfo(codeModeHost).Length > 0)
        {
            return;
        }

        AddDiagnostic("Codex runtime is incomplete: codex-code-mode-host.exe is missing beside codex.exe.");
        throw new FileNotFoundException(
            "The bundled Codex runtime requires codex-code-mode-host.exe beside codex.exe.",
            codeModeHost);
    }

    private async Task HandleAppServerExitAsync(AppServerClient exitedClient, int? exitCode)
    {
        await exitedClient.DisposeAsync().ConfigureAwait(false);
        AddDiagnostic($"App Server exited (code {exitCode?.ToString() ?? "unknown"}); rotating protocol epoch.");
        RotateEpoch();
        await InvalidateApprovalsAsync("expired").ConfigureAwait(false);
        foreach (var task in RequireState().GetAll().Where(task =>
                     task.State is BridgeTaskState.Running or BridgeTaskState.NeedsApproval or BridgeTaskState.NeedsReply))
        {
            RequireState().MarkRecoveryUnknown(task.ThreadId, "App Server disconnected during an active turn.");
        }

        RuntimeChanged?.Invoke();
        await BroadcastEventAsync("snapshot", await GetBridgeSnapshotAsync(_lifetime.Token).ConfigureAwait(false)).ConfigureAwait(false);
        await BroadcastEventAsync("bridge.status", CreateBridgeStatus()).ConfigureAwait(false);
        await RestartAppServerLoopAsync().ConfigureAwait(false);
    }

    private async Task RestartAppServerLoopAsync()
    {
        await _restartGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            if (IsAppServerConnected)
            {
                return;
            }

            var delays = new[] { 1, 2, 5, 10, 20, 30 };
            foreach (var seconds in delays)
            {
                AddDiagnostic($"App Server restart scheduled in {seconds}s.");
                await Task.Delay(TimeSpan.FromSeconds(seconds), _lifetime.Token).ConfigureAwait(false);
                if (await StartAppServerAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    await RecoverOwnedThreadsAsync(_lifetime.Token).ConfigureAwait(false);
                    await BroadcastEventAsync("snapshot", await GetBridgeSnapshotAsync(_lifetime.Token).ConfigureAwait(false))
                        .ConfigureAwait(false);
                    AddDiagnostic("App Server recovery completed; authoritative snapshot published.");
                    return;
                }
            }

            AddDiagnostic("App Server restart limit reached. Use desktop diagnostics after fixing the executable or version.");
            await BroadcastEventAsync("bridge.status", CreateBridgeStatus()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _restartGate.Release();
        }
    }

    private async Task RecoverOwnedThreadsAsync(CancellationToken cancellationToken)
    {
        foreach (var task in RequireState().GetAll())
        {
            try
            {
                _ = await RequireAppServer().SendRequestAsync("thread/resume", new { threadId = task.ThreadId }, cancellationToken)
                    .ConfigureAwait(false);
                var read = await RequireAppServer().SendRequestAsync("thread/read", new
                {
                    threadId = task.ThreadId,
                    includeTurns = true,
                }, cancellationToken).ConfigureAwait(false);
                await ApplyThreadReadAsync(task.ThreadId, read, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is AppServerRpcException or InvalidOperationException or ProtocolException)
            {
                RequireState().MarkRecoveryUnknown(task.ThreadId, "Could not resume the owned thread from authoritative App Server state.");
            }
        }
    }

    private async Task ApplyThreadReadAsync(
        string expectedThreadId,
        JsonElement response,
        CancellationToken cancellationToken)
    {
        await _messageGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (!response.TryGetProperty("thread", out var thread) || thread.ValueKind != JsonValueKind.Object ||
            !string.Equals(OptionalString(thread, "id"), expectedThreadId, StringComparison.Ordinal) ||
            !thread.TryGetProperty("turns", out var turns) || turns.ValueKind != JsonValueKind.Array)
        {
            RequireState().MarkRecoveryUnknown(expectedThreadId, "thread/read did not match the pinned schema.");
            throw new ProtocolException("APP_SERVER_SCHEMA_MISMATCH", "Codex App Server thread/read response did not match the pinned schema.");
        }

        var turnList = turns.EnumerateArray()
            .Where(turn => turn.ValueKind == JsonValueKind.Object)
            .Select(turn => turn.Clone())
            .ToArray();
        var latestTurn = turnList.LastOrDefault();
        var latestTurnId = latestTurn.ValueKind == JsonValueKind.Object ? OptionalString(latestTurn, "id") : null;
        var latestTurnStatus = latestTurn.ValueKind == JsonValueKind.Object ? OptionalString(latestTurn, "status") : null;
        var activeTurn = turnList.LastOrDefault(turn => OptionalString(turn, "status") == "inProgress");
        var activeTurnId = activeTurn.ValueKind == JsonValueKind.Object ? OptionalString(activeTurn, "id") : null;

        var hasNewMessages = false;
        foreach (var turn in turnList)
        {
            var turnId = OptionalString(turn, "id");
            if (string.IsNullOrWhiteSpace(turnId) || !turn.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var completedAt = turn.TryGetProperty("completedAt", out var completed) && completed.TryGetInt64(out var completedSeconds)
                ? DateTimeOffset.FromUnixTimeSeconds(completedSeconds)
                : DateTimeOffset.UtcNow;
            foreach (var item in items.EnumerateArray())
            {
                if (OptionalString(item, "type") != "agentMessage")
                {
                    continue;
                }

                var itemId = OptionalString(item, "id");
                var text = OptionalString(item, "text");
                if (string.IsNullOrWhiteSpace(itemId) || text is null)
                {
                    continue;
                }

                if (!await RequireRepository().MessageExistsAsync(itemId, cancellationToken).ConfigureAwait(false))
                {
                    hasNewMessages = true;
                }

                await RequireRepository().SaveMessageAsync(new BridgeMessage(
                    itemId,
                    expectedThreadId,
                    turnId,
                    itemId,
                    "assistant",
                    text,
                    completedAt), cancellationToken).ConfigureAwait(false);
            }
        }

        var threadStatus = thread.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object
            ? OptionalString(status, "type")
            : null;
        var activeFlags = thread.TryGetProperty("status", out status) && status.ValueKind == JsonValueKind.Object &&
            status.TryGetProperty("activeFlags", out var flags) && flags.ValueKind == JsonValueKind.Array
                ? flags.EnumerateArray()
                    .Where(flag => flag.ValueKind == JsonValueKind.String)
                    .Select(flag => flag.GetString())
                    .Where(flag => flag is not null)
                    .Cast<string>()
                    .ToArray()
                : Array.Empty<string>();
        var current = RequireState().Get(expectedThreadId);
        var authoritativeState = threadStatus switch
        {
            "systemError" => BridgeTaskState.Failed,
            "active" when activeFlags.Contains("waitingOnUserInput", StringComparer.Ordinal) => BridgeTaskState.NeedsReply,
            "active" when activeFlags.Any(flag => flag.Contains("waiting", StringComparison.OrdinalIgnoreCase)) => BridgeTaskState.NeedsApproval,
            "active" => BridgeTaskState.Running,
            _ when latestTurnStatus == "inProgress" => BridgeTaskState.Running,
            _ when latestTurnStatus == "interrupted" => BridgeTaskState.Interrupted,
            _ when latestTurnStatus == "failed" => BridgeTaskState.Failed,
            _ when latestTurnStatus == "completed" &&
                !hasNewMessages &&
                current?.State == BridgeTaskState.Idle &&
                string.Equals(current.LastTurnId, latestTurnId, StringComparison.Ordinal) => BridgeTaskState.Idle,
            _ when latestTurnStatus == "completed" => BridgeTaskState.Completed,
            "notLoaded" when current?.ActiveTurnId is not null => BridgeTaskState.RecoveryUnknown,
            "notLoaded" when current?.State is BridgeTaskState.Completed or BridgeTaskState.Failed or BridgeTaskState.Interrupted => current.State,
            "notLoaded" => BridgeTaskState.Idle,
            "idle" => BridgeTaskState.Idle,
            _ => BridgeTaskState.RecoveryUnknown,
        };
        var error = latestTurn.ValueKind == JsonValueKind.Object
            ? ReadNestedString(latestTurn, "error", "message")
            : null;
        RequireState().ReconcileAuthoritative(
            expectedThreadId,
            authoritativeState,
            activeTurnId,
            latestTurnId,
            OptionalString(thread, "name"),
            OptionalString(thread, "cwd"),
            error is null ? null : "Codex App Server reported a turn error.");
        }
        finally
        {
            _messageGate.Release();
        }
    }

    private async Task InvalidateApprovalsAsync(string resolution)
    {
        var approvals = _pendingApprovals.Values.ToArray();
        _pendingApprovals.Clear();
        _approvalIdsByRequest.Clear();
        foreach (var approval in approvals)
        {
            await BroadcastEventAsync("approval.resolved", new
            {
                approvalId = approval.ApprovalId,
                threadId = approval.ThreadId,
                turnId = approval.TurnId,
                epoch = approval.Epoch,
                seq = approval.Sequence,
                resolution,
            }).ConfigureAwait(false);
        }
    }

    private void RotateEpoch()
    {
        Epoch = Guid.NewGuid().ToString("N");
        Interlocked.Exchange(ref _eventSequence, 0);
    }

    private async Task StartWebHostAsync(CancellationToken cancellationToken)
    {
        var certificate = _certificate?.Certificate ?? throw new InvalidOperationException("TLS certificate is unavailable.");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(BridgeRuntime).Assembly.FullName,
        });
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Listen(_listenAddress, _options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(certificate);
            });
        });

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (!PrivateNetworkPolicy.IsAllowedRemote(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            AllowedOrigins = { },
        });
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
        app.MapGet("/fingerprint", () => Results.Json(new
        {
            algorithm = "SHA-256",
            fingerprint = CertificateFingerprint,
            spkiSha256 = CertificateSpkiSha256,
            pinRequired = true,
        }));
        app.MapGet("/v1/mobile", HandleWebSocketAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _webApplication = app;
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!PrivateNetworkPolicy.IsAllowedRemote(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connection = new ClientConnection(Guid.NewGuid(), socket);
        _clients[connection.Id] = connection;
        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var message = await ReceiveTextAsync(socket, context.RequestAborted).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                await HandleClientRequestAsync(connection, message, context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
        }
        catch (WebSocketException exception)
        {
            AddDiagnostic($"Phone WebSocket closed ({exception.GetType().Name}).");
        }
        catch (MobileEnvelopeSizeException exception)
        {
            AddDiagnostic($"Rejected oversized inbound mobile envelope ({exception.ActualBytes} bytes; limit {exception.MaximumBytes}).");
            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    "Mobile envelopes are limited to 1 MiB",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            _clients.TryRemove(connection.Id, out _);
            connection.SendLock.Dispose();
            AddDiagnostic($"Phone WebSocket ended (state={socket.State}, close={socket.CloseStatus?.ToString() ?? "none"}).");
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Bridge connection closed", CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    private async Task HandleClientRequestAsync(ClientConnection connection, string message, CancellationToken cancellationToken)
    {
        var id = "invalid";
        try
        {
            var request = MobileRequestValidator.Validate(message);
            id = request.Id;
            var result = await DispatchClientOperationAsync(connection, request.Operation, request.Parameters, cancellationToken)
                .ConfigureAwait(false);
            await SendEnvelopeAsync(connection, new { v = 1, id, result }, cancellationToken).ConfigureAwait(false);
            if (connection.DeviceId is not null && !connection.BusinessReady)
            {
                try
                {
                    await SendSnapshotEventAsync(connection, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // Authentication already succeeded and its response was delivered.  A
                    // snapshot failure must trigger a clean reconnect, not a second response
                    // with the same request id (which older Android clients could interpret as
                    // a permanent authentication failure).
                    connection.BusinessReady = false;
                    var correlationId = Guid.NewGuid().ToString("N")[..12];
                    AddDiagnostic($"[{correlationId}] Initial mobile snapshot failed ({exception.GetType().Name}); reconnect requested.");
                    if (connection.Socket.State == WebSocketState.Open)
                    {
                        await connection.Socket.CloseAsync(
                            WebSocketCloseStatus.InternalServerError,
                            "Initial snapshot unavailable; reconnect",
                            CancellationToken.None).ConfigureAwait(false);
                    }
                }
            }
        }
        catch (MobileRequestValidationException exception)
        {
            await SendErrorAsync(connection, id, exception.Code, exception.Message, false, cancellationToken).ConfigureAwait(false);
        }
        catch (ProtocolException exception)
        {
            await SendErrorAsync(connection, id, exception.Code, exception.Message, false, cancellationToken).ConfigureAwait(false);
        }
        catch (MobileEnvelopeSizeException exception)
        {
            AddDiagnostic($"Rejected oversized outbound mobile response ({exception.ActualBytes} bytes; limit {exception.MaximumBytes}).");
            await SendErrorAsync(
                connection,
                id,
                "OVERLOADED",
                "The requested response exceeds the 1 MiB mobile envelope limit. See desktop diagnostics.",
                false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (IdempotencyConflictException)
        {
            await SendErrorAsync(connection, id, "IDEMPOTENCY_CONFLICT", "clientCommandId was reused for a different request.", false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            var code = connection.DeviceId is null ? "AUTH_FAILED" : "THREAD_NOT_FOUND";
            await SendErrorAsync(connection, id, code, "The requested identity or managed resource was not authorized.", false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AppServerRpcException exception)
        {
            var correlationId = Guid.NewGuid().ToString("N")[..12];
            AddDiagnostic($"[{correlationId}] App Server request failed ({exception.GetType().Name}); upstream details suppressed.");
            await SendErrorAsync(connection, id, "APP_SERVER_UNAVAILABLE", $"Codex App Server request failed. Reference {correlationId}.", true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("App Server", StringComparison.OrdinalIgnoreCase))
        {
            await SendErrorAsync(connection, id, "APP_SERVER_UNAVAILABLE", "Codex App Server is unavailable. Check desktop diagnostics.", true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            await SendErrorAsync(connection, id, "INVALID_MESSAGE", "The request contains an invalid parameter.", false, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A phone can close or replace its WebSocket while authentication, snapshot delivery,
            // or a command response is in flight.  That is a transport cancellation, not an
            // INTERNAL protocol failure and there is no live request channel left to answer.
            AddDiagnostic("Mobile request was cancelled because the phone connection closed; no error response was sent.");
        }
        catch (OperationCanceledException)
        {
            await SendErrorAsync(
                connection,
                id,
                "TIMEOUT",
                "The desktop operation did not finish before its deadline. Please retry.",
                true,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var correlationId = Guid.NewGuid().ToString("N")[..12];
            AddDiagnostic($"[{correlationId}] Mobile request failed ({exception.GetType().Name}); details suppressed.");
            await SendErrorAsync(connection, id, "INTERNAL", $"The bridge could not complete the request. Reference {correlationId}.", false, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<JsonElement> DispatchClientOperationAsync(
        ClientConnection connection,
        string operation,
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case "pairing.info":
            {
                var window = RequirePairing().GetWindowInfo()
                    ?? throw new ProtocolException("AUTH_FAILED", "Pairing is not open or has expired.");
                return WireElement(new
                {
                    pairing = new
                    {
                        serverNonce = window.ServerNonce,
                        expiresAt = window.ExpiresAt,
                        certificateFingerprint = window.CertificateFingerprint,
                    },
                    certificateFingerprint = CertificateFingerprint,
                    certSpkiSha256 = CertificateSpkiSha256,
                });
            }
            case "pairing.complete":
            {
                var proof = new PairingProof(
                    RequiredString(parameters, "code"),
                    RequiredString(parameters, "deviceId"),
                    RequiredString(parameters, "displayName"),
                    RequiredString(parameters, "clientPublicKeySpki"),
                    RequiredString(parameters, "clientNonce"),
                    RequiredString(parameters, "signatureDer"));
                var device = await RequirePairing().CompletePairingAsync(proof, cancellationToken).ConfigureAwait(false);
                connection.DeviceId = device.DeviceId;
                await MakeSoleBusinessConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
                CurrentPairingWindow = null;
                RuntimeChanged?.Invoke();
                AddDiagnostic($"Paired device authorized: {device.DeviceId}.");
                return WireElement(new { authenticated = true, deviceId = device.DeviceId });
            }
            case "auth.challenge":
            {
                var deviceId = RequiredString(parameters, "deviceId");
                var challenge = await RequirePairing().BeginAuthenticationAsync(deviceId, cancellationToken).ConfigureAwait(false);
                return WireElement(new
                {
                    challengeId = challenge.ChallengeId,
                    deviceId = challenge.DeviceId,
                    serverNonce = challenge.ServerNonce,
                    expiresAt = challenge.ExpiresAt,
                    certificateFingerprint = challenge.CertificateFingerprint,
                });
            }
            case "auth.complete":
            {
                var challengeId = RequiredString(parameters, "challengeId");
                var signature = RequiredString(parameters, "signatureDer");
                var device = await RequirePairing().CompleteAuthenticationAsync(challengeId, signature, cancellationToken)
                    .ConfigureAwait(false);
                connection.DeviceId = device.DeviceId;
                await MakeSoleBusinessConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
                return WireElement(new { authenticated = true, deviceId = device.DeviceId });
            }
        }

        var authenticatedDeviceId = connection.DeviceId
            ?? throw new ProtocolException("AUTH_REQUIRED", "Pair or authenticate this device first.");

        return operation switch
        {
            "tasks.list" => await ListTasksAsync(cancellationToken).ConfigureAwait(false),
            "task.create" => await CreateTaskAsync(authenticatedDeviceId, parameters, cancellationToken).ConfigureAwait(false),
            "task.read" => await ReadTaskAsync(parameters, cancellationToken).ConfigureAwait(false),
            "task.send" => await SendTaskMessageAsync(authenticatedDeviceId, parameters, cancellationToken).ConfigureAwait(false),
            "task.interrupt" => await InterruptTaskAsync(authenticatedDeviceId, parameters, cancellationToken).ConfigureAwait(false),
            "task.fork" => await ForkTaskAsync(authenticatedDeviceId, parameters, cancellationToken).ConfigureAwait(false),
            "approval.respond" => await RespondToApprovalAsync(authenticatedDeviceId, parameters, cancellationToken).ConfigureAwait(false),
            "task.read_ack" => await MarkTaskReadAsync(authenticatedDeviceId, parameters, cancellationToken).ConfigureAwait(false),
            "slot.assign" => await AssignSlotAsync(authenticatedDeviceId, parameters, cancellationToken).ConfigureAwait(false),
            _ => throw new ProtocolException("INVALID_MESSAGE", $"Operation '{operation}' is not supported."),
        };
    }

    private async Task<JsonElement> GetBridgeSnapshotAsync(CancellationToken cancellationToken)
    {
        return WireElement(new
        {
            bridge = CreateBridgeStatus(),
            tasks = await CreateWireTasksAsync(cancellationToken).ConfigureAwait(false),
            slots = await CreateWireSlotsAsync(cancellationToken).ConfigureAwait(false),
            approvals = _pendingApprovals.Values.Select(approval => approval.ToWire()),
            projectCatalog = (await RequireProjects().ListAsync(cancellationToken).ConfigureAwait(false))
                .Select(project => new { projectId = project.ProjectId, displayName = NonEmpty(project.DisplayName, project.ProjectId, 200) }),
            modelCatalog = GetModelCatalog(),
        });
    }

    private async Task SendSnapshotEventAsync(ClientConnection connection, CancellationToken cancellationToken)
    {
        await _eventGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await GetBridgeSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var sequence = Interlocked.Increment(ref _eventSequence);
            await SendEventEnvelopeAsync(connection, "snapshot", snapshot, sequence, cancellationToken).ConfigureAwait(false);
            connection.BusinessReady = connection.Socket.State == WebSocketState.Open;
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private async Task<JsonElement> ListTasksAsync(CancellationToken cancellationToken)
    {
        return WireElement(new
        {
            epoch = Epoch,
            seq = Math.Max(1, Interlocked.Read(ref _eventSequence)),
            slots = await CreateWireSlotsAsync(cancellationToken).ConfigureAwait(false),
            tasks = await CreateWireTasksAsync(cancellationToken).ConfigureAwait(false),
            projectCatalog = (await RequireProjects().ListAsync(cancellationToken).ConfigureAwait(false))
                .Select(project => new { projectId = project.ProjectId, displayName = NonEmpty(project.DisplayName, project.ProjectId, 200) }),
            modelCatalog = GetModelCatalog(),
        });
    }

    private Task<JsonElement> CreateTaskAsync(string deviceId, JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireCurrentEpoch(parameters);
        var commandId = RequiredString(parameters, "clientCommandId");
        return RequireCommands().ExecuteAsync(deviceId, commandId, "task.create", parameters, async token =>
        {
            var prompt = RequiredString(parameters, "prompt");
            await SendToDesktopAsync(prompt, token).ConfigureAwait(false);
            return WireElement(new
            {
                task = await CreateWireTaskAsync(RequireState().Get(DesktopThreadId)!, token).ConfigureAwait(false),
            });
        }, cancellationToken);
    }

    private async Task<JsonElement> ReadTaskAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var threadId = RequireManagedThread(parameters);
        var snapshot = RequireState().Get(threadId)
            ?? throw new InvalidOperationException("Managed task state is unavailable.");
        var messageWindow = CreateTaskReadMessageWindow(
            await RequireRepository().GetMessagesAsync(threadId, cancellationToken).ConfigureAwait(false));
        if (messageWindow.Truncated)
        {
            AddDiagnostic($"Bounded task.read message history to the 512 KiB wire budget for thread {threadId}.");
        }

        return WireElement(new
        {
            epoch = Epoch,
            seq = Math.Max(1, Interlocked.Read(ref _eventSequence)),
            task = await CreateWireTaskAsync(snapshot, cancellationToken).ConfigureAwait(false),
            messages = messageWindow.Messages,
            approvals = _pendingApprovals.Values
                .Where(approval => string.Equals(approval.ThreadId, threadId, StringComparison.Ordinal))
                .Select(approval => approval.ToWire()),
        });
    }

    private Task<JsonElement> SendTaskMessageAsync(string deviceId, JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireCurrentEpoch(parameters);
        var commandId = RequiredString(parameters, "clientCommandId");
        return RequireCommands().ExecuteAsync(deviceId, commandId, "task.send", parameters, async token =>
        {
            var threadId = RequireManagedThread(parameters);
            var message = RequiredString(parameters, "text");
            await SendToDesktopAsync(message, token).ConfigureAwait(false);
            return WireElement(new { accepted = true, threadId, turnId = _desktopTurnId ?? DesktopThreadId });
        }, cancellationToken);
    }

    private async Task SendToDesktopAsync(string message, CancellationToken cancellationToken)
    {
        var inspection = await Task.Run(_desktopAutomation.Inspect, cancellationToken).ConfigureAwait(false);
        if (!inspection.Available)
        {
            throw new ProtocolException(
                "BRIDGE_OFFLINE",
                inspection.Error ?? "未找到当前 Codex 桌面窗口，请先打开目标对话。");
        }

        var sentAt = DateTimeOffset.UtcNow;
        await RunDesktopActionAsync(
            () => _desktopAutomation.SendMessage(message),
            "发送桌面消息",
            cancellationToken).ConfigureAwait(false);
        _desktopPromptSentAt = sentAt;
        _expectedDesktopPrompt = message;
        _desktopSessionPath = null;
        _desktopTurnId = $"desktop-turn-{sentAt.ToUnixTimeMilliseconds()}";
        RequireState().ApplyNotification("turn/started", WireElement(new
        {
            threadId = DesktopThreadId,
            turn = new { id = _desktopTurnId },
        }));
        _desktopWasRunning = true;
        AddDiagnostic("Phone message was written to the verified current Codex desktop composer and sent.");
    }

    private Task<JsonElement> InterruptTaskAsync(string deviceId, JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireCurrentEpoch(parameters);
        var commandId = RequiredString(parameters, "clientCommandId");
        return RequireCommands().ExecuteAsync(deviceId, commandId, "task.interrupt", parameters, async token =>
        {
            var threadId = RequireManagedThread(parameters);
            var requestedTurnId = RequiredString(parameters, "turnId");
            await RunDesktopActionAsync(
                _desktopAutomation.StopCurrentTurn,
                "停止桌面任务",
                token).ConfigureAwait(false);
            return WireElement(new { accepted = true, threadId, turnId = requestedTurnId });
        }, cancellationToken);
    }

    private Task<JsonElement> ForkTaskAsync(string deviceId, JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireCurrentEpoch(parameters);
        _ = deviceId;
        _ = parameters;
        _ = cancellationToken;
        throw new ProtocolException("INVALID_MESSAGE", "桌面同步模式只控制当前 Codex 对话，不会创建独立分支任务。");
    }

    private Task<JsonElement> AssignSlotAsync(string deviceId, JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireCurrentEpoch(parameters);
        var commandId = RequiredString(parameters, "clientCommandId");
        return RequireCommands().ExecuteAsync(deviceId, commandId, "slot.assign", parameters, async token =>
        {
            var slot = RequiredInt32(parameters, "slot");
            RequireSlot(slot);
            var threadId = OptionalString(parameters, "threadId");
            if (threadId is null)
            {
                await RequireRepository().ClearSlotAsync(slot, token).ConfigureAwait(false);
            }
            else
            {
                _ = RequireManagedThread(parameters);
                await RequireRepository().AssignSlotAsync(slot, threadId, token).ConfigureAwait(false);
            }

            var result = WireElement(new { accepted = true, slot, threadId });
            await BroadcastEventAsync("snapshot", await GetBridgeSnapshotAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
            return result;
        }, cancellationToken);
    }

    private Task<JsonElement> RespondToApprovalAsync(string deviceId, JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireCurrentEpoch(parameters);
        var commandId = RequiredString(parameters, "clientCommandId");
        return RequireCommands().ExecuteAsync(deviceId, commandId, "approval.respond", parameters, async token =>
        {
            var approvalId = RequiredString(parameters, "approvalId");
            if (!_pendingApprovals.TryGetValue(approvalId, out var approval))
            {
                throw new ProtocolException("APPROVAL_NOT_PENDING", "The App Server request is already resolved or no longer exists.");
            }

            var bindingError = ApprovalBindingPolicy.Validate(
                approval.Epoch,
                approval.Sequence,
                approval.ThreadId,
                approval.TurnId,
                RequiredString(parameters, "epoch"),
                RequiredInt64(parameters, "seq"),
                RequiredString(parameters, "threadId"),
                RequiredString(parameters, "turnId"));
            if (bindingError is not null)
            {
                throw new ProtocolException(
                    bindingError,
                    bindingError == "APPROVAL_STALE"
                        ? "The approval belongs to an earlier event generation."
                        : "Approval thread or turn binding does not match.");
            }

            if (!approval.TryBeginResponse())
            {
                throw new ProtocolException("APPROVAL_NOT_PENDING", "The approval response was already sent.");
            }

            try
            {
                if (!parameters.TryGetProperty("response", out var response))
                {
                    throw new ProtocolException("INVALID_MESSAGE", "approval.respond requires a tagged response object.");
                }

                var normalized = NormalizeApprovalResponse(approval, response);
                approval.Resolution = normalized.Resolution;
                if (string.Equals(approval.Request.Method, "desktop/test/approval", StringComparison.Ordinal))
                {
                    var decision = OptionalString(response, "decision")
                        ?? throw new ProtocolException("INVALID_MESSAGE", "审批测试响应缺少 decision。");
                    var approve = decision is "approve_once" or "approve_session";
                    if (approve)
                    {
                        var notepad = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                            "System32",
                            "notepad.exe");
                        Process.Start(new ProcessStartInfo(notepad) { UseShellExecute = true });
                    }

                    _pendingApprovals.TryRemove(approval.ApprovalId, out _);
                    await BroadcastEventAsync("approval.resolved", new
                    {
                        approvalId = approval.ApprovalId,
                        threadId = approval.ThreadId,
                        turnId = approval.TurnId,
                        epoch = approval.Epoch,
                        seq = approval.Sequence,
                        resolution = normalized.Resolution,
                    }).ConfigureAwait(false);
                    AddDiagnostic(approve
                        ? "Phone approved the test request; Windows Notepad was launched."
                        : "Phone declined the test request; no desktop action was performed.");
                }
                else if (string.Equals(approval.Request.Method, "desktop/ui/approval", StringComparison.Ordinal))
                {
                    var decision = OptionalString(response, "decision")
                        ?? throw new ProtocolException("INVALID_MESSAGE", "桌面审批响应缺少 decision。");
                    var approve = decision is "approve_once" or "approve_session";
                    var fingerprint = RequiredString(approval.Request.Parameters, "fingerprint");
                    await RunDesktopActionAsync(
                        () => _desktopAutomation.ResolveApproval(fingerprint, approve),
                        approve ? "批准桌面权限" : "拒绝桌面权限",
                        token).ConfigureAwait(false);
                    _pendingApprovals.TryRemove(approval.ApprovalId, out _);
                    _desktopApprovalId = null;
                    _desktopApprovalFingerprint = null;
                    await BroadcastEventAsync("approval.resolved", new
                    {
                        approvalId = approval.ApprovalId,
                        threadId = approval.ThreadId,
                        turnId = approval.TurnId,
                        epoch = approval.Epoch,
                        seq = approval.Sequence,
                        resolution = normalized.Resolution,
                    }).ConfigureAwait(false);
                    AddDiagnostic(approve
                        ? "Phone approved the currently verified Codex desktop permission control."
                        : "Phone declined the currently verified Codex desktop permission control.");
                }
                else
                {
                    await RequireAppServer().RespondToServerRequestAsync(approval.Request.Id, normalized.Upstream, token)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                approval.ResetResponse();
                throw;
            }

            return WireElement(new { accepted = true, approvalId });
        }, cancellationToken);
    }

    private async Task RunDesktopActionAsync(Action action, string actionName, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(action, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            AddDiagnostic($"{actionName}失败：{exception.Message}");
            throw new ProtocolException("INTERNAL", exception.Message);
        }
    }

    private Task<JsonElement> MarkTaskReadAsync(string deviceId, JsonElement parameters, CancellationToken cancellationToken)
    {
        RequireCurrentEpoch(parameters);
        var commandId = RequiredString(parameters, "clientCommandId");
        return RequireCommands().ExecuteAsync(deviceId, commandId, "task.read_ack", parameters, async token =>
        {
            var threadId = RequireManagedThread(parameters);
            var throughMessageId = RequiredString(parameters, "throughMessageId");
            await _messageGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var latestMessageId = await RequireRepository().GetLatestMessageIdAsync(threadId, token).ConfigureAwait(false);
                if (ReadAcknowledgementPolicy.CoversLatest(throughMessageId, latestMessageId))
                {
                    var snapshot = RequireState().MarkRead(threadId);
                    await RequireRepository().SaveTaskAsync(snapshot, token).ConfigureAwait(false);
                }
            }
            finally
            {
                _messageGate.Release();
            }

            return WireElement(new { accepted = true, threadId, throughMessageId });
        }, cancellationToken);
    }

    private async Task OnAppServerNotificationAsync(AppServerNotification notification)
    {
        PendingApproval? resolvedApproval = null;
        if (notification.Method == "serverRequest/resolved" &&
            notification.Parameters.TryGetProperty("requestId", out var requestId))
        {
            var requestKey = ServerRequestKey(requestId);
            if (_approvalIdsByRequest.TryRemove(requestKey, out var approvalId))
            {
                _pendingApprovals.TryRemove(approvalId, out resolvedApproval);
            }
        }

        _ = RequireState().ApplyNotification(notification.Method, notification.Parameters);
        foreach (var completedMessage in ExtractCompletedMessages(notification.Method, notification.Parameters))
        {
            await PersistCompletedMessageAsync(completedMessage).ConfigureAwait(false);
        }

        switch (notification.Method)
        {
            case "item/agentMessage/delta":
                await BroadcastEventAsync("task.message.delta", CreateMessageDelta(notification.Parameters)).ConfigureAwait(false);
                break;
            case "turn/plan/updated":
                await BroadcastEventAsync("task.plan.updated", CreatePlanUpdate(notification.Parameters)).ConfigureAwait(false);
                break;
            case "serverRequest/resolved" when resolvedApproval is not null:
                await BroadcastEventAsync("approval.resolved", new
                {
                    approvalId = resolvedApproval.ApprovalId,
                    threadId = resolvedApproval.ThreadId,
                    turnId = resolvedApproval.TurnId,
                    epoch = resolvedApproval.Epoch,
                    seq = resolvedApproval.Sequence,
                    resolution = resolvedApproval.Resolution ?? "resolved_elsewhere",
                }).ConfigureAwait(false);
                break;
            case "error":
                await BroadcastEventAsync("task.error", CreateTaskError(notification.Parameters)).ConfigureAwait(false);
                break;
            case "turn/completed" when IsFailedTurn(notification.Parameters):
                await BroadcastEventAsync("task.error", CreateTaskError(notification.Parameters)).ConfigureAwait(false);
                break;
        }
    }

    private async Task OnAppServerRequestAsync(AppServerRequest request)
    {
        if (!SupportedServerRequests.Contains(request.Method))
        {
            await RequireAppServer().RejectServerRequestAsync(
                request.Id,
                -32601,
                $"Bridge does not support App Server request '{request.Method}'.")
                .ConfigureAwait(false);
            return;
        }

        var threadId = OptionalString(request.Parameters, "threadId");
        var turnId = OptionalString(request.Parameters, "turnId");
        if (!string.IsNullOrWhiteSpace(threadId) && RequireState().Get(threadId) is not null)
        {
            RequireState().MarkNeedsInput(
                threadId,
                turnId,
                string.Equals(request.Method, "item/tool/requestUserInput", StringComparison.Ordinal));
        }

        await _eventGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var sequence = Interlocked.Increment(ref _eventSequence);
            var approvalId = OptionalString(request.Parameters, "approvalId") ?? $"{Epoch}-{sequence}";
            var approval = CreatePendingApproval(
                request,
                approvalId,
                threadId ?? string.Empty,
                turnId ?? string.Empty,
                Epoch,
                sequence);
            _pendingApprovals[approvalId] = approval;
            _approvalIdsByRequest[ServerRequestKey(request.Id)] = approvalId;
            await SendToReadyClientsAsync("approval.requested", new { approval = approval.ToWire() }, sequence)
                .ConfigureAwait(false);
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private void OnSnapshotChanged(BridgeTaskSnapshot snapshot)
    {
        if (!_stateChanges.Writer.TryWrite(snapshot))
        {
            AddDiagnostic("Task-state queue was unavailable; the next authoritative snapshot will recover mobile state.");
        }
        RuntimeChanged?.Invoke();
    }

    private async Task StateBroadcastLoopAsync()
    {
        try
        {
            await foreach (var snapshot in _stateChanges.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await PersistAndBroadcastSnapshotAsync(snapshot).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task PersistAndBroadcastSnapshotAsync(BridgeTaskSnapshot snapshot)
    {
        await RequireRepository().SaveTaskAsync(snapshot).ConfigureAwait(false);
        await BroadcastEventAsync("task.state", new
        {
            task = await CreateWireTaskAsync(snapshot, CancellationToken.None).ConfigureAwait(false),
        }).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<object>> CreateWireTasksAsync(CancellationToken cancellationToken)
    {
        var tasks = new List<object>();
        var snapshots = RequireState().GetAll();
        if (snapshots.Count > 6)
        {
            throw new InvalidOperationException("Managed task capacity invariant exceeded; refusing to hide tasks.");
        }

        foreach (var snapshot in snapshots)
        {
            tasks.Add(await CreateWireTaskAsync(snapshot, cancellationToken).ConfigureAwait(false));
        }

        return tasks;
    }

    private async Task<IReadOnlyList<object>> CreateWireSlotsAsync(CancellationToken cancellationToken)
    {
        var assignments = await RequireRepository().GetSlotAssignmentsAsync(cancellationToken).ConfigureAwait(false);
        var managedThreadIds = RequireState().GetAll().Select(task => task.ThreadId).ToHashSet(StringComparer.Ordinal);
        return Enumerable.Range(1, 6)
            .Select(slot => (object)new
            {
                slot,
                threadId = assignments.FirstOrDefault(assignment =>
                    assignment.Slot == slot && managedThreadIds.Contains(assignment.ThreadId))?.ThreadId,
            })
            .ToArray();
    }

    private async Task<object> CreateWireTaskAsync(BridgeTaskSnapshot snapshot, CancellationToken cancellationToken)
    {
        var lastMessagePreview = (await RequireRepository().GetMessagesAsync(snapshot.ThreadId, cancellationToken)
                .ConfigureAwait(false))
            .Where(message => string.Equals(message.Role, "assistant", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(message.Text))
            .OrderByDescending(message => message.CompletedAt)
            .Select(message => NonEmpty(message.Text.Trim(), message.Text.Trim(), 500))
            .FirstOrDefault();
        var turnId = snapshot.ActiveTurnId ?? snapshot.LastTurnId ?? snapshot.ThreadId;
        var plan = snapshot.Plan.Take(100).Select((step, index) => new
        {
            stepId = $"{turnId}-step-{index + 1}",
            text = NonEmpty(step.Text, $"Step {index + 1}", 500),
            status = step.Status switch
            {
                "completed" => "completed",
                "inProgress" or "in_progress" => "in_progress",
                _ => "pending",
            },
        }).ToArray();
        object progress = snapshot.Plan.Count > 0
            ? new
            {
                kind = "plan_steps",
                completedSteps = plan.Count(step => step.status == "completed"),
                totalSteps = plan.Length,
            }
            : snapshot.State == BridgeTaskState.Running
                ? new { kind = "indeterminate", label = "桌面 Codex 正在处理", source = "desktop_ui_status" }
                : new { kind = "unknown" };
        var status = snapshot.State switch
        {
            BridgeTaskState.Unassigned => "idle",
            BridgeTaskState.Idle => "idle",
            BridgeTaskState.Running => "running",
            BridgeTaskState.NeedsApproval => "waiting_approval",
            BridgeTaskState.NeedsReply => "waiting_input",
            BridgeTaskState.Completed => "completed",
            BridgeTaskState.Failed => "error",
            BridgeTaskState.Interrupted => "interrupted",
            BridgeTaskState.RecoveryUnknown => "recovery_unknown",
            _ => "recovery_unknown",
        };
        return new
        {
            threadId = snapshot.ThreadId,
            projectId = snapshot.ProjectId,
            title = NonEmpty(snapshot.Title, snapshot.ThreadId, 200),
            status,
            activeTurnId = snapshot.ActiveTurnId,
            attention = snapshot.IsUnread || snapshot.State is BridgeTaskState.NeedsApproval or BridgeTaskState.NeedsReply or BridgeTaskState.Failed or BridgeTaskState.RecoveryUnknown,
            progress,
            plan,
            lastMessagePreview,
            updatedAt = snapshot.UpdatedAt,
        };
    }

    private async Task BroadcastEventAsync(string eventName, object data)
    {
        await _eventGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var sequence = Interlocked.Increment(ref _eventSequence);
            await SendToReadyClientsAsync(eventName, data, sequence).ConfigureAwait(false);
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private Task SendToReadyClientsAsync(string eventName, object data, long sequence) =>
        Task.WhenAll(_clients.Values
            .Where(connection => connection.BusinessReady)
            .Select(connection => SendEventEnvelopeAsync(connection, eventName, data, sequence, CancellationToken.None)));

    private async Task SendEventEnvelopeAsync(
        ClientConnection connection,
        string eventName,
        object data,
        long sequence,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendEnvelopeAsync(
                connection,
                new { v = 1, epoch = Epoch, seq = sequence, @event = eventName, data },
                cancellationToken).ConfigureAwait(false);
        }
        catch (MobileEnvelopeSizeException exception)
        {
            connection.BusinessReady = false;
            AddDiagnostic($"Rejected oversized outbound {eventName} event ({exception.ActualBytes} bytes; limit {exception.MaximumBytes}).");
            if (connection.Socket.State == WebSocketState.Open)
            {
                await connection.Socket.CloseAsync(
                    WebSocketCloseStatus.MessageTooBig,
                    "Mobile envelopes are limited to 1 MiB",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            connection.BusinessReady = false;
        }
        catch (WebSocketException exception)
        {
            connection.BusinessReady = false;
            AddDiagnostic($"Outbound {eventName} event could not be delivered ({exception.WebSocketErrorCode}); the phone may reconnect safely.");
        }
    }

    private static Task SendErrorAsync(
        ClientConnection connection,
        string id,
        string code,
        string message,
        bool retryable,
        CancellationToken cancellationToken) =>
        SendEnvelopeAsync(connection, new
        {
            v = 1,
            id,
            error = new { code, message, retryable },
        }, cancellationToken);

    internal static JsonElement WireElement<T>(T value) =>
        JsonSerializer.SerializeToElement(value, WireJsonOptions);

    private async Task MakeSoleBusinessConnectionAsync(
        ClientConnection selected,
        CancellationToken cancellationToken)
    {
        foreach (var existing in _clients.Values.Where(candidate =>
                     candidate.Id != selected.Id && candidate.BusinessReady))
        {
            existing.BusinessReady = false;
            if (existing.Socket.State == WebSocketState.Open)
            {
                try
                {
                    await existing.Socket.CloseAsync(
                        WebSocketCloseStatus.PolicyViolation,
                        "Another authenticated phone became active",
                        cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                }
            }
        }
    }

    private static async Task SendEnvelopeAsync(
        ClientConnection connection,
        object envelope,
        CancellationToken cancellationToken)
    {
        if (connection.Socket.State != WebSocketState.Open)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, WireJsonOptions);
        MobileEnvelopeLimits.EnsureWithinLimit(bytes.Length);
        await connection.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            connection.SendLock.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new WebSocketException(WebSocketError.InvalidMessageType);
            }

            MobileEnvelopeLimits.EnsureWithinLimit(stream.Length + result.Count);
            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }
    }

    private string RequireManagedThread(JsonElement parameters)
    {
        var threadId = RequiredString(parameters, "threadId");
        if (RequireState().Get(threadId) is null)
        {
            throw new UnauthorizedAccessException("The thread is not managed by this bridge.");
        }

        return threadId;
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        OptionalString(element, propertyName) is { Length: > 0 } value
            ? value
            : throw new ProtocolException("INVALID_MESSAGE", $"String parameter '{propertyName}' is required.");

    private static string? OptionalString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? ReadNestedString(JsonElement element, string objectName, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(objectName, out var nested) &&
        nested.ValueKind == JsonValueKind.Object
            ? OptionalString(nested, propertyName)
            : null;

    private static string ServerRequestKey(JsonElement id) => $"{id.ValueKind}:{id.GetRawText()}";

    private static int RequiredInt32(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt32(out var value)
            ? value
            : throw new ProtocolException("INVALID_MESSAGE", $"Integer parameter '{propertyName}' is required.");

    private static long RequiredInt64(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.TryGetInt64(out var value)
            ? value
            : throw new ProtocolException("INVALID_MESSAGE", $"Integer parameter '{propertyName}' is required.");

    private void RequireCurrentEpoch(JsonElement parameters)
    {
        var requestedEpoch = OptionalString(parameters, "epoch");
        if (!string.Equals(requestedEpoch, Epoch, StringComparison.Ordinal))
        {
            throw new ProtocolException("STALE_EPOCH", "The command targets an earlier bridge or App Server generation.");
        }
    }

    private static bool TryOptionalInt32(JsonElement parameters, string propertyName, out int value)
    {
        value = default;
        return parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private static void RequireSlot(int slot)
    {
        if (slot is < 1 or > 6)
        {
            throw new ProtocolException("INVALID_MESSAGE", "slot must be between 1 and 6.");
        }
    }

    private async Task EnsureNewTaskCapacityAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        if (RequireState().GetAll().Count >= 6)
        {
            throw new ProtocolException("OVERLOADED", "This bridge already manages the maximum of six tasks.");
        }

        if (!TryOptionalInt32(parameters, "slot", out var requestedSlot))
        {
            return;
        }

        RequireSlot(requestedSlot);
        var assignments = await RequireRepository().GetSlotAssignmentsAsync(cancellationToken).ConfigureAwait(false);
        if (assignments.Any(assignment => assignment.Slot == requestedSlot))
        {
            throw new ProtocolException("OVERLOADED", "The requested task slot is already assigned.");
        }
    }

    private static bool IsFailedTurn(JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object &&
        parameters.TryGetProperty("turn", out var turn) &&
        OptionalString(turn, "status") is "failed";

    private static object CreateMessageDelta(JsonElement parameters)
    {
        var itemId = RequiredString(parameters, "itemId");
        return new
        {
            threadId = RequiredString(parameters, "threadId"),
            turnId = RequiredString(parameters, "turnId"),
            itemId,
            messageId = itemId,
            channel = "assistant",
            delta = RequiredString(parameters, "delta"),
        };
    }

    internal static IReadOnlyList<BridgeMessage> ExtractCompletedMessages(string method, JsonElement parameters)
    {
        var messages = new List<BridgeMessage>();
        if (method == "item/completed")
        {
            if (TryCreateCompletedMessage(parameters, out var message))
            {
                messages.Add(message);
            }

            return messages;
        }

        if (method != "turn/completed" ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("turn", out var turn) || turn.ValueKind != JsonValueKind.Object ||
            !turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        var threadId = OptionalString(parameters, "threadId");
        var turnId = OptionalString(turn, "id");
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return messages;
        }

        var completedAt = turn.TryGetProperty("completedAt", out var completed) && completed.TryGetInt64(out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.UtcNow;
        foreach (var item in items.EnumerateArray())
        {
            if (TryCreateCompletedMessage(threadId, turnId, item, completedAt, out var message))
            {
                messages.Add(message);
            }
        }

        return messages;
    }

    private async Task PersistCompletedMessageAsync(BridgeMessage message)
    {
        var isNew = false;
        await _messageGate.WaitAsync().ConfigureAwait(false);
        try
        {
            isNew = !await RequireRepository().MessageExistsAsync(message.MessageId).ConfigureAwait(false);
            await RequireRepository().SaveMessageAsync(message).ConfigureAwait(false);
        }
        finally
        {
            _messageGate.Release();
        }

        if (isNew)
        {
            // Deliver the complete message before the following compact task.state preview.
            // This prevents a 500-character preview from replacing the full phone response.
            await BroadcastEventAsync("task.message.completed", new { message = ToWireMessage(message) }).ConfigureAwait(false);
            if (string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            {
                RequireState().MarkMessageUnread(message.ThreadId);
            }
        }
    }

    private static bool TryCreateCompletedMessage(JsonElement parameters, out BridgeMessage message)
    {
        message = default!;
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("item", out var item))
        {
            return false;
        }

        var threadId = OptionalString(parameters, "threadId");
        var turnId = OptionalString(parameters, "turnId");
        if (string.IsNullOrWhiteSpace(threadId) || string.IsNullOrWhiteSpace(turnId))
        {
            return false;
        }

        var completedAt = parameters.TryGetProperty("completedAtMs", out var completed) && completed.TryGetInt64(out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : DateTimeOffset.UtcNow;
        return TryCreateCompletedMessage(threadId, turnId, item, completedAt, out message);
    }

    private static bool TryCreateCompletedMessage(
        string threadId,
        string turnId,
        JsonElement item,
        DateTimeOffset completedAt,
        out BridgeMessage message)
    {
        message = default!;
        if (item.ValueKind != JsonValueKind.Object || OptionalString(item, "type") != "agentMessage")
        {
            return false;
        }

        var itemId = OptionalString(item, "id");
        var text = OptionalString(item, "text");
        if (string.IsNullOrWhiteSpace(itemId) || text is null)
        {
            return false;
        }

        message = new BridgeMessage(
            itemId,
            threadId,
            turnId,
            itemId,
            "assistant",
            text,
            completedAt);
        return true;
    }

    private static object ToWireMessage(BridgeMessage message) => new
    {
        messageId = message.MessageId,
        threadId = message.ThreadId,
        turnId = message.TurnId,
        itemId = message.ItemId,
        role = message.Role,
        text = message.Text.Length <= 200_000 ? message.Text : message.Text[..200_000],
        completedAt = message.CompletedAt,
    };

    private static MessageWindow CreateTaskReadMessageWindow(IReadOnlyList<BridgeMessage> messages)
    {
        messages = CollapseDuplicateMessages(messages);
        var remainingBytes = TaskReadMessageBudgetBytes;
        var selected = new List<object>();
        var truncated = false;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            const int perMessageOverheadBytes = 512;
            if (remainingBytes <= perMessageOverheadBytes)
            {
                truncated = true;
                break;
            }

            var message = messages[index];
            var canonicalLength = Math.Min(message.Text.Length, 200_000);
            if (canonicalLength > 0 && canonicalLength < message.Text.Length &&
                char.IsHighSurrogate(message.Text[canonicalLength - 1]))
            {
                canonicalLength--;
            }

            var canonicalText = message.Text[..canonicalLength];
            var text = TruncateJsonString(canonicalText, remainingBytes - perMessageOverheadBytes);
            if (text.Length != message.Text.Length)
            {
                truncated = true;
            }

            selected.Insert(0, new
            {
                messageId = message.MessageId,
                threadId = message.ThreadId,
                turnId = message.TurnId,
                itemId = message.ItemId,
                role = message.Role,
                text,
                completedAt = message.CompletedAt,
            });
            remainingBytes -= GetEncodedJsonStringByteCount(text) + perMessageOverheadBytes;
        }

        if (selected.Count < messages.Count)
        {
            truncated = true;
        }

        return new MessageWindow(selected, truncated);
    }

    internal static IReadOnlyList<BridgeMessage> CollapseDuplicateMessages(IReadOnlyList<BridgeMessage> messages)
    {
        var selected = new List<BridgeMessage>(messages.Count);
        foreach (var message in messages.OrderBy(candidate => candidate.CompletedAt))
        {
            var duplicate = selected.LastOrDefault(existing =>
                string.Equals(existing.Role, message.Role, StringComparison.Ordinal) &&
                string.Equals(existing.Text.Trim(), message.Text.Trim(), StringComparison.Ordinal) &&
                Math.Abs((existing.CompletedAt - message.CompletedAt).TotalSeconds) <= 10);
            if (duplicate is null)
            {
                selected.Add(message);
            }
        }

        return selected;
    }

    private static string TruncateJsonString(string value, int maximumBytes)
    {
        if (GetEncodedJsonStringByteCount(value) <= maximumBytes)
        {
            return value;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maximumBytes));
        var usedBytes = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var text = rune.ToString();
            var encodedBytes = GetEncodedJsonStringByteCount(text);
            if (usedBytes + encodedBytes > maximumBytes)
            {
                break;
            }

            builder.Append(text);
            usedBytes += encodedBytes;
        }

        return builder.ToString();
    }

    private static int GetEncodedJsonStringByteCount(string value) =>
        JsonEncodedText.Encode(value, WireJsonOptions.Encoder ?? JavaScriptEncoder.Default).EncodedUtf8Bytes.Length;

    private static object CreatePlanUpdate(JsonElement parameters)
    {
        var turnId = RequiredString(parameters, "turnId");
        var steps = new List<object>();
        if (parameters.TryGetProperty("plan", out var plan) && plan.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var step in plan.EnumerateArray())
            {
                index++;
                var status = OptionalString(step, "status") switch
                {
                    "inProgress" => "in_progress",
                    "completed" => "completed",
                    _ => "pending",
                };
                steps.Add(new
                {
                    stepId = $"{turnId}-step-{index}",
                    text = NonEmpty(OptionalString(step, "step"), $"Step {index}", 500),
                    status,
                });
            }
        }

        return new
        {
            threadId = RequiredString(parameters, "threadId"),
            turnId,
            steps,
        };
    }

    private static object CreateTaskError(JsonElement parameters)
    {
        var threadId = OptionalString(parameters, "threadId") ?? "unknown-thread";
        var turnId = OptionalString(parameters, "turnId") ?? ReadNestedString(parameters, "turn", "id");
        return new
        {
            threadId,
            turnId,
            code = "INTERNAL",
            message = "Codex App Server reported a task error. See desktop diagnostics for local details.",
            recoverable = false,
        };
    }

    private PendingApproval CreatePendingApproval(
        AppServerRequest request,
        string approvalId,
        string threadId,
        string turnId,
        string epoch,
        long sequence)
    {
        var parameters = request.Parameters;
        var reason = NonEmpty(OptionalString(parameters, "reason"), "Codex requests confirmation.", 4000);
        var cwd = NonEmpty(
            OptionalString(parameters, "cwd") ?? RequireState().Get(threadId)?.WorkingDirectory,
            "Unknown working directory",
            4096);
        return request.Method switch
        {
            "item/commandExecution/requestApproval" => new PendingApproval(
                approvalId,
                threadId,
                turnId,
                epoch,
                sequence,
                "command",
                "Run command",
                reason,
                new
                {
                    type = "command",
                    command = NonEmpty(OptionalString(parameters, "command"), "Command details unavailable", 20_000),
                    cwd,
                    reason = AppendNetworkContext(parameters, reason),
                    allowedDecisions = new[] { "approve_once", "approve_session", "decline", "cancel" },
                },
                ["approve_once", "approve_session", "decline", "cancel"],
                new Dictionary<string, PermissionGrantPart>(StringComparer.Ordinal),
                request),
            "item/fileChange/requestApproval" => CreateFileChangeApproval(
                request,
                approvalId,
                threadId,
                turnId,
                epoch,
                sequence,
                reason),
            "item/permissions/requestApproval" => CreatePermissionApproval(
                request,
                approvalId,
                threadId,
                turnId,
                epoch,
                sequence,
                reason,
                cwd),
            "item/tool/requestUserInput" => CreateUserInputApproval(
                request,
                approvalId,
                threadId,
                turnId,
                epoch,
                sequence),
            _ => throw new InvalidOperationException($"Unsupported approval method '{request.Method}'."),
        };
    }

    private static PendingApproval CreateFileChangeApproval(
        AppServerRequest request,
        string approvalId,
        string threadId,
        string turnId,
        string epoch,
        long sequence,
        string reason)
    {
        var grantRoot = NonEmpty(OptionalString(request.Parameters, "grantRoot"), "Unavailable from App Server", 4096);
        return new PendingApproval(
            approvalId,
            threadId,
            turnId,
            epoch,
            sequence,
            "file_change",
            "Apply file changes",
            reason,
            new
            {
                type = "file_change",
                itemId = RequiredString(request.Parameters, "itemId"),
                paths = (string[]?)null,
                grantRoot,
                allowedDecisions = new[] { "approve_once", "approve_session", "decline", "cancel" },
            },
            ["approve_once", "approve_session", "decline", "cancel"],
            new Dictionary<string, PermissionGrantPart>(StringComparer.Ordinal),
            request);
    }

    private static PendingApproval CreatePermissionApproval(
        AppServerRequest request,
        string approvalId,
        string threadId,
        string turnId,
        string epoch,
        long sequence,
        string reason,
        string cwd)
    {
        var permissionParts = new Dictionary<string, PermissionGrantPart>(StringComparer.Ordinal);
        var filesystem = new List<object>();
        var networkId = $"network-{approvalId}";
        var networkEnabled = false;
        if (request.Parameters.TryGetProperty("permissions", out var permissions) &&
            permissions.ValueKind == JsonValueKind.Object)
        {
            if (permissions.TryGetProperty("fileSystem", out var fileSystem) && fileSystem.ValueKind == JsonValueKind.Object)
            {
                var index = 0;
                if (fileSystem.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var access = OptionalString(entry, "access");
                        if (access is not ("read" or "write") || !entry.TryGetProperty("path", out var path))
                        {
                            continue;
                        }

                        var displayPath = DescribeFileSystemPath(path);
                        if (string.IsNullOrWhiteSpace(displayPath))
                        {
                            continue;
                        }

                        var id = $"fs-{approvalId}-{++index}";
                        filesystem.Add(new { permissionId = id, path = NonEmpty(displayPath, "Unavailable", 4096), access });
                        permissionParts[id] = new PermissionGrantPart("entry", entry.Clone());
                    }
                }

                AddLegacyFilePermissions(fileSystem, filesystem, permissionParts, "read", "read", approvalId, ref index);
                AddLegacyFilePermissions(fileSystem, filesystem, permissionParts, "write", "write", approvalId, ref index);
            }

            if (permissions.TryGetProperty("network", out var network) && network.ValueKind == JsonValueKind.Object &&
                network.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                networkEnabled = enabled.GetBoolean();
                if (networkEnabled)
                {
                    permissionParts[networkId] = new PermissionGrantPart("network", network.Clone());
                }
            }
        }

        return new PendingApproval(
            approvalId,
            threadId,
            turnId,
            epoch,
            sequence,
            "permission",
            "Grant additional permissions",
            reason,
            new
            {
                type = "permission",
                cwd,
                requested = new
                {
                    filesystem,
                    network = new { permissionId = networkId, enabled = networkEnabled, targets = Array.Empty<object>() },
                },
                allowedScopes = new[] { "once", "session" },
            },
            [],
            permissionParts,
            request);
    }

    private static PendingApproval CreateUserInputApproval(
        AppServerRequest request,
        string approvalId,
        string threadId,
        string turnId,
        string epoch,
        long sequence)
    {
        var questions = new List<object>();
        if (request.Parameters.TryGetProperty("questions", out var upstreamQuestions) &&
            upstreamQuestions.ValueKind == JsonValueKind.Array)
        {
            foreach (var question in upstreamQuestions.EnumerateArray())
            {
                    var options = question.TryGetProperty("options", out var upstreamOptions) &&
                    upstreamOptions.ValueKind == JsonValueKind.Array
                        ? upstreamOptions.EnumerateArray()
                            .Select(option => OptionalString(option, "label"))
                            .Where(label => !string.IsNullOrWhiteSpace(label))
                            .Cast<string>()
                            .Take(20)
                            .Select(label => NonEmpty(label, "Option", 200))
                            .ToArray()
                        : Array.Empty<string>();
                questions.Add(new
                {
                    questionId = NonEmpty(OptionalString(question, "id"), $"question-{questions.Count + 1}", 128),
                    prompt = NonEmpty(OptionalString(question, "question"), "Input requested", 1000),
                    required = true,
                    options,
                });
                if (questions.Count == 20)
                {
                    break;
                }
            }
        }

        if (questions.Count == 0)
        {
            questions.Add(new
            {
                questionId = "question-1",
                prompt = "Input requested",
                required = true,
                options = Array.Empty<string>(),
            });
        }

        return new PendingApproval(
            approvalId,
            threadId,
            turnId,
            epoch,
            sequence,
            "user_input",
            "Codex needs your reply",
            "Answer the question to continue the task.",
            new { type = "user_input", questions },
            [],
            new Dictionary<string, PermissionGrantPart>(StringComparer.Ordinal),
            request);
    }

    private static NormalizedApprovalResponse NormalizeApprovalResponse(PendingApproval approval, JsonElement response)
    {
        var responseType = RequiredString(response, "type");
        if (!string.Equals(responseType, approval.ApprovalType, StringComparison.Ordinal))
        {
            throw new ProtocolException("DECISION_NOT_ALLOWED", "The tagged approval response type does not match the request.");
        }

        if (responseType is "command" or "file_change")
        {
            var decision = RequiredString(response, "decision");
            if (!approval.AllowedDecisions.Contains(decision, StringComparer.Ordinal))
            {
                throw new ProtocolException("DECISION_NOT_ALLOWED", "The selected approval decision was not offered.");
            }

            var upstreamDecision = decision switch
            {
                "approve_once" => "accept",
                "approve_session" => "acceptForSession",
                "decline" => "decline",
                "cancel" => "cancel",
                _ => throw new ProtocolException("DECISION_NOT_ALLOWED", "Unknown approval decision."),
            };
            var resolution = decision switch
            {
                "approve_once" or "approve_session" => "approved",
                "decline" => "declined",
                _ => "cancelled",
            };
            return new NormalizedApprovalResponse(new { decision = upstreamDecision }, resolution);
        }

        if (responseType == "permission")
        {
            var scope = RequiredString(response, "scope");
            if (scope is not ("once" or "session") ||
                !response.TryGetProperty("granted", out var grantedElement) ||
                grantedElement.ValueKind != JsonValueKind.Array)
            {
                throw new ProtocolException("DECISION_NOT_ALLOWED", "Permission responses require granted IDs and a valid scope.");
            }

            var granted = grantedElement.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
                .Where(item => item is not null)
                .Cast<string>()
                .ToArray();
            if (granted.Length != granted.Distinct(StringComparer.Ordinal).Count() ||
                granted.Any(id => !approval.PermissionParts.ContainsKey(id)))
            {
                throw new ProtocolException("DECISION_NOT_ALLOWED", "Every granted permission ID must be unique and offered by this approval.");
            }

            var selected = granted.Select(id => approval.PermissionParts[id]).ToArray();
            var entries = selected.Where(part => part.Kind == "entry").Select(part => part.Value).ToArray();
            var read = selected.Where(part => part.Kind == "read").Select(part => part.Value.GetString()!).ToArray();
            var write = selected.Where(part => part.Kind == "write").Select(part => part.Value.GetString()!).ToArray();
            object? fileSystem = entries.Length + read.Length + write.Length == 0
                ? null
                : new
                {
                    entries = entries.Length == 0 ? null : entries,
                    read = read.Length == 0 ? null : read,
                    write = write.Length == 0 ? null : write,
                };
            object? network = selected.FirstOrDefault(part => part.Kind == "network")?.Value;
            var permissions = new { fileSystem, network };
            return new NormalizedApprovalResponse(
                new { permissions, scope = scope == "session" ? "session" : "turn" },
                granted.Length > 0 ? "approved" : "declined");
        }

        if (!response.TryGetProperty("answers", out var answers) || answers.ValueKind != JsonValueKind.Object)
        {
            throw new ProtocolException("DECISION_NOT_ALLOWED", "User input responses require an answers object.");
        }

        var upstreamAnswers = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var answer in answers.EnumerateObject())
        {
            if (answer.Value.ValueKind != JsonValueKind.String)
            {
                throw new ProtocolException("DECISION_NOT_ALLOWED", "Every user-input answer must be a string.");
            }

            upstreamAnswers[answer.Name] = new { answers = new[] { answer.Value.GetString() ?? string.Empty } };
        }

        return new NormalizedApprovalResponse(new { answers = upstreamAnswers }, "submitted");
    }

    private static void AddLegacyFilePermissions(
        JsonElement upstreamFileSystem,
        ICollection<object> wire,
        IDictionary<string, PermissionGrantPart> parts,
        string propertyName,
        string access,
        string approvalId,
        ref int index)
    {
        if (!upstreamFileSystem.TryGetProperty(propertyName, out var paths) || paths.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
            {
                continue;
            }

            var id = $"fs-{approvalId}-{++index}";
            wire.Add(new { permissionId = id, path = NonEmpty(path.GetString(), "Unavailable", 4096), access });
            parts[id] = new PermissionGrantPart(propertyName, path.Clone());
        }
    }

    private static string? DescribeFileSystemPath(JsonElement path)
    {
        return OptionalString(path, "type") switch
        {
            "path" => OptionalString(path, "path"),
            "glob_pattern" => OptionalString(path, "pattern"),
            "special" => OptionalString(path, "value"),
            _ => null,
        };
    }

    private static string AppendNetworkContext(JsonElement parameters, string reason)
    {
        if (!parameters.TryGetProperty("networkApprovalContext", out var context) ||
            context.ValueKind != JsonValueKind.Object)
        {
            return reason;
        }

        var host = OptionalString(context, "host");
        var protocol = OptionalString(context, "protocol");
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(protocol))
        {
            return reason;
        }

        var suffix = $" Network target: {NonEmpty(host, "unavailable", 253)} via {NonEmpty(protocol, "unknown", 20)}.";
        return NonEmpty(reason + suffix, reason, 4000);
    }

    private static string NonEmpty(string? value, string fallback, int maxLength)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return selected.Length <= maxLength ? selected : selected[..maxLength];
    }

    private static bool IsWireId(string value) =>
        value.Length is >= 1 and <= 128 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private IReadOnlyList<ModelCatalogItem> GetModelCatalog()
    {
        lock (_catalogGate)
        {
            return _modelCatalog.ToArray();
        }
    }

    private static IReadOnlyList<ModelCatalogItem> NormalizeModelCatalog(JsonElement response)
    {
        var allowedEfforts = new HashSet<string>(
            ["none", "minimal", "low", "medium", "high", "xhigh"],
            StringComparer.Ordinal);
        var result = new List<ModelCatalogItem>();
        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var model in data.EnumerateArray())
        {
            if (model.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var id = OptionalString(model, "id") ?? OptionalString(model, "model");
            if (string.IsNullOrWhiteSpace(id) || !IsWireId(id))
            {
                continue;
            }

            var efforts = new HashSet<string>(StringComparer.Ordinal);
            if (model.TryGetProperty("supportedReasoningEfforts", out var supported) && supported.ValueKind == JsonValueKind.Array)
            {
                foreach (var option in supported.EnumerateArray())
                {
                    var effort = OptionalString(option, "reasoningEffort");
                    if (effort is not null && allowedEfforts.Contains(effort))
                    {
                        efforts.Add(effort);
                    }
                }
            }

            var defaultEffort = OptionalString(model, "defaultReasoningEffort");
            if (defaultEffort is not null && allowedEfforts.Contains(defaultEffort))
            {
                efforts.Add(defaultEffort);
            }

            if (efforts.Count == 0)
            {
                continue;
            }

            result.Add(new ModelCatalogItem(
                id,
                NonEmpty(OptionalString(model, "displayName"), id, 200),
                efforts.OrderBy(effort => effort, StringComparer.Ordinal).ToArray(),
                model.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True));
        }

        return result;
    }

    private ModelSelection ValidateModelSelection(JsonElement parameters)
    {
        var requestedModel = OptionalString(parameters, "model");
        var requestedEffort = OptionalString(parameters, "effort");
        if (requestedModel is null && requestedEffort is null)
        {
            return new ModelSelection(null, null);
        }

        var catalog = GetModelCatalog();
        var model = requestedModel is null
            ? catalog.FirstOrDefault(item => item.Default) ?? catalog.FirstOrDefault()
            : catalog.FirstOrDefault(item => string.Equals(item.Id, requestedModel, StringComparison.Ordinal));
        if (model is null)
        {
            throw new ProtocolException("MODEL_NOT_AVAILABLE", "The selected model is not in the current model catalog.");
        }

        if (requestedEffort is not null && !model.SupportedReasoningEfforts.Contains(requestedEffort, StringComparer.Ordinal))
        {
            throw new ProtocolException(
                "REASONING_EFFORT_NOT_SUPPORTED",
                "The selected reasoning effort is not supported by this model.");
        }

        return new ModelSelection(model.Id, requestedEffort);
    }

    private object CreateBridgeStatus() => new
    {
        status = IsDesktopCodexAvailable ? "online" : "degraded",
        reason = IsDesktopCodexAvailable ? null : DesktopStatusReason,
    };

    private static string BuildWssUrl(IPAddress address, int port) =>
        $"wss://{(address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString())}:{port}/v1/mobile";

    private void AddDiagnostic(string message)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss}] {message}";
        lock (_diagnosticGate)
        {
            _diagnostics.Add(line);
            if (_diagnostics.Count > 300)
            {
                _diagnostics.RemoveRange(0, _diagnostics.Count - 300);
            }
        }

        RuntimeChanged?.Invoke();
    }

    private void TrackBackground(Task task, string operation)
    {
        _ = task.ContinueWith(
            completed => AddDiagnostic($"Could not {operation} ({completed.Exception?.GetBaseException().GetType().Name ?? "unknown"}); details suppressed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private BridgeRepository RequireRepository() =>
        _repository ?? throw new InvalidOperationException("Bridge repository is not initialized.");

    private IdempotentCommandExecutor RequireCommands() =>
        _commands ?? throw new InvalidOperationException("Command executor is not initialized.");

    private ProjectAllowList RequireProjects() =>
        _projects ?? throw new InvalidOperationException("Project allow-list is not initialized.");

    private PairingService RequirePairing() =>
        _pairing ?? throw new InvalidOperationException("Pairing service is not initialized.");

    private BridgeStateStore RequireState() =>
        _state ?? throw new InvalidOperationException("State store is not initialized.");

    private AppServerClient RequireAppServer() =>
        _appServer is { IsRunning: true } client
            ? client
            : throw new InvalidOperationException("Codex App Server is unavailable. Check the desktop diagnostics.");

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _stateChanges.Writer.TryComplete();

        foreach (var connection in _clients.Values)
        {
            if (connection.Socket.State == WebSocketState.Open)
            {
                await connection.Socket.CloseAsync(
                    WebSocketCloseStatus.EndpointUnavailable,
                    "Bridge is shutting down",
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (_webApplication is not null)
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _webApplication.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            await _webApplication.DisposeAsync().ConfigureAwait(false);
        }

        _mdns?.Dispose();

        if (_appServer is not null)
        {
            await _appServer.DisposeAsync().ConfigureAwait(false);
        }

        _certificate?.Certificate.Dispose();
        _taskCapacityGate.Dispose();
        _messageGate.Dispose();
        _eventGate.Dispose();
        _restartGate.Dispose();
        _lifetime.Dispose();
    }

    private sealed class ClientConnection(Guid id, WebSocket socket)
    {
        public Guid Id { get; } = id;

        public WebSocket Socket { get; } = socket;

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public string? DeviceId { get; set; }

        public bool BusinessReady { get; set; }
    }

    private sealed class ProtocolException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }

    private sealed class PendingApproval(
        string approvalId,
        string threadId,
        string turnId,
        string epoch,
        long sequence,
        string approvalType,
        string title,
        string summary,
        object details,
        IReadOnlyList<string> allowedDecisions,
        IReadOnlyDictionary<string, PermissionGrantPart> permissionParts,
        AppServerRequest request)
    {
        private int _responseStarted;

        public string ApprovalId { get; } = approvalId;

        public string ThreadId { get; } = threadId;

        public string TurnId { get; } = turnId;

        public string Epoch { get; } = epoch;

        public long Sequence { get; } = sequence;

        public string ApprovalType { get; } = approvalType;

        public string Title { get; } = title;

        public string Summary { get; } = summary;

        public object Details { get; } = details;

        public IReadOnlyList<string> AllowedDecisions { get; } = allowedDecisions;

        public IReadOnlyDictionary<string, PermissionGrantPart> PermissionParts { get; } = permissionParts;

        public AppServerRequest Request { get; } = request;

        public string? Resolution { get; set; }

        public DateTimeOffset RequestedAt { get; } = DateTimeOffset.UtcNow;

        public bool TryBeginResponse() => Interlocked.CompareExchange(ref _responseStarted, 1, 0) == 0;

        public void ResetResponse() => Interlocked.Exchange(ref _responseStarted, 0);

        public object ToWire() => new
        {
            approvalId = ApprovalId,
            threadId = ThreadId,
            turnId = TurnId,
            epoch = Epoch,
            seq = Sequence,
            approvalType = ApprovalType,
            title = Title,
            summary = Summary,
            details = Details,
            requestedAt = RequestedAt,
        };
    }

    private sealed record NormalizedApprovalResponse(object Upstream, string Resolution);

    private sealed record MessageWindow(IReadOnlyList<object> Messages, bool Truncated);

    private sealed record PermissionGrantPart(string Kind, JsonElement Value);

    private sealed record ModelCatalogItem(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("displayName")] string DisplayName,
        [property: JsonPropertyName("supportedReasoningEfforts")] IReadOnlyList<string> SupportedReasoningEfforts,
        [property: JsonPropertyName("default")] bool Default);

    private sealed record ModelSelection(string? Model, string? Effort);
}
