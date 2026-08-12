using System.Text.Json;

namespace CodexMicroBridge.App;

internal sealed record ValidatedMobileRequest(string Id, string Operation, JsonElement Parameters);

internal sealed class MobileRequestValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal static class MobileRequestValidator
{
    private static readonly string[] EnvelopeFields = ["v", "id", "op", "params"];

    public static ValidatedMobileRequest Validate(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            ValidateClosedObject(root, EnvelopeFields, EnvelopeFields, "request");
            if (!root.GetProperty("v").TryGetInt32(out var version) || version != 1)
            {
                throw new MobileRequestValidationException("UNSUPPORTED_PROTOCOL", "Only protocol v=1 is supported.");
            }

            var id = RequireString(root, "id");
            if (!IsProtocolId(id))
            {
                throw Invalid("id is not a valid protocol identifier.");
            }

            var operation = RequireString(root, "op");
            var parameters = root.GetProperty("params");
            if (parameters.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("params must be an object.");
            }

            ValidateParameters(operation, parameters);
            ValidateCanonicalBounds(operation, parameters);
            return new ValidatedMobileRequest(id, operation, parameters.Clone());
        }
        catch (JsonException)
        {
            throw Invalid("The request is not valid JSON.");
        }
    }

    private static void ValidateParameters(string operation, JsonElement parameters)
    {
        switch (operation)
        {
            case "tasks.list":
            case "pairing.info":
                ValidateClosedObject(parameters, [], [], "params");
                return;
            case "task.create":
                ValidateClosedObject(parameters,
                    ["clientCommandId", "epoch", "projectId", "title", "prompt", "model", "effort", "slot"],
                    ["clientCommandId", "epoch", "projectId", "prompt"], "params");
                RequireStringFields(parameters, "clientCommandId", "epoch", "projectId", "prompt");
                OptionalStringFields(parameters, "title", "model", "effort");
                OptionalIntegerOrNull(parameters, "slot");
                ValidateWriteIdentity(parameters);
                return;
            case "task.read":
                ValidateClosedObject(parameters, ["threadId"], ["threadId"], "params");
                RequireStringFields(parameters, "threadId");
                return;
            case "task.send":
                ValidateClosedObject(parameters,
                    ["clientCommandId", "epoch", "threadId", "expectedTurnId", "model", "effort", "text"],
                    ["clientCommandId", "epoch", "threadId", "text"], "params");
                RequireStringFields(parameters, "clientCommandId", "epoch", "threadId", "text");
                OptionalStringFields(parameters, "expectedTurnId", "model", "effort");
                if (parameters.TryGetProperty("expectedTurnId", out _) &&
                    (parameters.TryGetProperty("model", out _) || parameters.TryGetProperty("effort", out _)))
                {
                    throw Invalid("An active-turn send cannot include model or effort overrides.");
                }
                ValidateWriteIdentity(parameters);
                return;
            case "task.interrupt":
                ValidateClosedObject(parameters,
                    ["clientCommandId", "epoch", "threadId", "turnId"],
                    ["clientCommandId", "epoch", "threadId", "turnId"], "params");
                RequireStringFields(parameters, "clientCommandId", "epoch", "threadId", "turnId");
                ValidateWriteIdentity(parameters);
                return;
            case "task.fork":
                ValidateClosedObject(parameters,
                    ["clientCommandId", "epoch", "threadId", "turnId", "slot"],
                    ["clientCommandId", "epoch", "threadId"], "params");
                RequireStringFields(parameters, "clientCommandId", "epoch", "threadId");
                OptionalStringOrNull(parameters, "turnId");
                OptionalIntegerOrNull(parameters, "slot");
                ValidateWriteIdentity(parameters);
                return;
            case "task.read_ack":
                ValidateClosedObject(parameters,
                    ["clientCommandId", "epoch", "threadId", "throughMessageId"],
                    ["clientCommandId", "epoch", "threadId", "throughMessageId"], "params");
                RequireStringFields(parameters, "clientCommandId", "epoch", "threadId", "throughMessageId");
                ValidateWriteIdentity(parameters);
                return;
            case "approval.respond":
                ValidateClosedObject(parameters,
                    ["clientCommandId", "approvalId", "threadId", "turnId", "epoch", "seq", "response"],
                    ["clientCommandId", "approvalId", "threadId", "turnId", "epoch", "seq", "response"], "params");
                RequireStringFields(parameters, "clientCommandId", "approvalId", "threadId", "turnId", "epoch");
                RequireInteger(parameters, "seq");
                ValidateApprovalResponse(parameters.GetProperty("response"));
                ValidateWriteIdentity(parameters);
                return;
            case "slot.assign":
                ValidateClosedObject(parameters,
                    ["clientCommandId", "epoch", "slot", "threadId"],
                    ["clientCommandId", "epoch", "slot", "threadId"], "params");
                RequireStringFields(parameters, "clientCommandId", "epoch");
                RequireInteger(parameters, "slot");
                StringOrNull(parameters, "threadId");
                ValidateWriteIdentity(parameters);
                return;
            case "pairing.complete":
                ValidateClosedObject(parameters,
                    ["code", "deviceId", "displayName", "clientPublicKeySpki", "clientNonce", "signatureDer"],
                    ["code", "deviceId", "displayName", "clientPublicKeySpki", "clientNonce", "signatureDer"], "params");
                RequireStringFields(parameters, "code", "deviceId", "displayName", "clientPublicKeySpki", "clientNonce", "signatureDer");
                return;
            case "auth.challenge":
                ValidateClosedObject(parameters, ["deviceId"], ["deviceId"], "params");
                RequireStringFields(parameters, "deviceId");
                return;
            case "auth.complete":
                ValidateClosedObject(parameters, ["challengeId", "signatureDer"], ["challengeId", "signatureDer"], "params");
                RequireStringFields(parameters, "challengeId", "signatureDer");
                return;
            default:
                throw Invalid("The requested operation is not supported.");
        }
    }

    private static void ValidateApprovalResponse(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("approval response must be an object.");
        }

        var type = RequireString(response, "type");
        switch (type)
        {
            case "command":
            case "file_change":
                ValidateClosedObject(response, ["type", "decision"], ["type", "decision"], "response");
                RequireStringFields(response, "decision");
                return;
            case "permission":
                ValidateClosedObject(response, ["type", "granted", "scope"], ["type", "granted", "scope"], "response");
                RequireStringFields(response, "scope");
                var granted = response.GetProperty("granted");
                if (granted.ValueKind != JsonValueKind.Array || granted.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                {
                    throw Invalid("permission granted must be an array of identifiers.");
                }
                return;
            case "user_input":
                ValidateClosedObject(response, ["type", "answers"], ["type", "answers"], "response");
                var answers = response.GetProperty("answers");
                if (answers.ValueKind != JsonValueKind.Object || !answers.EnumerateObject().Any() ||
                    answers.EnumerateObject().Any(answer => answer.Value.ValueKind != JsonValueKind.String))
                {
                    throw Invalid("user_input answers must be a non-empty object of strings.");
                }
                return;
            default:
                throw Invalid("Unknown tagged approval response type.");
        }
    }

    private static void ValidateCanonicalBounds(string operation, JsonElement parameters)
    {
        switch (operation)
        {
            case "tasks.list":
            case "pairing.info":
                return;
            case "task.create":
                ValidateIdField(parameters, "projectId");
                ValidateLength(parameters, "prompt", 100_000);
                ValidateOptionalLength(parameters, "title", 200);
                ValidateOptionalId(parameters, "model");
                ValidateOptionalEffort(parameters);
                ValidateOptionalSlot(parameters);
                return;
            case "task.read":
                ValidateIdField(parameters, "threadId");
                return;
            case "task.send":
                ValidateIdField(parameters, "threadId");
                ValidateLength(parameters, "text", 100_000);
                ValidateOptionalId(parameters, "expectedTurnId");
                ValidateOptionalId(parameters, "model");
                ValidateOptionalEffort(parameters);
                return;
            case "task.interrupt":
                ValidateIdField(parameters, "threadId");
                ValidateIdField(parameters, "turnId");
                return;
            case "task.fork":
                ValidateIdField(parameters, "threadId");
                ValidateOptionalId(parameters, "turnId", allowNull: true);
                ValidateOptionalSlot(parameters);
                return;
            case "task.read_ack":
                ValidateIdField(parameters, "threadId");
                ValidateIdField(parameters, "throughMessageId");
                return;
            case "approval.respond":
                ValidateIdField(parameters, "approvalId");
                ValidateIdField(parameters, "threadId");
                ValidateIdField(parameters, "turnId");
                if (parameters.GetProperty("seq").GetInt64() < 1)
                {
                    throw Invalid("approval seq must be at least 1.");
                }
                ValidateApprovalBounds(parameters.GetProperty("response"));
                return;
            case "slot.assign":
                ValidateSlot(parameters.GetProperty("slot"));
                ValidateOptionalId(parameters, "threadId", allowNull: true);
                return;
            case "pairing.complete":
                var code = parameters.GetProperty("code").GetString()!;
                if (code.Length != 6 || code.Any(character => !char.IsAsciiDigit(character)))
                {
                    throw Invalid("pairing code must contain exactly six digits.");
                }
                ValidateLength(parameters, "deviceId", 80);
                ValidateLength(parameters, "displayName", 80);
                return;
            case "auth.challenge":
                ValidateLength(parameters, "deviceId", 80);
                return;
            case "auth.complete":
                ValidateLength(parameters, "challengeId", 128);
                return;
        }
    }

    private static void ValidateApprovalBounds(JsonElement response)
    {
        var type = response.GetProperty("type").GetString();
        if (type is "command" or "file_change")
        {
            var decision = response.GetProperty("decision").GetString();
            if (decision is not ("approve_once" or "approve_session" or "decline" or "cancel"))
            {
                throw Invalid("approval decision is not a canonical value.");
            }
            return;
        }

        if (type == "permission")
        {
            var scope = response.GetProperty("scope").GetString();
            if (scope is not ("once" or "session"))
            {
                throw Invalid("permission scope must be once or session.");
            }

            var granted = response.GetProperty("granted").EnumerateArray().Select(item => item.GetString()!).ToArray();
            if (granted.Length != granted.Distinct(StringComparer.Ordinal).Count() || granted.Any(id => !IsProtocolId(id)))
            {
                throw Invalid("granted permission IDs must be unique protocol identifiers.");
            }
            return;
        }

        foreach (var answer in response.GetProperty("answers").EnumerateObject())
        {
            if (!IsProtocolId(answer.Name) || (answer.Value.GetString()?.Length ?? 0) > 10_000)
            {
                throw Invalid("user-input answer keys or values exceed canonical bounds.");
            }
        }
    }

    private static void ValidateLength(JsonElement element, string name, int maximum)
    {
        var value = element.GetProperty(name).GetString()!;
        if (value.Length > maximum)
        {
            throw Invalid($"{name} exceeds the canonical length limit of {maximum}.");
        }
    }

    private static void ValidateOptionalLength(JsonElement element, string name, int maximum)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length > maximum)
        {
            throw Invalid($"{name} exceeds the canonical length limit of {maximum}.");
        }
    }

    private static void ValidateIdField(JsonElement element, string name)
    {
        if (!IsProtocolId(element.GetProperty(name).GetString()!))
        {
            throw Invalid($"{name} is not a canonical protocol identifier.");
        }
    }

    private static void ValidateOptionalId(JsonElement element, string name, bool allowNull = false)
    {
        if (!element.TryGetProperty(name, out var value) || (allowNull && value.ValueKind == JsonValueKind.Null))
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String || !IsProtocolId(value.GetString()!))
        {
            throw Invalid($"{name} is not a canonical protocol identifier.");
        }
    }

    private static void ValidateOptionalEffort(JsonElement element)
    {
        if (element.TryGetProperty("effort", out var effort) &&
            effort.GetString() is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh"))
        {
            throw Invalid("effort is not a canonical reasoning level.");
        }
    }

    private static void ValidateOptionalSlot(JsonElement element)
    {
        if (element.TryGetProperty("slot", out var slot) && slot.ValueKind != JsonValueKind.Null)
        {
            ValidateSlot(slot);
        }
    }

    private static void ValidateSlot(JsonElement slot)
    {
        if (!slot.TryGetInt32(out var value) || value is < 1 or > 6)
        {
            throw Invalid("slot must be an integer from 1 through 6.");
        }
    }

    private static void ValidateClosedObject(
        JsonElement element,
        string[] allowed,
        string[] required,
        string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Invalid($"{name} must be an object.");
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw Invalid($"Unknown {name} field '{property.Name}'.");
            }
        }

        foreach (var property in required)
        {
            if (!element.TryGetProperty(property, out _))
            {
                throw Invalid($"Missing required {name} field '{property}'.");
            }
        }
    }

    private static void RequireStringFields(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            _ = RequireString(element, name);
        }
    }

    private static void OptionalStringFields(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
            {
                throw Invalid($"{name} must be a non-empty string.");
            }
        }
    }

    private static string RequireString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"{name} must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static void OptionalStringOrNull(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null &&
            (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())))
        {
            throw Invalid($"{name} must be a non-empty string or null.");
        }
    }

    private static void StringOrNull(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
            (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
        {
            throw Invalid($"{name} must be a non-empty string or null.");
        }
    }

    private static void OptionalIntegerOrNull(JsonElement element, string name)
    {
        if (element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null &&
            (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
        {
            throw Invalid($"{name} must be an integer or null.");
        }
    }

    private static void RequireInteger(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
        {
            throw Invalid($"{name} must be an integer.");
        }
    }

    private static void ValidateWriteIdentity(JsonElement parameters)
    {
        var commandId = parameters.GetProperty("clientCommandId").GetString()!;
        if (commandId.Length is < 16 or > 128 || !IsProtocolId(commandId))
        {
            throw Invalid("clientCommandId is not a valid 16-128 character identifier.");
        }

        var epoch = parameters.GetProperty("epoch").GetString()!;
        if (epoch.Length is < 16 or > 128 || epoch.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')))
        {
            throw Invalid("epoch is not a valid protocol epoch.");
        }
    }

    private static bool IsProtocolId(string value) =>
        value.Length is >= 1 and <= 128 && char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static MobileRequestValidationException Invalid(string message) =>
        new("INVALID_MESSAGE", message);
}
