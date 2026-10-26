using System.Text.Json;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

// Missing keys and legacy nulls mean unavailable. Only providers that completed
// an authoritative read may emit this explicit confirmed-absence marker.
public static class MetadataEvidence
{
    public static JsonElement Absent=>JsonSerializer.SerializeToElement(new{confirmedAbsent=true});
    public static bool IsAbsent(JsonElement value)=>value.ValueKind==JsonValueKind.Object&&value.EnumerateObject().Count()==1&&value.TryGetProperty("confirmedAbsent",out var absent)&&absent.ValueKind==JsonValueKind.True;
    public static IEnumerable<string> Fields(IEnumerable<Condition> conditions)=>conditions.SelectMany(c=>c.ReferenceField is {} reference?new[]{c.Field,reference}:new[]{c.Field}).Distinct();
}
