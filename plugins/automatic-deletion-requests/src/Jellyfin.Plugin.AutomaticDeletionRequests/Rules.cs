using System.Text.Json;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed record Condition(string Field, string Operator, JsonElement Value)
{
    public string? ReferenceField{get;init;}
    public string? Conversion { get; init; }
}
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
    public static readonly string[] Fields = MetadataCatalog.Fields.Select(f=>f.Id).ToArray();
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
                var field=MetadataCatalog.Find(c.Field)!;
                if(c.Conversion is {} conversion){
                    if(!field.Conversions.Contains(conversion)||c.Operator is not ("equals" or "not-equals" or "greater" or "less")||
                        c.Value.ValueKind!=JsonValueKind.Number||!c.Value.TryGetDouble(out var threshold)||!double.IsFinite(threshold))throw new InvalidOperationException("Invalid metadata conversion");
                    var difference=conversion.StartsWith("difference-",StringComparison.Ordinal);
                    if(difference ? c.ReferenceField is null||MetadataCatalog.Find(c.ReferenceField)?.Type!="date" : c.ReferenceField is not null)throw new InvalidOperationException("Invalid conversion operand");
                    continue;
                }
                if(!field.Operators.Contains(c.Operator))throw new InvalidOperationException("Invalid comparison");
                if(c.ReferenceField is {} reference){
                    if(field.Type=="list"&&c.Operator is "contains" or "not-contains"){
                        if(MetadataCatalog.Find(reference)?.Type!=field.ElementType)throw new InvalidOperationException("List membership requires a compatible element type");
                        continue;
                    }
                    var target=MetadataCatalog.Find(reference);if(target is null||target.Type!=field.Type||target.Unit!=field.Unit||c.Operator is not ("equals" or "not-equals" or "greater" or "less" or "before" or "after")||field.Type=="list")throw new InvalidOperationException("Incompatible metadata comparison");
                    continue;
                }
                if(c.Operator is "exists" or "not-exists"){if(c.Value.ValueKind!=JsonValueKind.Null)throw new InvalidOperationException("Existence comparisons have no value");continue;}
                var valid=field.Type switch {
                    "text"=>c.Value.ValueKind==JsonValueKind.String,
                    "user"=>c.Value.ValueKind==JsonValueKind.String&&Guid.TryParse(c.Value.GetString(),out var uid)&&uid!=Guid.Empty,
                    "boolean"=>c.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "date" when c.Operator is "before" or "after" or "equals" or "not-equals"=>c.Value.ValueKind==JsonValueKind.String&&c.Value.TryGetDateTimeOffset(out _),
                    "date"=>c.Value.ValueKind==JsonValueKind.Number&&c.Value.TryGetInt32(out var days)&&days is >=0 and <=36500,
                    "list" when c.Operator.StartsWith("count-",StringComparison.Ordinal)=>c.Value.ValueKind==JsonValueKind.Number&&c.Value.TryGetInt32(out var count)&&count is >=0 and <=1000000,
                    "list" when c.Operator is "contains" or "not-contains" or "contains-partial" or "not-contains-partial"=>c.Value.ValueKind==JsonValueKind.String,
                    "list"=>ValidList(c.Value),
                    _=>c.Value.ValueKind==JsonValueKind.Number&&c.Value.TryGetDouble(out var number)&&double.IsFinite(number)
                };
                if(!valid)throw new InvalidOperationException("Invalid condition value");
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
            if (GetConditions(rule).Any(c => TestCondition(c,evidence,now) is null)) return false;
            if (rule.Circuit is not null) return CircuitRules.Evaluate(rule.Circuit, c => TestCondition(c,evidence,now)==true);
            var results = rule.Conditions.Select(c => TestCondition(c,evidence,now)==true).ToArray();
            return rule.Match == "all" ? results.All(x => x) : results.Any(x => x);
        } catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { return false; }
    }
    public static IEnumerable<Condition> GetConditions(Rule rule) => rule.Circuit is null ? rule.Conditions : CircuitRules.Conditions(rule.Circuit);
    public static bool? TestCondition(Condition c, IReadOnlyDictionary<string, JsonElement> evidence, DateTimeOffset now) {
        try {
            if(!evidence.TryGetValue(c.Field,out var value))return null;
            if(c.Operator is "exists" or "not-exists"){
                if(MetadataEvidence.IsAbsent(value))return c.Operator=="not-exists";
                return ValidEvidence(c,value)?c.Operator=="exists":null;
            }
            if(!ValidEvidence(c,value))return null;
            if(c.Conversion is {} conversion){
                if(!MetadataCatalog.Find(c.Field)!.Conversions.Contains(conversion))return null;
                double number;
                if(conversion=="count")number=value.GetArrayLength();
                else if(conversion=="boolean-number")number=value.GetBoolean()?1:0;
                else if(conversion=="text-number"){
                    if(!double.TryParse(value.GetString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out number))return null;
                }
                else if(conversion=="unix-seconds")number=(value.GetDateTimeOffset()-DateTimeOffset.UnixEpoch).TotalSeconds;
                else{
                    var end=now;
                    if(conversion.StartsWith("difference-",StringComparison.Ordinal)){
                        if(c.ReferenceField is not {} field||!evidence.TryGetValue(field,out var date)||MetadataCatalog.Find(field)?.Type!="date"||!ValidEvidence(c with{Field=field},date))return null;
                        end=value.GetDateTimeOffset();value=date;
                    }
                    var delta=end-value.GetDateTimeOffset();
                    number=conversion.EndsWith("hours",StringComparison.Ordinal)?delta.TotalHours:delta.TotalDays;
                }
                if(!double.IsFinite(number))return null;
                return Compare(c with{Field="year",Conversion=null,ReferenceField=null},JsonSerializer.SerializeToElement(number),now);
            }
            if(c.ReferenceField is {} reference){if(!evidence.TryGetValue(reference,out var other)||!ValidEvidence(c with{Field=reference},other))return null;
                var field=MetadataCatalog.Find(c.Field)!;
                if(field.Type=="list"&&field.ElementType=="user"&&c.Operator is "contains" or "not-contains"){
                    if(!Guid.TryParse(other.GetString(),out var user)||user==Guid.Empty)return null;
                    var ids=value.EnumerateArray().Select(v=>Guid.TryParse(v.GetString(),out var id)&&id!=Guid.Empty?(Guid?)id:null).ToArray();
                    if(ids.Any(id=>id is null))return null;
                    return c.Operator=="contains"?ids.Contains(user):!ids.Contains(user);
                }
                c=c with{Value=other};}
            return Compare(c,value,now);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { return null; }
    }
    private static bool ValidList(JsonElement value)=>value.ValueKind==JsonValueKind.Array&&value.GetArrayLength()<=1000&&value.EnumerateArray().All(v=>v.ValueKind==JsonValueKind.String&&v.GetString()!.Length<=500);
    private static bool ValidEvidence(Condition condition, JsonElement value) => MetadataCatalog.Find(condition.Field)?.Type switch {
        "text" => value.ValueKind == JsonValueKind.String,
        "date" => value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out _),
        "user" => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(),out var id) && id != Guid.Empty,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "list"=>ValidList(value),
        _ => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number)
    };
    private static bool Compare(Condition c, JsonElement actual, DateTimeOffset now) => c.Operator switch {
        "older-days" => actual.GetDateTimeOffset() < now.AddDays(-c.Value.GetInt32()),
        "newer-days" => actual.GetDateTimeOffset() >= now.AddDays(-c.Value.GetInt32()),
        "older-hours" => actual.GetDateTimeOffset() < now.AddHours(-c.Value.GetInt32()),
        "newer-hours" => actual.GetDateTimeOffset() >= now.AddHours(-c.Value.GetInt32()),
        "in-next-days"=>actual.GetDateTimeOffset()>=now&&actual.GetDateTimeOffset()<=now.AddDays(c.Value.GetInt32()),
        "in-last-days"=>actual.GetDateTimeOffset()<=now&&actual.GetDateTimeOffset()>=now.AddDays(-c.Value.GetInt32()),
        "before"=>actual.GetDateTimeOffset()<c.Value.GetDateTimeOffset(),
        "after"=>actual.GetDateTimeOffset()>c.Value.GetDateTimeOffset(),
        "contains" => Contains(actual,c.Value.GetString()!),
        "not-contains"=>!Contains(actual,c.Value.GetString()!),
        "contains-partial"=>actual.EnumerateArray().Any(v=>v.GetString()!.Contains(c.Value.GetString()!,StringComparison.OrdinalIgnoreCase)),
        "not-contains-partial"=>!actual.EnumerateArray().Any(v=>v.GetString()!.Contains(c.Value.GetString()!,StringComparison.OrdinalIgnoreCase)),
        "contains-any"=>c.Value.EnumerateArray().Any(v=>Contains(actual,v.GetString()!)),
        "contains-all"=>c.Value.EnumerateArray().All(v=>Contains(actual,v.GetString()!)),
        "not-contains-all"=>!c.Value.EnumerateArray().All(v=>Contains(actual,v.GetString()!)),
        "contains-none"=>!c.Value.EnumerateArray().Any(v=>Contains(actual,v.GetString()!)),
        "count-equals"=>actual.GetArrayLength()==c.Value.GetInt32(),
        "count-not-equals"=>actual.GetArrayLength()!=c.Value.GetInt32(),
        "count-greater"=>actual.GetArrayLength()>c.Value.GetInt32(),
        "count-less"=>actual.GetArrayLength()<c.Value.GetInt32(),
        "greater" => actual.GetDouble() > c.Value.GetDouble(),
        "less" => actual.GetDouble() < c.Value.GetDouble(),
        "equals" => EqualValue(c,actual), "not-equals" => !EqualValue(c,actual), _ => false
    };
    private static bool Contains(JsonElement actual,string value)=>actual.ValueKind==JsonValueKind.Array
        ?actual.EnumerateArray().Any(v=>string.Equals(v.GetString(),value,StringComparison.OrdinalIgnoreCase))
        :actual.GetString()!.Contains(value,StringComparison.OrdinalIgnoreCase);
    private static bool EqualValue(Condition c,JsonElement actual) => c.Field == "seerr.requesterId"
        ? Guid.Parse(actual.GetString()!) == Guid.Parse(c.Value.GetString()!) : MetadataCatalog.Find(c.Field)?.Type=="date"?actual.GetDateTimeOffset()==c.Value.GetDateTimeOffset():Equal(actual,c.Value);
    private static bool Equal(JsonElement a, JsonElement b) => a.ValueKind == b.ValueKind && (a.ValueKind == JsonValueKind.Number ? a.GetDouble() == b.GetDouble() : a.ValueKind is JsonValueKind.True or JsonValueKind.False || a.ValueKind == JsonValueKind.String && string.Equals(a.GetString(), b.GetString(), StringComparison.OrdinalIgnoreCase));
}
