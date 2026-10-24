using System.Text.Json;
using System.Text.Json.Nodes;
using AccountHub.Core;
using Jellyfin.Plugin.UserManager.Services;
using MediaBrowser.Model.Users;

namespace AccountHub.Api;

public static class ManagedPolicy
{
    public static readonly string[] Fields = ["EnableMediaPlayback", "EnableRemoteAccess", "EnableContentDownloading", "EnableAudioPlaybackTranscoding", "EnableVideoPlaybackTranscoding", "EnablePlaybackRemuxing", "MaxActiveSessions", "EnableAllFolders", "EnabledFolders", "MaxParentalRating"];
    public static Dictionary<string, object?> Project(UserPolicy value) => Fields.ToDictionary(name => name, name => typeof(UserPolicy).GetProperty(name)!.GetValue(value));
    public static UserPolicy Apply(UserPolicy current, JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new HubException("invalid_request", "Managed fields must be an object");
        var node = JsonSerializer.SerializeToNode(current, PolicyTools.JsonOptions)!.AsObject();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in patch.EnumerateObject())
        {
            if (!seen.Add(item.Name)) throw new HubException("invalid_request", "Duplicate managed field");
            if (!Fields.Contains(item.Name, StringComparer.Ordinal)) throw new HubException("rejected", "Field is not managed");
            var type = typeof(UserPolicy).GetProperty(item.Name)!.PropertyType;
            if (type == typeof(bool) && item.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || type == typeof(int) && !item.Value.TryGetInt32(out _)
                || type == typeof(int?) && item.Value.ValueKind != JsonValueKind.Null && !item.Value.TryGetInt32(out _)
                || type == typeof(string[]) && (item.Value.ValueKind != JsonValueKind.Array || item.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString()!.Length > 256)))
                throw new HubException("invalid_request", "Invalid managed field type");
            node[char.ToLowerInvariant(item.Name[0]) + item.Name[1..]] = JsonNode.Parse(item.Value.GetRawText());
        }
        var policy = node.Deserialize<UserPolicy>(PolicyTools.JsonOptions)!;
        if (policy.MaxActiveSessions < 0 || policy.MaxActiveSessions > 1000 || policy.EnabledFolders?.Length > 100 || policy.MaxParentalRating is < 0 or > 1000) throw new HubException("invalid_request", "Policy exceeds limits");
        return policy;
    }
}
