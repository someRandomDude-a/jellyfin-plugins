using System.Reflection;
using Jellyfin.Plugin.AutomaticDeletionRequests;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;

namespace AutomaticDeletionRequests.Tests;

public sealed class LiveArrFactAttribute:FactAttribute
{
    public LiveArrFactAttribute(){if(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUITE_SONARR_URL")))Skip="Run scripts/metadata-arr-acceptance.mjs against disposable containers";}
}
public sealed class RealArrTests
{
    public class Proxy:DispatchProxy{public Func<MethodInfo,object?> Call=null!;protected override object? Invoke(MethodInfo? method,object?[]? args)=>Call(method!);}
    private static T Fake<T>(Func<MethodInfo,object?> call)where T:class{var value=DispatchProxy.Create<T,Proxy>();((Proxy)(object)value).Call=call;return value;}
    [LiveArrFact]public async Task ActualSonarrAndRadarrRecordsAreReadUsingExactNativeProviderIds(){
        var root=Path.Combine(Path.GetTempPath(),"live-arr-"+Guid.NewGuid().ToString("N"));
        try{
            var connections=new ArrConnections(Fake<IApplicationPaths>(m=>m.Name=="get_DataPath"?root:throw new NotSupportedException()),Fake<IPluginManager>(_=>throw new NotSupportedException()));
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(40));
            foreach(var source in new[]{"sonarr","radarr"}){
                connections.Save(source,new(0,Environment.GetEnvironmentVariable("SUITE_"+source.ToUpperInvariant()+"_URL")!,Environment.GetEnvironmentVariable("SUITE_"+source.ToUpperInvariant()+"_KEY")));
                BaseItem item=source=="sonarr"?new Series{Id=Guid.NewGuid()}:new Movie{Id=Guid.NewGuid()};item.ProviderIds[source=="sonarr"?"Tvdb":"Tmdb"]="987654321";
                var fields=MetadataCatalog.Fields.Where(f=>f.Source==source).Select(f=>f.Id).ToArray();var facts=await new ArrMetadata(connections).ReadAsync(item,fields,deadline.Token);
                Assert.Equal("Suite synthetic metadata",facts[source+".title"].GetString());Assert.False(facts[source+".monitored"].GetBoolean());Assert.True(facts[source+".id"].GetInt32()>0);Assert.NotEmpty(facts[source+".qualityProfile"].GetString()!);
                if(source=="sonarr"){
                    Assert.Equal(1,facts["sonarr.latestSeasonNumber"].GetInt32());Assert.Equal(1,facts["sonarr.latestSeasonEpisodeCount"].GetInt32());Assert.Equal(1,facts["sonarr.latestSeasonMissingEpisodes"].GetInt32());Assert.Equal(0,facts["sonarr.latestSeasonFileCount"].GetInt32());Assert.Equal(0,facts["sonarr.fileQualityNames"].GetArrayLength());
                    Assert.Equal(2020,facts["sonarr.latestSeasonFinaleAired"].GetDateTimeOffset().Year);
                }
                item.ProviderIds[source=="sonarr"?"Tvdb":"Tmdb"]="987654320";
                facts=await new ArrMetadata(connections).ReadAsync(item,[source+".title"],deadline.Token);Assert.True(MetadataEvidence.IsAbsent(facts[source+".title"]));
            }
        }finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
