using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccountHub.Api.Contracts;

public sealed record AuditEvent
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("eventId")] public Guid EventId { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("eventType")] public string EventType { get; init; } = "";
    [JsonPropertyName("actorUserId")] public Guid? ActorUserId { get; init; }
    [JsonPropertyName("subjectType")] public string? SubjectType { get; init; }
    [JsonPropertyName("subjectId")] public string? SubjectId { get; init; }
    [JsonPropertyName("outcome")] public string Outcome { get; init; } = "success";
    [JsonPropertyName("occurredAt")] public DateTimeOffset OccurredAt { get; init; }
    [JsonPropertyName("correlationId")] public Guid? CorrelationId { get; init; }
    [JsonPropertyName("metadata")] public JsonElement Metadata { get; init; } = JsonSerializer.SerializeToElement(new { });
}

public sealed record NotificationEvent
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("eventId")] public Guid EventId { get; init; }
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("severity")] public string Severity { get; init; } = "info";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("body")] public string Body { get; init; } = "";
    [JsonPropertyName("link")] public string? Link { get; init; }
    [JsonPropertyName("audience")] public Guid[] Audience { get; init; } = [];
    [JsonPropertyName("adminOnly")] public bool AdminOnly { get; init; }
    [JsonPropertyName("dedupeKey")] public string? DedupeKey { get; init; }
    [JsonPropertyName("occurredAt")] public DateTimeOffset OccurredAt { get; init; }
    [JsonPropertyName("correlationId")] public Guid? CorrelationId { get; init; }
    [JsonPropertyName("metadata")] public JsonElement Metadata { get; init; } = JsonSerializer.SerializeToElement(new { });
}
