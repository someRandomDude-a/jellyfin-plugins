using System.Text.Json;
using System.Text.Json.Serialization;

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
[method: JsonConstructor]
public sealed record NotificationEvent(
    int SchemaVersion, Guid EventId, string Source, string Type, string Severity,
    string Title, string Body, Guid[] Audience, bool AdminOnly, DateTimeOffset OccurredAt,
    string? Link = null, string? DedupeKey = null, Guid? CorrelationId = null,
    Dictionary<string, string>? Metadata = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NotificationAction[]? Actions = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Details = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] NotificationLayout? Layout = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public NotificationCommand[]? Commands { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public DateTimeOffset? FirstViewedAt { get; init; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? Importance{get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]public bool Retroactive{get;init;}
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public NotificationGroup? Group{get;init;}
    // Retain the original ABI for independently packaged producer DLLs.
    public NotificationEvent(int schemaVersion, Guid eventId, string source, string type, string severity, string title, string body,
        Guid[] audience, bool adminOnly, DateTimeOffset occurredAt, string? link, string? dedupeKey, Guid? correlationId, Dictionary<string, string>? metadata)
        : this(schemaVersion, eventId, source, type, severity, title, body, audience, adminOnly, occurredAt, link, dedupeKey, correlationId, metadata, null, null, null) { }
    public NotificationEvent(int schemaVersion, Guid eventId, string source, string type, string severity, string title, string body,
        Guid[] audience, bool adminOnly, DateTimeOffset occurredAt, string? link, string? dedupeKey, Guid? correlationId, Dictionary<string, string>? metadata, NotificationAction[]? actions, string? details)
        : this(schemaVersion, eventId, source, type, severity, title, body, audience, adminOnly, occurredAt, link, dedupeKey, correlationId, metadata, actions, details, null) { }
    public void Deconstruct(out int schemaVersion, out Guid eventId, out string source, out string type, out string severity, out string title, out string body,
        out Guid[] audience, out bool adminOnly, out DateTimeOffset occurredAt, out string? link, out string? dedupeKey, out Guid? correlationId, out Dictionary<string, string>? metadata)
    {
        schemaVersion = SchemaVersion; eventId = EventId; source = Source; type = Type; severity = Severity; title = Title; body = Body;
        audience = Audience; adminOnly = AdminOnly; occurredAt = OccurredAt; link = Link; dedupeKey = DedupeKey; correlationId = CorrelationId; metadata = Metadata;
    }
}
public sealed record NotificationAction(string Label, string Link, string Size = "compact");
public sealed record NotificationCommand(string Id, string Label, long Revision, string Snapshot, bool Enabled = true)
{
    public bool Selected { get; init; }
}
public sealed record NotificationCommandRequest(string Id, Guid CommandId, long Revision, string Snapshot);
public sealed record AuditEvent(
    int SchemaVersion, Guid EventId, string Source, string EventType, string Outcome,
    DateTimeOffset OccurredAt, Guid? ActorUserId = null, string? SubjectType = null,
    string? SubjectId = null, Guid? CorrelationId = null, JsonElement? Metadata = null);
public sealed record PublishResult(string Status, Guid[] NotificationIds);
