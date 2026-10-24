using System.Text.Json;
using System.Text.Json.Serialization;

namespace AccountHub.Api.Contracts;

// Domain-owned metadata: Hub must not invent defaults or replace native enum/null semantics.
public sealed record DomainCapabilities(
    [property: JsonPropertyName("apiVersion")] string ApiVersion,
    [property: JsonPropertyName("plugin")] string Plugin,
    [property: JsonPropertyName("pluginVersion")] string PluginVersion,
    [property: JsonPropertyName("features")] string[] Features,
    [property: JsonPropertyName("adminExclusion")] bool AdminExclusion,
    [property: JsonPropertyName("fields")] DomainField[] Fields);

public sealed record DomainField(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("default")] JsonElement Default,
    [property: JsonPropertyName("nullable")] bool Nullable = false,
    [property: JsonPropertyName("enumValues")] string[]? EnumValues = null,
    [property: JsonPropertyName("minimum")] decimal? Minimum = null,
    [property: JsonPropertyName("maximum")] decimal? Maximum = null);

public static class DomainRoutes
{
    public const string DeletionQueue = "/DeletionQueue/admin/account-hub/v1";
    public const string PrivateSyncPlay = "/PrivateSyncPlay/admin/account-hub/v1";
}
