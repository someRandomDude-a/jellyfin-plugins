using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccountHub.Security;

[JsonConverter(typeof(JsonStringEnumConverter<StepUpLevel>))]
public enum StepUpLevel { Off, Destructive, AllConfigChanges, Everything }
public enum CommandRisk { Destructive = 1, Configuration = 2, Audit = 3, Operational = 4 }

public sealed record SecuritySettings(
    [property: JsonPropertyName("stepUpLevelMirror")] StepUpLevel StepUpLevelMirror = StepUpLevel.Destructive,
    [property: JsonPropertyName("alertMode")] string AlertMode = "native",
    [property: JsonPropertyName("passwordLoginPolicy")] string PasswordLoginPolicy = "auto")
{
    public string StepUpLevelSource => "manual";
    public const string MirrorWarning = "Must match Jellyfin Security's step-up level; not auto-detected";
    public void Validate()
    {
        if (!Enum.IsDefined(StepUpLevelMirror) || AlertMode is not ("native" or "hub" or "both")
            || PasswordLoginPolicy is not ("auto" or "local_disabled" or "allow_local"))
            throw new SecurityFailure("invalid_request", 400);
    }
}

// Not a record: generated ToString must never disclose proofs or session tokens.
public sealed class StepUpProof : IDisposable
{
    [JsonPropertyName("code")] public string? Code { get; set; }
    [JsonPropertyName("stepUpToken")] public string? StepUpToken { get; set; }
    public void Dispose() { Code = null; StepUpToken = null; }
    public override string ToString() => "[redacted proof]";
}

public sealed record UserStatus(
    [property: JsonPropertyName("userId")] Guid? UserId,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("totpEnabled")] bool? TotpEnabled,
    [property: JsonPropertyName("emailOtpEnabled")] bool? EmailOtpEnabled,
    [property: JsonPropertyName("passkeyCount")] int? PasskeyCount,
    [property: JsonPropertyName("trustedDeviceCount")] int? TrustedDeviceCount,
    [property: JsonPropertyName("recoveryCodesRemaining")] int? RecoveryCodesRemaining,
    [property: JsonPropertyName("isLockedOut")] bool? IsLockedOut)
{
    public bool? HasFactor => TotpEnabled == true || EmailOtpEnabled == true || PasskeyCount > 0 || RecoveryCodesRemaining > 0 ? true
        : TotpEnabled == false && EmailOtpEnabled == false && PasskeyCount == 0 && RecoveryCodesRemaining == 0 ? false : null;
}

public sealed record TrustedDevice(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("username")] string? Username,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("deviceName")] string? DeviceName,
    [property: JsonPropertyName("createdAt")] DateTimeOffset? CreatedAt,
    [property: JsonPropertyName("lastUsedAt")] DateTimeOffset? LastUsedAt);

public sealed record UserSummary(
    [property: JsonPropertyName("userId")] Guid? UserId,
    [property: JsonPropertyName("devices")] SummaryDevices? Devices);
public sealed record SummaryDevices(
    [property: JsonPropertyName("trusted")] SummaryDevice[]? Trusted);
public sealed record SummaryDevice(
    [property: JsonPropertyName("deviceName")] string? DeviceName,
    [property: JsonPropertyName("lastUsedAt")] DateTimeOffset? LastUsedAt);

public sealed class SecurityFailure(string code, int status, bool retryable = false, int? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public bool Retryable { get; } = retryable;
    public int? RetryAfter { get; } = retryAfter;
}

public static class SecurityJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JellyfinGuidConverter() }
    };

    // Jellyfin's native API emits compact GUIDs, while Hub's API uses standard dashed GUIDs.
    private sealed class JellyfinGuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && reader.GetString() is { } value &&
                (Guid.TryParseExact(value, "N", out var id) || Guid.TryParseExact(value, "D", out id))) return id;
            throw new JsonException("Invalid Jellyfin identity");
        }
        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString("D"));
    }

    public static string ErrorCode(int status, JsonElement body)
    {
        if (status is 401 or 403 && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("twoFactorRequired", out var marker) && marker.ValueKind == JsonValueKind.True)
            return "two_factor_required";
        return status switch { 401 => "unauthorized", 403 => "forbidden", 429 => "rate_limited",
            404 => "not_found", 409 => "administrator_protected", _ => "unavailable" };
    }
}
