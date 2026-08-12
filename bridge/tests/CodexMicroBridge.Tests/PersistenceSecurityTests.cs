using System.Text.Json;
using System.Security.Cryptography;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using CodexMicroBridge.Core.Persistence;
using CodexMicroBridge.Core.Security;
using CodexMicroBridge.Core.State;
using Microsoft.Data.Sqlite;

namespace CodexMicroBridge.Tests;

public sealed class PersistenceSecurityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexMicroBridge.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BridgeCertificate_CompletesARealTlsServerHandshake()
    {
        var secrets = new DpapiSecretStore(Path.Combine(_directory, "tls"), $"CodexMicroBridge.Tls.Tests.{Guid.NewGuid():N}");
        using var certificate = new BridgeCertificateProvider(secrets).GetOrCreate("127.0.0.1").Certificate;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
                await using var tls = new SslStream(accepted.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, timeout.Token);
                return tls.IsAuthenticated && tls.IsEncrypted;
            }, timeout.Token);

            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await using var clientTls = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                (_, _, _, _) => true);
            await clientTls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, timeout.Token);

            Assert.True(clientTls.IsAuthenticated);
            Assert.True(await server);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Idempotency_ReplaysSameBody_AndRejectsDifferentBody()
    {
        var repository = await CreateRepositoryAsync(new PassthroughFieldProtector());
        var executor = new IdempotentCommandExecutor(repository);
        var executions = 0;
        var commandId = "command-1234567890";
        var firstBody = JsonSerializer.SerializeToElement(new
        {
            clientCommandId = commandId,
            epoch = "epoch-1234567890",
            text = "same",
        });

        var first = await executor.ExecuteAsync("device-one", commandId, "task.send", firstBody, _ =>
        {
            executions++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }));
        });
        var replay = await executor.ExecuteAsync("device-one", commandId, "task.send", firstBody, _ =>
        {
            executions++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = false }));
        });

        Assert.True(first.GetProperty("accepted").GetBoolean());
        Assert.True(replay.GetProperty("accepted").GetBoolean());
        Assert.Equal(1, executions);

        var differentBody = JsonSerializer.SerializeToElement(new
        {
            clientCommandId = commandId,
            epoch = "epoch-1234567890",
            text = "different",
        });
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => executor.ExecuteAsync(
            "device-one",
            commandId,
            "task.send",
            differentBody,
            _ => Task.FromResult(JsonSerializer.SerializeToElement(new { accepted = true }))));
    }

    [Fact]
    public async Task Slots_UseOneThroughSix_AndCanBeCleared()
    {
        var repository = await CreateRepositoryAsync(new PassthroughFieldProtector());
        await repository.AssignSlotAsync(1, "thread-one");
        await repository.AssignSlotAsync(6, "thread-six");
        var assigned = await repository.GetSlotAssignmentsAsync();
        Assert.Equal([1, 6], assigned.Select(item => item.Slot).ToArray());

        await repository.ClearSlotAsync(6);
        assigned = await repository.GetSlotAssignmentsAsync();
        Assert.Single(assigned);
        Assert.Equal(1, assigned[0].Slot);
    }

    [Fact]
    public async Task Dpapi_ProtectsSensitiveDatabaseColumns_AndReadsThemBack()
    {
        const string secretPath = @"C:\Users\example\secret-project";
        const string secretMessage = "private completed assistant message";
        var databasePath = Path.Combine(_directory, "bridge.db");
        var repository = await CreateRepositoryAsync(new DpapiFieldProtector("CodexMicroBridge.Database.Tests"));
        await repository.SaveTaskAsync(new BridgeTaskSnapshot
        {
            ThreadId = "thread-private",
            WorkingDirectory = secretPath,
            Title = "Private",
        });
        await repository.AddAllowedProjectAsync(new AllowedProject("project-private", secretPath, "Secret Project"));
        await repository.SaveMessageAsync(new BridgeMessage(
            "message-private",
            "thread-private",
            "turn-private",
            "item-private",
            "assistant",
            secretMessage,
            DateTimeOffset.UtcNow));

        var restored = Assert.Single(await repository.GetTasksAsync());
        Assert.Equal(secretPath, restored.WorkingDirectory);
        Assert.Equal(secretMessage, Assert.Single(await repository.GetMessagesAsync("thread-private")).Text);

        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        foreach (var query in new[]
                 {
                     "SELECT snapshot_json FROM managed_threads LIMIT 1",
                     "SELECT path FROM projects LIMIT 1",
                     "SELECT text FROM completed_messages LIMIT 1",
                 })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = query;
            var raw = Assert.IsType<string>(await command.ExecuteScalarAsync());
            Assert.StartsWith("dpapi:v1:", raw, StringComparison.Ordinal);
            Assert.DoesNotContain(secretPath, raw, StringComparison.Ordinal);
            Assert.DoesNotContain(secretMessage, raw, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RevokingDevice_RemovesCredentialsAndOutstandingChallenges()
    {
        var repository = await CreateRepositoryAsync(new PassthroughFieldProtector());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var device = new PairedDevice(
            "phone-one",
            "Pixel",
            key.ExportSubjectPublicKeyInfo(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await repository.UpsertPairedDeviceAsync(device);
        var pairing = new PairingService(repository, "test-spki-pin");
        var challenge = await pairing.BeginAuthenticationAsync(device.DeviceId);

        Assert.True(await pairing.RevokeDeviceAsync(device.DeviceId));
        Assert.Null(await repository.GetPairedDeviceAsync(device.DeviceId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            pairing.CompleteAuthenticationAsync(challenge.ChallengeId, "unused"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pairing.BeginAuthenticationAsync(device.DeviceId));
    }

    [Fact]
    public async Task PairingWindow_ClosesAfterFiveInvalidCodes()
    {
        var repository = await CreateRepositoryAsync(new PassthroughFieldProtector());
        var pairing = new PairingService(repository, "test-spki-pin");
        var window = pairing.OpenWindow();
        var wrongCode = window.Code == "000000" ? "999999" : "000000";
        var invalidProof = new PairingProof(
            wrongCode,
            "phone-one",
            "Pixel",
            "unused",
            "unused",
            "unused");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pairing.CompletePairingAsync(invalidProof));
        }

        Assert.Null(pairing.GetWindowInfo());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pairing.CompletePairingAsync(invalidProof with { Code = window.Code }));
    }

    [Fact]
    public async Task PairingAndReconnect_VerifyRealEcdsaProofs()
    {
        const string fingerprint = "test-spki-pin";
        var repository = await CreateRepositoryAsync(new PassthroughFieldProtector());
        var pairing = new PairingService(repository, fingerprint);
        using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var window = pairing.OpenWindow();
        var clientNonce = RandomNumberGenerator.GetBytes(32);
        var pairingPayload = PairingService.CreatePairingPayload(
            "phone-one",
            PairingService.Base64UrlDecode(window.ServerNonce),
            clientNonce,
            fingerprint);
        var pairingSignature = clientKey.SignData(
            pairingPayload,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var device = await pairing.CompletePairingAsync(new PairingProof(
            window.Code,
            "phone-one",
            "Pixel",
            Convert.ToBase64String(clientKey.ExportSubjectPublicKeyInfo()),
            PairingService.Base64UrlEncode(clientNonce),
            PairingService.Base64UrlEncode(pairingSignature)));

        Assert.Equal("phone-one", device.DeviceId);
        Assert.Null(pairing.GetWindowInfo());

        var challenge = await pairing.BeginAuthenticationAsync(device.DeviceId);
        var authenticationSignature = clientKey.SignData(
            PairingService.CreateAuthenticationPayload(challenge),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var authenticated = await pairing.CompleteAuthenticationAsync(
            challenge.ChallengeId,
            PairingService.Base64UrlEncode(authenticationSignature));

        Assert.Equal(device.DeviceId, authenticated.DeviceId);
        Assert.True(authenticated.LastSeenAt >= device.LastSeenAt);
    }

    [Fact]
    public async Task OldReadAck_DoesNotClearUnreadWhenANewerCompletedMessageAlreadyExists()
    {
        var repository = await CreateRepositoryAsync(new PassthroughFieldProtector());
        var state = new BridgeStateStore();
        state.Register("thread-one", "Test task");
        state.ApplyNotification("turn/completed", JsonSerializer.SerializeToElement(new
        {
            threadId = "thread-one",
            turn = new { id = "turn-one", status = "completed" },
        }));
        var completedAt = DateTimeOffset.UtcNow;
        await repository.SaveMessageAsync(new BridgeMessage(
            "message-old", "thread-one", "turn-one", "item-old", "assistant", "old", completedAt));
        await repository.SaveMessageAsync(new BridgeMessage(
            "message-new", "thread-one", "turn-one", "item-new", "assistant", "new", completedAt.AddSeconds(1)));

        var latestMessageId = await repository.GetLatestMessageIdAsync("thread-one");
        Assert.Equal("message-new", latestMessageId);
        Assert.False(ReadAcknowledgementPolicy.CoversLatest("message-old", latestMessageId));

        var afterStaleAck = Assert.IsType<BridgeTaskSnapshot>(state.Get("thread-one"));
        Assert.True(afterStaleAck.IsUnread);
        Assert.Equal(BridgeTaskState.Completed, afterStaleAck.State);

        Assert.True(ReadAcknowledgementPolicy.CoversLatest("message-new", latestMessageId));
        var afterCurrentAck = state.MarkRead("thread-one");
        Assert.False(afterCurrentAck.IsUnread);
        Assert.Equal(BridgeTaskState.Idle, afterCurrentAck.State);
    }

    private async Task<BridgeRepository> CreateRepositoryAsync(IFieldProtector protector)
    {
        Directory.CreateDirectory(_directory);
        var repository = new BridgeRepository(Path.Combine(_directory, "bridge.db"), protector);
        await repository.InitializeAsync();
        return repository;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
