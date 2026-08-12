using System.Text.Json;
using CodexMicroBridge.App;
using CodexMicroBridge.Core.Security;

namespace CodexMicroBridge.Tests;

public sealed class ProtocolInteropTests
{
    private const string CommandId = "command-1234567890";
    private const string Epoch = "epoch-1234567890";

    [Fact]
    public void ClosedObjectValidator_RejectsUnknownTopLevelAndParamsFields()
    {
        var valid = MobileRequestValidator.Validate("""
            {"v":1,"id":"request-1","op":"tasks.list","params":{}}
            """);
        Assert.Equal("tasks.list", valid.Operation);

        Assert.Throws<MobileRequestValidationException>(() => MobileRequestValidator.Validate("""
            {"v":1,"id":"request-1","op":"tasks.list","params":{},"extra":true}
            """));
        Assert.Throws<MobileRequestValidationException>(() => MobileRequestValidator.Validate("""
            {"v":1,"id":"request-1","op":"task.read","params":{"threadId":"thread-1","cwd":"C:\\forbidden"}}
            """));
    }

    [Fact]
    public void PairingProof_DeserializesAndroidLowerCamelJson()
    {
        var proof = JsonSerializer.Deserialize<PairingProof>("""
            {
              "code":"123456",
              "deviceId":"phone-one",
              "displayName":"Pixel",
              "clientPublicKeySpki":"AQID",
              "clientNonce":"nonce-value",
              "signatureDer":"signature-value"
            }
            """);

        Assert.NotNull(proof);
        Assert.Equal("phone-one", proof.DeviceId);
        Assert.Equal("AQID", proof.ClientPublicKeySpki);
    }

    [Fact]
    public void SnapshotWireJson_UsesLowerCamelForNestedModelCatalogRecords()
    {
        var snapshot = BridgeRuntime.WireElement(new
        {
            modelCatalog = new[]
            {
                new ModelCatalogProbe("gpt-5", "GPT-5", ["low", "high"], true),
            },
        });
        var model = snapshot.GetProperty("modelCatalog")[0];

        Assert.Equal("gpt-5", model.GetProperty("id").GetString());
        Assert.Equal("GPT-5", model.GetProperty("displayName").GetString());
        Assert.Equal(2, model.GetProperty("supportedReasoningEfforts").GetArrayLength());
        Assert.True(model.GetProperty("default").GetBoolean());
        Assert.False(model.TryGetProperty("Id", out _));
        Assert.False(model.TryGetProperty("DisplayName", out _));
    }

    [Fact]
    public void MobileEnvelopeLimit_IsExactlyOneMiB()
    {
        Assert.Equal(1024 * 1024, MobileEnvelopeLimits.MaximumBytes);
        MobileEnvelopeLimits.EnsureWithinLimit(MobileEnvelopeLimits.MaximumBytes);
        var exception = Assert.Throws<MobileEnvelopeSizeException>(() =>
            MobileEnvelopeLimits.EnsureWithinLimit(MobileEnvelopeLimits.MaximumBytes + 1L));
        Assert.Equal(MobileEnvelopeLimits.MaximumBytes + 1L, exception.ActualBytes);
    }

    [Theory]
    [InlineData("{\"v\":1,\"id\":\"request-1\",\"op\":\"slot.assign\",\"params\":{\"clientCommandId\":\"command-1234567890\",\"epoch\":\"epoch-1234567890\",\"slot\":7,\"threadId\":null}}")]
    [InlineData("{\"v\":1,\"id\":\"request-1\",\"op\":\"approval.respond\",\"params\":{\"clientCommandId\":\"command-1234567890\",\"approvalId\":\"approval-1\",\"threadId\":\"thread-1\",\"turnId\":\"turn-1\",\"epoch\":\"epoch-1234567890\",\"seq\":0,\"response\":{\"type\":\"command\",\"decision\":\"approve_forever\"}}}")]
    [InlineData("{\"v\":1,\"id\":\"request-1\",\"op\":\"task.create\",\"params\":{\"clientCommandId\":\"command-1234567890\",\"epoch\":\"epoch-1234567890\",\"projectId\":\"project-1\",\"prompt\":\"go\",\"effort\":\"turbo\"}}")]
    public void CanonicalBounds_RejectInvalidFixtures(string json)
    {
        Assert.Throws<MobileRequestValidationException>(() => MobileRequestValidator.Validate(json));
    }

