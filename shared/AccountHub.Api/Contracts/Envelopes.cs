using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccountHub.Api.Contracts;

public sealed record ApiRequest(
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("requestId")] Guid RequestId,
    [property: JsonPropertyName("payload")] JsonElement Payload);

public sealed record ApiError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable);

public sealed record ApiResponse(
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Data,
    [property: JsonPropertyName("error"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ApiError? Error)
{
    public static ApiResponse Success<T>(T data) => new("1", true, JsonSerializer.SerializeToElement(data, Wire.Options), null);
    public static ApiResponse Failure(string code, string message, bool retryable = false) => new("1", false, null, new(code, message, retryable));
}

public static class Wire
{
    public const string ServiceKey = "AccountHub.Api.1";
    public const int MaxEventBytes = 64 * 1024;
    public const int MaxBatchBytes = 256 * 1024;
    public const int MaxBatchEvents = 50;
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };
}
