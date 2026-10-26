using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.AutomaticDeletionRequests;
using MediaBrowser.Common.Configuration;

namespace AutomaticDeletionRequests.Tests;

public sealed class RuleStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "automatic-store-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(root, "automatic-deletion-requests", "rules.json");
    private RuleStore Store() {
        var paths = DispatchProxy.Create<IApplicationPaths, LiveRuleTests.Proxy>();
        ((LiveRuleTests.Proxy)(object)paths).Call = (method, _) => method.Name == "get_DataPath" ? root : throw new NotSupportedException();
        return new(paths);
    }
    private void Seed() => Store().Write(s => { s.Submitted["old-rule"] = [Guid.NewGuid()]; return true; });

    [Fact] public void MissingEstablishedStateFailsClosedAcrossNewStoreInstances() {
        Seed(); File.Delete(FilePath);
        Assert.Throws<InvalidOperationException>(() => Store().Read(s => s.Configuration));
        Assert.Throws<InvalidOperationException>(() => Store().Write(s => true));
        Assert.False(File.Exists(FilePath));
    }
    [Fact] public void LegacyStateIsAdoptedAndOrphanedPendingStateIsNotAFirstInstall() {
        Seed(); File.Delete(FilePath + ".installed");
        Assert.Single(Store().Read(s => s.Submitted)); Assert.True(File.Exists(FilePath + ".installed"));
        File.Delete(FilePath); File.Delete(FilePath + ".installed"); File.WriteAllText(FilePath + ".pending", "{}");
        Assert.Throws<InvalidOperationException>(() => Store().Read(s => s.Configuration));
    }
    [Fact] public void OversizedWritePreservesLastReadableState() {
        Seed(); var before = File.ReadAllBytes(FilePath);
        Assert.Throws<InvalidOperationException>(() => Store().Write(s => {
            s.Configuration = s.Configuration with { Rules = Enumerable.Range(0, 50).Select(i => new Rule("r" + i, "Rule", false, "all",
                Enumerable.Range(0, 50).Select(_ => new Condition("title", "contains", JsonSerializer.SerializeToElement(new string('a', 450)))).ToArray())).ToArray() };
            for (var i = 0; i < 4; i++) s.Submitted["history" + i] = Enumerable.Range(0, 9000).Select(_ => Guid.NewGuid()).ToHashSet();
            return true;
        }));
        Assert.Equal(before, File.ReadAllBytes(FilePath)); Assert.Single(Store().Read(s => s.Submitted));
        Assert.False(File.Exists(FilePath + ".pending"));
    }
    [Theory] [InlineData("{\"Configuration\":null}")] [InlineData("{\"Submitted\":null}")]
    [InlineData("{\"Submitted\":{\"rule\":null}}")]
    [InlineData("{\"Submitted\":{\"rule\":[\"00000000-0000-0000-0000-000000000000\"]}}")]
    public void InvalidStateNeverResetsOrRewritesHistory(string json) {
        Seed(); File.WriteAllText(FilePath, json);
        Assert.Throws<InvalidOperationException>(() => Store().Read(s => s.Configuration));
        Assert.Equal(json, File.ReadAllText(FilePath));
    }
    [Fact] public void NullRulesAndConditionsAreRejectedAsInvalidConfiguration() {
        var config = Store().Read(s => s.Configuration);
        Assert.Throws<InvalidOperationException>(() => RuleEngine.Validate(config with { Rules = [null!] }));
        Assert.Throws<InvalidOperationException>(() => RuleEngine.Validate(config with { Rules = [new("r", "Rule", false, "all", [null!])] }));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