    [Theory]
    [MemberData(nameof(CanonicalInvalidRequests))]
    public void CanonicalValidator_RejectsEveryExplicitRequestBoundary(string reason, string json)
    {
        var exception = Assert.Throws<MobileRequestValidationException>(() => MobileRequestValidator.Validate(json));

        Assert.Equal("INVALID_MESSAGE", exception.Code);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void CanonicalValidator_AcceptsAllSharedRequestFixtures()
    {
        var fixtureDirectory = Path.Combine(FindRepositoryRoot(), "shared", "protocol-v1", "fixtures");
        var requestFixtures = Directory.GetFiles(fixtureDirectory, "request-*.json", SearchOption.TopDirectoryOnly);

        Assert.NotEmpty(requestFixtures);
        foreach (var fixture in requestFixtures)
        {
            var request = MobileRequestValidator.Validate(File.ReadAllText(fixture));
            Assert.StartsWith("request-", Path.GetFileNameWithoutExtension(fixture), StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(request.Operation));
        }
    }

    public static IEnumerable<object[]> CanonicalInvalidRequests()
    {
        yield return Invalid("slot below minimum", "slot.assign", new
        {
            clientCommandId = CommandId,
            epoch = Epoch,
            slot = 0,
            threadId = (string?)null,
        });
        yield return Invalid("slot above maximum", "slot.assign", new
        {
            clientCommandId = CommandId,
            epoch = Epoch,
            slot = 7,
            threadId = (string?)null,
        });
        yield return Invalid("approval sequence below minimum", "approval.respond", new
        {
            clientCommandId = CommandId,
            approvalId = "approval-1",
            threadId = "thread-1",
            turnId = "turn-1",
            epoch = Epoch,
            seq = 0,
            response = new { type = "command", decision = "approve_once" },
        });
        yield return Invalid("prompt exceeds maxLength", "task.create", new
        {
            clientCommandId = CommandId,
            epoch = Epoch,
            projectId = "project-1",
            prompt = new string('p', 100_001),
        });
        yield return Invalid("title exceeds maxLength", "task.create", new
        {
            clientCommandId = CommandId,
            epoch = Epoch,
            projectId = "project-1",
            prompt = "go",
            title = new string('t', 201),
        });
        yield return Invalid("text exceeds maxLength", "task.send", new
        {
            clientCommandId = CommandId,
            epoch = Epoch,
            threadId = "thread-1",
            text = new string('m', 100_001),
        });
        yield return Invalid("non-canonical reasoning effort", "task.create", new
        {
            clientCommandId = CommandId,
            epoch = Epoch,
            projectId = "project-1",
            prompt = "go",
            effort = "turbo",
        });
        yield return Invalid("active steer carries model override", "task.send", new
        {
            clientCommandId = CommandId,
            epoch = Epoch,
            threadId = "thread-1",
            expectedTurnId = "turn-1",
            model = "gpt-5",
            text = "continue",
        });
        yield return Invalid("non-canonical approval decision", "approval.respond", new
        {
            clientCommandId = CommandId,
            approvalId = "approval-1",
            threadId = "thread-1",
            turnId = "turn-1",
            epoch = Epoch,
            seq = 1,
            response = new { type = "file_change", decision = "approve_forever" },
        });
        yield return Invalid("non-canonical permission scope", "approval.respond", new
        {
            clientCommandId = CommandId,
            approvalId = "approval-1",
            threadId = "thread-1",
            turnId = "turn-1",
            epoch = Epoch,
            seq = 1,
            response = new { type = "permission", granted = Array.Empty<string>(), scope = "forever" },
        });
        yield return Invalid("duplicate permission IDs", "approval.respond", new
        {
            clientCommandId = CommandId,
            approvalId = "approval-1",
            threadId = "thread-1",
            turnId = "turn-1",
            epoch = Epoch,
            seq = 1,
            response = new { type = "permission", granted = new[] { "permission-1", "permission-1" }, scope = "once" },
        });
        yield return Invalid("empty user-input answers", "approval.respond", new
        {
            clientCommandId = CommandId,
            approvalId = "approval-1",
            threadId = "thread-1",
            turnId = "turn-1",
            epoch = Epoch,
            seq = 1,
            response = new { type = "user_input", answers = new Dictionary<string, string>() },
        });
        yield return Invalid("user-input answer exceeds maxLength", "approval.respond", new
        {
            clientCommandId = CommandId,
            approvalId = "approval-1",
            threadId = "thread-1",
            turnId = "turn-1",
            epoch = Epoch,
            seq = 1,
            response = new
            {
                type = "user_input",
                answers = new Dictionary<string, string> { ["question-1"] = new string('a', 10_001) },
            },
        });
        yield return Invalid("invalid protocol identifier", "task.read", new { threadId = "thread/1" });
        yield return Invalid("short client command ID", "task.read_ack", new
        {
            clientCommandId = "too-short",
            epoch = Epoch,
            threadId = "thread-1",
            throughMessageId = "message-1",
        });
        yield return Invalid("short epoch", "task.interrupt", new
        {
            clientCommandId = CommandId,
            epoch = "short",
            threadId = "thread-1",
            turnId = "turn-1",
        });
    }

    private static object[] Invalid(string reason, string operation, object parameters) =>
        [reason, JsonSerializer.Serialize(new { v = 1, id = "request-1", op = operation, @params = parameters })];

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

    private sealed record ModelCatalogProbe(
        string Id,
        string DisplayName,
        IReadOnlyList<string> SupportedReasoningEfforts,
        bool Default);
}
