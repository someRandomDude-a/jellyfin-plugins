using System.Text.Json;
using Jellyfin.Plugin.AutomaticDeletionRequests;

namespace AutomaticDeletionRequests.Tests;

public sealed class RuleTests
{
    [Fact] public void SeerrRequesterIsTypedMetadataAndUnknownNeverMatchesNegativeComparisons() {
        var id=Guid.NewGuid();
        var rule=new Rule("requester","Requested by",true,"all",[new("seerr.requesterId","equals",Value(id.ToString("N")))]);
        RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule]));
        var facts=new Dictionary<string,JsonElement>{["seerr.requesterId"]=Value(id.ToString("D"))};
        Assert.True(RuleEngine.Matches(rule,facts,DateTimeOffset.UtcNow));
        rule=rule with{Conditions=[rule.Conditions[0] with{Operator="not-equals"}]};
        foreach(var missing in new[]{JsonSerializer.SerializeToElement<object?>(null),Value(""),Value("not-a-user"),Value(false)}) {
            facts["seerr.requesterId"]=missing;
            Assert.Null(RuleEngine.TestCondition(rule.Conditions[0],facts,DateTimeOffset.UtcNow));
            Assert.False(RuleEngine.Matches(rule,facts,DateTimeOffset.UtcNow));
        }
        Assert.True(RuleEngine.Matches(new("year","Year only",true,"all",[new("year","less",Value(2000))]),new Dictionary<string,JsonElement>{["year"]=Value(1990)},DateTimeOffset.UtcNow));
    }
    private static JsonElement Value(object value) => JsonSerializer.SerializeToElement(value);
    [Fact] public void NumericAndTextConditionsMatchWithoutScripts()
    {
        var rule = new Rule("old", "Old movies", true, "all", [new("year", "less", Value(2000)), new("kind", "equals", Value("movie"))]);
        RuleEngine.Validate(new(0, false, Guid.Empty, Guid.Empty, 24, [rule]));
        Assert.True(RuleEngine.Matches(rule, new Dictionary<string, JsonElement> { ["year"] = Value(1990), ["kind"] = Value("movie") }, DateTimeOffset.UtcNow));
    }
    [Fact] public void MissingEvidenceDoesNotMatchEvenUnderOr()
    {
        var rule = new Rule("test", "Unknown rating", true, "any", [new("year", "greater", Value(1900)), new("rating", "less", Value(5))]);
        Assert.False(RuleEngine.Matches(rule, new Dictionary<string, JsonElement> { ["year"] = Value(2020) }, DateTimeOffset.UtcNow));
    }
    [Fact] public void RelativeDatesUseTheCapturedEvaluationTime()
    {
        var now = DateTimeOffset.Parse("2026-10-25T00:00:00Z");
        var rule = new Rule("old", "Old media", true, "all", [new("added", "older-days", Value(30))]);
        Assert.True(RuleEngine.Matches(rule, new Dictionary<string, JsonElement> { ["added"] = Value(now.AddDays(-31)) }, now));
        Assert.False(RuleEngine.Matches(rule, new Dictionary<string, JsonElement> { ["added"] = Value(now.AddDays(-29)) }, now));
    }
    [Theory] [InlineData("year", "\"unknown\"", "2000")]
    [InlineData("rating", "1e999", "5")]
    [InlineData("seerr.ownRequest", "\"false\"", "false")]
    [InlineData("added", "\"invalid-date\"", "30")]
    public void MalformedEvidenceNeverMatchesUnderNotEqualsOrOr(string field, string actual, string expected)
    {
        using var input = JsonDocument.Parse(actual); using var target = JsonDocument.Parse(expected);
        var rule = new Rule("bad", "Bad evidence", true, "any", [new("kind", "equals", Value("movie")), new(field, field == "added" ? "older-days" : "not-equals", target.RootElement)]);
        RuleEngine.Validate(new(0, false, Guid.Empty, Guid.Empty, 24, [rule]));
        Assert.False(RuleEngine.Matches(rule, new Dictionary<string, JsonElement> { ["kind"] = Value("movie"), [field] = input.RootElement }, DateTimeOffset.UtcNow));
    }
    [Fact] public void UnsupportedProvidersAndEmptyRulesAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => RuleEngine.Validate(new(0, false, Guid.Empty, Guid.Empty, 24, [new("bad", "Unsupported", true, "all", [new("unknown.tags", "contains", Value("delete"))])])));
        Assert.Throws<InvalidOperationException>(() => RuleEngine.Validate(new(0, false, Guid.Empty, Guid.Empty, 24, [new("bad", "Empty", true, "all", [])])));
    }
}
