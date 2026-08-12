using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexMicroBridge.Core.Persistence;

namespace CodexMicroBridge.Core.Security;

public sealed record PairingWindow(
    string Code,
    string ServerNonce,
    DateTimeOffset ExpiresAt,
    string CertificateFingerprint);

public sealed record PairingWindowInfo(
    string ServerNonce,
    DateTimeOffset ExpiresAt,
    string CertificateFingerprint);

public sealed record PairingProof(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("displayName")] string DisplayName,
    [property: JsonPropertyName("clientPublicKeySpki")] string ClientPublicKeySpki,
    [property: JsonPropertyName("clientNonce")] string ClientNonce,
    [property: JsonPropertyName("signatureDer")] string SignatureDer);

public sealed record AuthenticationChallenge(
    string ChallengeId,
    string DeviceId,
    string ServerNonce,
    DateTimeOffset ExpiresAt,
    string CertificateFingerprint);

public sealed partial class PairingService
{
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(60);
    private const int MaximumPairingFailuresPerWindow = 5;
    private readonly object _pairingGate = new();
    private readonly ConcurrentDictionary<string, AuthenticationChallenge> _authenticationChallenges = new(StringComparer.Ordinal);
    private readonly BridgeRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly string _fingerprint;
    private ActivePairingWindow? _activeWindow;

    public PairingService(BridgeRepository repository, string certificateFingerprint, TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _fingerprint = certificateFingerprint;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public PairingWindow OpenWindow()
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
        var nonce = RandomNumberGenerator.GetBytes(32);
        var expires = _timeProvider.GetUtcNow().Add(ChallengeLifetime);
        lock (_pairingGate)
        {
            _activeWindow = new ActivePairingWindow(SHA256.HashData(Encoding.ASCII.GetBytes(code)), nonce, expires);
        }

        return new PairingWindow(code, Base64UrlEncode(nonce), expires, _fingerprint);
    }

    public PairingWindowInfo? GetWindowInfo()
    {
        lock (_pairingGate)
        {
            if (_activeWindow is null || _activeWindow.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _activeWindow = null;
                return null;
            }

            return new PairingWindowInfo(
                Base64UrlEncode(_activeWindow.ServerNonce),
                _activeWindow.ExpiresAt,
                _fingerprint);
        }
    }

    public async Task<PairedDevice> CompletePairingAsync(PairingProof proof, CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(proof.DeviceId);
        if (string.IsNullOrWhiteSpace(proof.DisplayName) || proof.DisplayName.Length > 80)
        {
            throw new InvalidOperationException("The device display name must contain 1 to 80 characters.");
        }

        ActivePairingWindow window;
        lock (_pairingGate)
        {
            window = _activeWindow ?? throw new InvalidOperationException("Pairing is not open.");
            if (window.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _activeWindow = null;
                throw new InvalidOperationException("The pairing window expired.");
            }

            var suppliedCodeHash = SHA256.HashData(Encoding.ASCII.GetBytes(proof.Code));
            if (!CryptographicOperations.FixedTimeEquals(window.CodeHash, suppliedCodeHash))
            {
                window.FailedAttempts++;
                if (window.FailedAttempts >= MaximumPairingFailuresPerWindow)
                {
                    _activeWindow = null;
                }

                throw new UnauthorizedAccessException("The pairing code is invalid.");
            }
        }

        var publicKey = Convert.FromBase64String(proof.ClientPublicKeySpki);
        var clientNonce = Base64UrlDecode(proof.ClientNonce);
        var signature = Base64UrlDecode(proof.SignatureDer);
        if (clientNonce.Length < 16)
        {
            throw new InvalidOperationException("Client nonces must contain at least 128 bits.");
        }

        using (var verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(publicKey, out _);
            var payload = CreatePairingPayload(proof.DeviceId, window.ServerNonce, clientNonce, _fingerprint);
            if (!verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                throw new UnauthorizedAccessException("The pairing challenge signature is invalid.");
            }
        }

        lock (_pairingGate)
        {
            if (!ReferenceEquals(window, _activeWindow))
            {
                throw new InvalidOperationException("The pairing window was replaced.");
            }

            _activeWindow = null;
        }

        var now = _timeProvider.GetUtcNow();
        var existing = await _repository.GetPairedDeviceAsync(proof.DeviceId, cancellationToken).ConfigureAwait(false);
        var device = new PairedDevice(
            proof.DeviceId,
            proof.DisplayName,
            publicKey,
            existing?.AddedAt ?? now,
            now);
        await _repository.UpsertPairedDeviceAsync(device, cancellationToken).ConfigureAwait(false);
        return device;
    }

