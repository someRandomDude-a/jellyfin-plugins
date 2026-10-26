using System.Text.Json;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed record Condition(string Field, string Operator, JsonElement Value);
public sealed record Rule(string Id, string Name, bool Enabled, string Match, Condition[] Conditions)
{
    public RuleCircuit? Circuit { get; init; }
    public Guid? LibraryId { get; init; }
    public float CanvasX { get; init; } = 540;
    public float CanvasY { get; init; }
    public string Description { get; init; } = "";
}
public sealed record LibraryFilter(string Id, string Name, Guid LibraryId, Rule Predicate, string[] Outputs)
{
    public string Description { get; init; } = "";
    public float X { get; init; }
    public float Y { get; init; }
}
public sealed record Configuration(long Revision, bool Enabled, Guid ActorId, Guid LibraryId, int IntervalHours, Rule[] Rules)
{
    public LibraryFilter[]? Filters { get; init; }
    public CanvasView View { get; init; } = new(1,0,0);
}
public sealed record CanvasView(float Zoom,float PanX,float PanY);
public static class RuleEngine
{
    public static readonly string[] Fields = ["title", "year", "added", "rating", "kind", "seerr.ownRequest", "seerr.is4k", "seerr.requestOnly"];
    public static void Validate(Configuration config)
    {
        if(config is not null && (config.View is null || !float.IsFinite(config.View.Zoom) || config.View.Zoom is <.25f or >2 || !float.IsFinite(config.View.PanX) || !float.IsFinite(config.View.PanY) || Math.Abs(config.View.PanX)>10000 || Math.Abs(config.View.PanY)>10000))throw new InvalidOperationException("Invalid canvas view");
        if (config is null || config.Revision < 0 || config.Rules is null || config.Rules.Length > 50 || config.Rules.Any(r => r is null) || config.IntervalHours is < 1 or > 168 ||
            config.Enabled && (config.ActorId == Guid.Empty || config.Filters is null && config.LibraryId == Guid.Empty) ||
            config.Rules.Select(r => r.Id).Distinct().Count() != config.Rules.Length) throw new InvalidOperationException("Invalid rule configuration");
        foreach (var r in config.Rules) {
            if (r.LibraryId == Guid.Empty) throw new InvalidOperationException("Invalid library scope");
            if(r.Description is null || r.Description.Length>1000)throw new InvalidOperationException("Invalid card description");
            if(!float.IsFinite(r.CanvasX)||!float.IsFinite(r.CanvasY)||Math.Abs(r.CanvasX)>10000||Math.Abs(r.CanvasY)>10000)throw new InvalidOperationException("Invalid canvas position");
            if (r.Id is not { Length: > 0 and <= 100 } || !r.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') || r.Name is not { Length: > 0 and <= 100 } ||
                r.Match is not ("all" or "any") || r.Conditions is null || r.Conditions.Length is < 1 or > 50) throw new InvalidOperationException("Invalid rule");
            foreach (var c in r.Conditions) {
                if (c is null || !Fields.Contains(c.Field) || c.Value.ValueKind == JsonValueKind.Undefined || c.Value.GetRawText().Length > 500) throw new InvalidOperationException("Unsupported field");
                if (c.Field is "title" or "kind") {
                    if (c.Operator is not ("equals" or "not-equals" or "contains") || c.Value.ValueKind != JsonValueKind.String) throw new InvalidOperationException("Invalid text condition");
                } else if (c.Field.StartsWith("seerr.", StringComparison.Ordinal)) {
                    if (c.Operator is not ("equals" or "not-equals") || c.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidOperationException("Invalid Seerr condition");
                } else if (c.Field == "added") {
                    if (c.Operator is not ("older-days" or "newer-days") || !c.Value.TryGetInt32(out var days) || days is < 0 or > 36500) throw new InvalidOperationException("Invalid date condition");
                } else if (c.Operator is not ("equals" or "not-equals" or "greater" or "less") || !c.Value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new InvalidOperationException("Invalid number condition");
            }
            if (r.Circuit is not null) CircuitRules.Validate(r.Circuit);
        }
        if (config.Filters is not null) {
            if(config.Filters.Length>50 || config.Filters.Any(f=>f is null) || config.Filters.Select(f=>f.Id).Distinct().Count()!=config.Filters.Length)throw new InvalidOperationException("Invalid library filters");
            foreach(var filter in config.Filters) {
                if(config.Rules.Any(r=>r.Id==filter.Id))throw new InvalidOperationException("Card types must have distinct identities");
                if(filter.Id is not {Length:>0 and <=100} || !filter.Id.All(c=>char.IsAsciiLetterOrDigit(c)||c is '-' or '_') || filter.Name is not {Length:>0 and <=100} || filter.LibraryId==Guid.Empty || filter.Predicate is null || filter.Outputs is null || filter.Outputs.Length>50 || filter.Outputs.Distinct().Count()!=filter.Outputs.Length || filter.Outputs.Any(id=>!config.Rules.Any(r=>r.Id==id)) || !float.IsFinite(filter.X) || !float.IsFinite(filter.Y) || Math.Abs(filter.X)>10000 || Math.Abs(filter.Y)>10000)throw new InvalidOperationException("Invalid library filter");
                if(filter.Description is null || filter.Description.Length>1000)throw new InvalidOperationException("Invalid card description");
                Validate(new(0,false,Guid.Empty,Guid.Empty,24,[filter.Predicate]));
            }
        }
    }
    public static bool Matches(Rule rule, IReadOnlyDictionary<string, JsonElement> evidence, DateTimeOffset now)
    {
        // Unavailable facts invalidate the whole evaluation, even under OR/NOT.
        try {
            if (GetConditions(rule).Any(c => !evidence.TryGetValue(c.Field, out var value) || !ValidEvidence(c, value))) return false;
            if (rule.Circuit is not null) return CircuitRules.Evaluate(rule.Circuit, c => Compare(c, evidence[c.Field], now));
            var results = rule.Conditions.Select(c => Compare(c, evidence[c.Field], now)).ToArray();
            return rule.Match == "all" ? results.All(x => x) : results.Any(x => x);
        } catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { return false; }
    }
    public static IEnumerable<Condition> GetConditions(Rule rule) => rule.Circuit is null ? rule.Conditions : CircuitRules.Conditions(rule.Circuit);
    public static bool? TestCondition(Condition c, IReadOnlyDictionary<string, JsonElement> evidence, DateTimeOffset now) {
        try { return evidence.TryGetValue(c.Field, out var value) && ValidEvidence(c, value) ? Compare(c, value, now) : null; }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { return null; }
    }
    private static bool ValidEvidence(Condition condition, JsonElement value) => condition.Field switch {
        "title" or "kind" => value.ValueKind == JsonValueKind.String,
        "added" => value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out _),
        _ when condition.Field.StartsWith("seerr.", StringComparison.Ordinal) => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
    };
    private static bool Compare(Condition c, JsonElement actual, DateTimeOffset now) => c.Operator switch {
        "older-days" => actual.GetDateTimeOffset() < now.AddDays(-c.Value.GetInt32()),
        "newer-days" => actual.GetDateTimeOffset() >= now.AddDays(-c.Value.GetInt32()),
        "contains" => actual.GetString()!.Contains(c.Value.GetString()!, StringComparison.OrdinalIgnoreCase),
        "greater" => actual.GetDouble() > c.Value.GetDouble(),
        "less" => actual.GetDouble() < c.Value.GetDouble(),
        "equals" => Equal(actual, c.Value), "not-equals" => !Equal(actual, c.Value), _ => false
    };
    private static bool Equal(JsonElement a, JsonElement b) => a.ValueKind == b.ValueKind && (a.ValueKind == JsonValueKind.Number ? a.GetDouble() == b.GetDouble() : a.ValueKind is JsonValueKind.True or JsonValueKind.False || a.ValueKind == JsonValueKind.String && string.Equals(a.GetString(), b.GetString(), StringComparison.OrdinalIgnoreCase));
}
