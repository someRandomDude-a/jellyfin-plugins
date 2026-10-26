using System.Text.Json;
using Jellyfin.Plugin.AutomaticDeletionRequests;
namespace AutomaticDeletionRequests.Tests;
public sealed class CircuitTests
{
    private static RuleCircuit Graph() => new(1,[new("start","start",0,0,[]),new("condition","condition",0,100,[]){Condition=new("year","less",JsonSerializer.SerializeToElement(2000))},new("not","not",300,100,["condition"]),new("delete","delete",600,100,["not","start"])]);
    [Fact] public void NotAndTracePreserveUnknownEvidence() {
        var graph=Graph();CircuitRules.Validate(graph);
        Assert.True(CircuitRules.Evaluate(graph,_=>false));
        var trace=CircuitRules.Trace(graph,_=>null);Assert.Null(trace["not"]);Assert.Null(trace["delete"]);
        var rule=new Rule("r","Rule",false,"all",CircuitRules.Conditions(graph).ToArray()){Circuit=graph};
        Assert.False(RuleEngine.Matches(rule,new Dictionary<string,JsonElement>(),DateTimeOffset.UtcNow));
    }
    [Fact] public void CyclesAndMissingInputsFailClosed() {
        var graph=Graph();graph.Nodes[2]=graph.Nodes[2] with{Inputs=["not"]};Assert.Throws<InvalidOperationException>(()=>CircuitRules.Validate(graph));
        graph=Graph();graph.Nodes[2]=graph.Nodes[2] with{Inputs=["missing"]};Assert.Throws<InvalidOperationException>(()=>CircuitRules.Validate(graph));
    }
}