    public async Task<AuthenticationChallenge> BeginAuthenticationAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);
        _ = await _repository.GetPairedDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The device is not paired.");

        var now = _timeProvider.GetUtcNow();
        foreach (var existing in _authenticationChallenges.Where(item =>
                     item.Value.ExpiresAt <= now || string.Equals(item.Value.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            _authenticationChallenges.TryRemove(existing.Key, out _);
        }

        var challenge = new AuthenticationChallenge(
            Base64UrlEncode(RandomNumberGenerator.GetBytes(18)),
            deviceId,
            Base64UrlEncode(RandomNumberGenerator.GetBytes(32)),
            now.Add(ChallengeLifetime),
            _fingerprint);
        _authenticationChallenges[challenge.ChallengeId] = challenge;
        return challenge;
    }

    public async Task<PairedDevice> CompleteAuthenticationAsync(
        string challengeId,
        string signatureDer,
        CancellationToken cancellationToken = default)
    {
        if (!_authenticationChallenges.TryRemove(challengeId, out var challenge) ||
            challenge.ExpiresAt <= _timeProvider.GetUtcNow())
        {
            throw new UnauthorizedAccessException("The authentication challenge is missing or expired.");
        }

        var device = await _repository.GetPairedDeviceAsync(challenge.DeviceId, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The device is not paired.");
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(device.PublicKeySpki, out _);
        var payload = CreateAuthenticationPayload(challenge);
        if (!verifier.VerifyData(
                payload,
                Base64UrlDecode(signatureDer),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new UnauthorizedAccessException("The authentication signature is invalid.");
        }

        var authenticated = device with { LastSeenAt = _timeProvider.GetUtcNow() };
        await _repository.UpsertPairedDeviceAsync(authenticated, cancellationToken).ConfigureAwait(false);
        return authenticated;
    }

    public Task<IReadOnlyList<PairedDevice>> GetPairedDevicesAsync(CancellationToken cancellationToken = default) =>
        _repository.GetPairedDevicesAsync(cancellationToken);

    public async Task<bool> RevokeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        ValidateDeviceId(deviceId);
        foreach (var challenge in _authenticationChallenges.Where(item =>
                     string.Equals(item.Value.DeviceId, deviceId, StringComparison.Ordinal)))
        {
            _authenticationChallenges.TryRemove(challenge.Key, out _);
        }

        return await _repository.DeletePairedDeviceAsync(deviceId, cancellationToken).ConfigureAwait(false);
    }

    public static byte[] CreatePairingPayload(
        string deviceId,
        ReadOnlySpan<byte> serverNonce,
        ReadOnlySpan<byte> clientNonce,
        string fingerprint)
    {
        using var stream = new MemoryStream();
        WriteField(stream, "codex-micro-pair-v1"u8);
        WriteField(stream, Encoding.UTF8.GetBytes(deviceId));
        WriteField(stream, serverNonce);
        WriteField(stream, clientNonce);
        WriteField(stream, Encoding.ASCII.GetBytes(fingerprint));
        return stream.ToArray();
    }

    public static byte[] CreateAuthenticationPayload(AuthenticationChallenge challenge)
    {
        using var stream = new MemoryStream();
        WriteField(stream, "codex-micro-auth-v1"u8);
        WriteField(stream, Encoding.UTF8.GetBytes(challenge.ChallengeId));
        WriteField(stream, Encoding.UTF8.GetBytes(challenge.DeviceId));
        WriteField(stream, Base64UrlDecode(challenge.ServerNonce));
        WriteField(stream, Encoding.ASCII.GetBytes(challenge.CertificateFingerprint));
        return stream.ToArray();
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - (normalized.Length % 4)) % 4);
        return Convert.FromBase64String(normalized);
    }

    private static void WriteField(Stream stream, ReadOnlySpan<byte> bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void ValidateDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !DeviceIdPattern().IsMatch(deviceId))
        {
            throw new InvalidOperationException("Device IDs must be 3 to 80 URL-safe characters.");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,80}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdPattern();

    private sealed class ActivePairingWindow(byte[] codeHash, byte[] serverNonce, DateTimeOffset expiresAt)
    {
        public byte[] CodeHash { get; } = codeHash;

        public byte[] ServerNonce { get; } = serverNonce;

        public DateTimeOffset ExpiresAt { get; } = expiresAt;

        public int FailedAttempts { get; set; }
    }
}
