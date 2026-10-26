namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed record CircuitNode(string Id, string Kind, float X, float Y, string[] Inputs)
{
    public Condition? Condition { get; init; }
    public RuleCircuit? Circuit { get; init; }
    public string? Name { get; init; }
    public string? Source { get; init; }
    public bool Expanded { get; init; }
}
public sealed record RuleCircuit(int Version, CircuitNode[] Nodes)
{
    public Dictionary<string, CanvasPoint> Sources {get;init;} = [];
    public float Zoom { get; init; } = 1;
    public float PanX { get; init; }
    public float PanY { get; init; }
}
public sealed record CanvasPoint(float X,float Y);
public static class CircuitRules
{
    public static Dictionary<string, bool?> Trace(RuleCircuit circuit, Func<Condition, bool?> compare) {
        var map=circuit.Nodes.ToDictionary(n=>n.Id); var result=new Dictionary<string,bool?>();
        bool? Value(string id) {
            if(result.TryGetValue(id,out var prior))return prior;
            var n=map[id]; var inputs=n.Inputs.Select(Value).ToArray();
            bool? value=n.Kind switch { "start"=>true,"metadata"=>null,"condition"=>compare(n.Condition!),"custom"=>Trace(n.Circuit!,compare)[n.Circuit!.Nodes.Single(x=>x.Kind=="delete").Id],
                _ when inputs.Any(x=>x is null)=>null,"not"=>!inputs[0]!.Value,"and"=>inputs.All(x=>x==true),"or"=>inputs.Any(x=>x==true),"delete"=>inputs.All(x=>x==true),_=>null };
            result[id]=value;return value;
        }
        foreach(var n in circuit.Nodes)Value(n.Id);
        if(Conditions(circuit).Any(c=>compare(c) is null))result[circuit.Nodes.Single(n=>n.Kind=="delete").Id]=null;
        return result;
    }
    public static void Validate(RuleCircuit circuit) { var budget = 0; Check(circuit, 0, ref budget); }
    private static void Check(RuleCircuit circuit, int nesting, ref int budget)
    {
        static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid) { if (!valid) throw new InvalidOperationException("Invalid rule circuit"); }
        Require(circuit is not null && nesting <= 4);
        Require(circuit!.Version is 1 or 2 && circuit.Nodes is { Length: >= 3 and <= 128 } &&
            float.IsFinite(circuit.Zoom) && circuit.Zoom is >= .25f and <= 2 && float.IsFinite(circuit.PanX) && float.IsFinite(circuit.PanY) &&
            Math.Abs(circuit.PanX) <= 10000 && Math.Abs(circuit.PanY) <= 10000);
        budget += circuit.Nodes.Length; Require(budget <= 256);
        Require(circuit.Sources is not null && circuit.Sources.Count<=50 && circuit.Sources.All(p=>p.Key.Length<=100 && p.Value is not null && float.IsFinite(p.Value.X)&&float.IsFinite(p.Value.Y)&&Math.Abs(p.Value.X)<=10000&&Math.Abs(p.Value.Y)<=10000));
        Require(circuit.Nodes.All(n => n is not null && n.Id is { Length: > 0 and <= 100 } && n.Id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')));
        Require(circuit.Nodes.Select(n => n.Id).Distinct().Count() == circuit.Nodes.Length);
        var map = circuit.Nodes.ToDictionary(n => n.Id);
        Require(circuit.Nodes.Count(n => n.Kind == "start") == (circuit.Version==1?1:0) && circuit.Nodes.Count(n => n.Kind == "delete") == 1);
        foreach (var n in circuit.Nodes) {
            Require(float.IsFinite(n.X) && float.IsFinite(n.Y) && Math.Abs(n.X) <= 10000 && Math.Abs(n.Y) <= 10000 && n.Name?.Length is not > 100 && n.Inputs is not null);
            Require(n.Inputs!.All(id => id is not null && map.ContainsKey(id) && map[id].Kind != "delete"));
            if(circuit.Version==1)Require(n.Inputs.Select((id,index)=>(n.Kind=="delete"&&index==1)==(map[id].Kind=="start")).All(valid=>valid));
            else Require(n.Inputs.All(id=>(n.Kind=="condition")== (map[id].Kind=="metadata")));
            Require(n.Kind switch { "start"=>circuit.Version==1&&n.Inputs.Length==0,
                "metadata"=>circuit.Version==2&&n.Inputs.Length==0&&MetadataCatalog.Sources.Contains(n.Source),
                "condition"=>n.Inputs.Length==(circuit.Version==1?0:n.Condition?.ReferenceField is null?1:2),"custom"=>n.Inputs.Length==0,"not" => n.Inputs.Length == 1,
                "and" or "or" => n.Inputs.Length is >= 2 and <= 128, "delete" => circuit.Version==1?n.Inputs.Length == 2 && map[n.Inputs[1]].Kind == "start":n.Inputs.Length==1, _ => false });
            Require(n.Kind=="metadata"||n.Source is null);
            Require(n.Kind == "condition" || n.Condition is null); Require(n.Kind == "custom" || n.Circuit is null);
            if (n.Kind == "condition") {
                Require(n.Condition is not null);
                RuleEngine.Validate(new(0, false, Guid.Empty, Guid.Empty, 24, [new("check", "Check", false, "all", [n.Condition!])]));
                if(circuit.Version==2)Require(map[n.Inputs[0]].Source==MetadataCatalog.Find(n.Condition!.Field)!.Source);
                if(circuit.Version==2&&n.Condition!.ReferenceField is {} reference)Require(map[n.Inputs[1]].Source==MetadataCatalog.Find(reference)!.Source);
            }
            if (n.Kind == "custom") { Require(n.Circuit is not null); Check(n.Circuit!, nesting + 1, ref budget); }
        }
        var visiting = new HashSet<string>(); var complete = new HashSet<string>();
        void Visit(string id, int depth) {
            Require(depth <= 64 && !visiting.Contains(id)); if (complete.Contains(id)) return;
            visiting.Add(id); foreach (var input in map[id].Inputs) Visit(input, depth + 1); visiting.Remove(id); complete.Add(id);
        }
        foreach (var n in circuit.Nodes) Visit(n.Id, 0);
        Require(Conditions(circuit).Any());
    }
    public static IEnumerable<Condition> Conditions(RuleCircuit circuit) => circuit.Nodes.SelectMany(n => n.Kind == "condition" ? new[] { n.Condition! } : n.Kind == "custom" ? Conditions(n.Circuit!) : Array.Empty<Condition>());
    public static bool Evaluate(RuleCircuit circuit, Func<Condition, bool> compare)
    {
        var map = circuit.Nodes.ToDictionary(n => n.Id); var cache = new Dictionary<string, bool>();
        bool Value(string id) {
            if (cache.TryGetValue(id, out var prior)) return prior;
            var n = map[id];
            return cache[id] = n.Kind switch {
                "start" => true, "condition" => compare(n.Condition!), "custom" => Evaluate(n.Circuit!, compare),
                "not" => !Value(n.Inputs[0]), "and" => n.Inputs.All(Value), "or" => n.Inputs.Any(Value),
                "delete" => n.Inputs.All(Value), _ => false
            };
        }
        return Value(circuit.Nodes.Single(n => n.Kind == "delete").Id);
    }
}
