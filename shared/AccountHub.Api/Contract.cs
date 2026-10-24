using System.Text.Json;

namespace AccountHub.Api;

/// <summary>Shared v1 producer DTOs. Only the framework delegate crosses plugin load contexts.</summary>
public static class Contract
{
    public const string ServiceKey = "AccountHub.Api.1";
    public const int EventLimit = 64 * 1024;
    public const int BatchLimit = 256 * 1024;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 32 };
    public static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    public static object Success(object data) => new { apiVersion = "1", ok = true, data };
    public static object Failure(string code, string message, bool retryable = false) =>
        new { apiVersion = "1", ok = false, error = new { code, message, retryable } };
}

public sealed record Request(string ApiVersion, string Operation, Guid RequestId, JsonElement Payload);
public sealed record NotificationEvent(
    int SchemaVersion, Guid EventId, string Source, string Type, string Severity,
    string Title, string Body, Guid[] Audience, bool AdminOnly, DateTimeOffset OccurredAt,
    string? Link = null, string? DedupeKey = null, Guid? CorrelationId = null,
    Dictionary<string, string>? Metadata = null);
public sealed record AuditEvent(
    int SchemaVersion, Guid EventId, string Source, string EventType, string Outcome,
    DateTimeOffset OccurredAt, Guid? ActorUserId = null, string? SubjectType = null,
    string? SubjectId = null, Guid? CorrelationId = null, JsonElement? Metadata = null);
public sealed record PublishResult(string Status, Guid[] NotificationIds);
