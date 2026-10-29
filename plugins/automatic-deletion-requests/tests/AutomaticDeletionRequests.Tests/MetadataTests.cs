using System.Text.Json;
using Jellyfin.Plugin.AutomaticDeletionRequests;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace AutomaticDeletionRequests.Tests;

public sealed class MetadataTests
{
    [Fact]public void EveryEpisodeFinishedByListCanContainTheVerifiedRequester(){
        var requester=Guid.NewGuid();var c=new Condition("jellyfin.allEpisodesSeenBy","contains",V(null)){ReferenceField="seerr.requesterId"};
        var rule=new Rule("finished","Requester finished the show",true,"all",[c]){Circuit=new(2,[new("j","metadata",0,0,[]){Source="jellyfin"},new("s","metadata",0,200,[]){Source="seerr"},new("c","condition",300,0,["j","s"]){Condition=c},new("n","not",600,0,["c"]),new("out","delete",900,0,["n"])])};
        RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule]));
        var facts=new Dictionary<string,JsonElement>{{c.Field,V(new[]{requester.ToString("N")})},{"seerr.requesterId",V(requester.ToString("D"))}};
        Assert.True(RuleEngine.TestCondition(c,facts,DateTimeOffset.UtcNow));
        Assert.False(RuleEngine.Matches(rule,facts,DateTimeOffset.UtcNow));
        facts.Remove("seerr.requesterId");Assert.Null(RuleEngine.TestCondition(c,facts,DateTimeOffset.UtcNow));Assert.False(RuleEngine.Matches(rule,facts,DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(()=>RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule with{Circuit=null,Conditions=[c with{ReferenceField="title"}]}])));
    }
    [Theory]
    [InlineData("title","text-number","12.5",12.5)]
    [InlineData("seerr.is4k","boolean-number",true,1)]
    [InlineData("seerr.is4k","boolean-number",false,0)]
    public void ScalarTypeConversionsAreExplicitAndValidated(string field,string conversion,object actual,double expected){
        var c=new Condition(field,"equals",V(expected)){Conversion=conversion};
        RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[new("convert","Convert",true,"all",[c])]));
        Assert.True(RuleEngine.TestCondition(c,new Dictionary<string,JsonElement>{{field,V(actual)}},DateTimeOffset.UtcNow));
    }
    [Theory]
    [InlineData("")][InlineData("NaN")][InlineData("1,234")][InlineData("12 days")]
    public void FailedTextConversionsStayUnknown(string text){
        var c=new Condition("title","not-equals",V(0)){Conversion="text-number"};
        Assert.Null(RuleEngine.TestCondition(c,new Dictionary<string,JsonElement>{{"title",V(text)}},DateTimeOffset.UtcNow));
    }
    [Fact]public void DateConversionsKeepFractionalSignedUnitsAndFetchBothSources(){
        var now=DateTimeOffset.Parse("2026-10-27T12:00:00Z");
        var c=new Condition("added","equals",V(1.5)){Conversion="difference-days",ReferenceField="radarr.added"};
        var rule=new Rule("delta","Date difference",true,"all",[c]);RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule]));
        var facts=new Dictionary<string,JsonElement>{{"added",V(now)},{"radarr.added",V(now.AddHours(-36))}};
        Assert.True(RuleEngine.Matches(rule,facts,now));Assert.Contains("radarr.added",MetadataEvidence.Fields([c]));
        Assert.True(RuleEngine.TestCondition(c with{Field="radarr.added",ReferenceField="added",Value=V(-1.5)},facts,now));
        Assert.True(RuleEngine.TestCondition(c with{Conversion="difference-hours",Value=V(36)},facts,now));
        Assert.True(RuleEngine.TestCondition(c with{Field="radarr.added",ReferenceField=null,Conversion="age-hours",Value=V(36)},facts,now));
        facts.Remove("radarr.added");Assert.Null(RuleEngine.TestCondition(c,facts,now));Assert.False(RuleEngine.Matches(rule,facts,now));
    }
    [Fact]public void ConvertedListsRequireRealEvidenceAndInvalidConversionsAreRejected(){
        var c=new Condition("jellyfin.genres","equals",V(2)){Conversion="count"};var rule=new Rule("count","List count",true,"all",[c]);
        RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule]));
        Assert.True(RuleEngine.Matches(rule,new Dictionary<string,JsonElement>{{c.Field,V(new[]{"Drama","Comedy"})}},DateTimeOffset.UtcNow));
        Assert.Null(RuleEngine.TestCondition(c,new Dictionary<string,JsonElement>{{c.Field,MetadataEvidence.Absent}},DateTimeOffset.UtcNow));
        foreach(var invalid in new[]{c with{Conversion="age-days"},c with{ReferenceField="added"},c with{Operator="contains"},c with{Value=V("2")}})
            Assert.Throws<InvalidOperationException>(()=>RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule with{Conditions=[invalid]}])));
    }
    [Fact]public void NativeWatchSummariesSeparatePlayedAndCompletedWithoutInventingHistory(){
        var user=new Jellyfin.Database.Implementations.Entities.User("viewer","auth","reset"){Id=Guid.NewGuid()};var movie=new Movie{Id=Guid.NewGuid(),DateCreated=DateTime.UtcNow.AddDays(-10)};var data=new UserItemData{Key="fixture",Played=false,PlayCount=1,LastPlayedDate=DateTime.UtcNow.AddDays(-1),PlaybackPositionTicks=TimeSpan.FromMinutes(5).Ticks};
        var native=new JellyfinMetadata(Fake<ILibraryManager>(_=>throw new NotSupportedException()),Fake<IUserManager>(m=>m.Name=="GetUsers"?new[]{user}:throw new NotSupportedException()),Fake<IUserDataManager>(m=>m.Name=="GetUserData"?data:throw new NotSupportedException()));
        var fields=new[]{"jellyfin.lastViewedAt","jellyfin.seenSinceAddedBy","jellyfin.completedWatchCount","jellyfin.lastPlayedAt"};var facts=native.Read(movie,fields,default);
        Assert.True(MetadataEvidence.IsAbsent(facts["jellyfin.lastViewedAt"]));Assert.Equal(0,facts["jellyfin.completedWatchCount"].GetInt64());Assert.Equal(data.LastPlayedDate,facts["jellyfin.lastPlayedAt"].GetDateTime());
        data.Played=true;facts=native.Read(movie,fields,default);Assert.Equal(1,facts["jellyfin.completedWatchCount"].GetInt64());Assert.Equal(user.Id.ToString("D"),facts["jellyfin.seenSinceAddedBy"][0].GetString());
        data.LastPlayedDate=null;facts=native.Read(movie,fields,default);Assert.False(facts.ContainsKey("jellyfin.lastViewedAt"));Assert.False(facts.ContainsKey("jellyfin.seenSinceAddedBy"));
    }
    [Fact]public void RemovedSiblingContainersCannotBecomeEmptySuccessfulWatchEvidence(){
        var links=Fake<MediaBrowser.Controller.Persistence.ILinkedChildrenService>(m=>m.Name=="GetManualLinkedParentIds"?new List<Guid>{Guid.NewGuid()}:throw new InvalidOperationException("Children must not be queried"));
        var library=Fake<ILibraryManager>(m=>m.Name=="GetItemById"?null:throw new InvalidOperationException("No broad queries"));
        var native=new JellyfinMetadata(library,Fake<IUserManager>(_=>throw new NotSupportedException()),Fake<IUserDataManager>(_=>throw new NotSupportedException()),null,links);
        Assert.Throws<InvalidOperationException>(()=>native.Read(new Movie{Id=Guid.NewGuid()},["jellyfin.collectionSiblingLastPlayedAt"],default));
    }
    [Fact]public void NativeDetailsReadPeopleStreamsManualMembershipAndProviderAbsence(){
        var movie=new Movie{Id=Guid.NewGuid()};var collection=new MediaBrowser.Controller.Entities.Movies.BoxSet{Id=Guid.NewGuid(),Name="Synthetic collection"};
        var library=Fake<ILibraryManager>(m=>m.Name switch{
            "GetPeople"=>new List<PersonInfo>{new(){Name="Synthetic actor",Type=Jellyfin.Data.Enums.PersonKind.Actor}},
            "GetItemById"=>collection,_=>throw new NotSupportedException(m.Name)});
        var media=Fake<IMediaSourceManager>(m=>m.Name=="GetMediaStreams"?new List<MediaBrowser.Model.Entities.MediaStream>{new(){Type=MediaBrowser.Model.Entities.MediaStreamType.Video,Codec="hevc",Width=3840,Height=2160},new(){Type=MediaBrowser.Model.Entities.MediaStreamType.Audio,Codec="aac",Language="eng",Channels=6}}:throw new NotSupportedException());
        var links=Fake<MediaBrowser.Controller.Persistence.ILinkedChildrenService>(m=>m.Name=="GetManualLinkedParentIds"?new List<Guid>{collection.Id}:throw new NotSupportedException());
        var native=new JellyfinMetadata(library,Fake<IUserManager>(_=>throw new NotSupportedException()),Fake<IUserDataManager>(_=>throw new NotSupportedException()),media,links);
        var facts=native.Read(movie,["jellyfin.actors","jellyfin.videoCodecs","jellyfin.audioLanguages","jellyfin.maxVideoWidth","jellyfin.collections","jellyfin.imdbId"],default);
        Assert.Equal("Synthetic actor",facts["jellyfin.actors"][0].GetString());Assert.Equal("hevc",facts["jellyfin.videoCodecs"][0].GetString());Assert.Equal(3840,facts["jellyfin.maxVideoWidth"].GetInt32());Assert.Equal("Synthetic collection",facts["jellyfin.collections"][0].GetString());Assert.True(MetadataEvidence.IsAbsent(facts["jellyfin.imdbId"]));
        Assert.False(facts.ContainsKey("jellyfin.lastPlayedAt"));
    }
    [Fact]public void ConfirmedAbsenceIsDistinctFromMissingNullAndInvalidEvidence(){
        var c=new Condition("jellyfin.imdbId","not-exists",V(null));var rule=new Rule("absent","Absent",true,"all",[c]);RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule]));
        Assert.True(RuleEngine.Matches(rule,new Dictionary<string,JsonElement>{{c.Field,MetadataEvidence.Absent}},DateTimeOffset.UtcNow));
        Assert.Null(RuleEngine.TestCondition(c,new Dictionary<string,JsonElement>{{c.Field,V(null)}},DateTimeOffset.UtcNow));
        Assert.False(RuleEngine.Matches(rule,new Dictionary<string,JsonElement>(),DateTimeOffset.UtcNow));
        Assert.False(RuleEngine.Matches(rule,new Dictionary<string,JsonElement>{{c.Field,V("tt123")}},DateTimeOffset.UtcNow));
        var not=rule with{Circuit=new(2,[new("metadata","metadata",0,0,[]){Source="jellyfin"},new("test","condition",200,0,["metadata"]){Condition=c with{Operator="exists"}},new("not","not",400,0,["test"]),new("result","delete",600,0,["not"])])};
        Assert.False(RuleEngine.Matches(not,new Dictionary<string,JsonElement>(),DateTimeOffset.UtcNow));Assert.True(RuleEngine.Matches(not,new Dictionary<string,JsonElement>{{c.Field,MetadataEvidence.Absent}},DateTimeOffset.UtcNow));
    }
    [Fact]public void CrossSourceComparisonsRequireCompatibleTypedOperandsAndBothSources(){
        var c=new Condition("added","before",V(null)){ReferenceField="radarr.added"};var graph=new RuleCircuit(2,[new("native","metadata",0,0,[]){Source="jellyfin"},new("remote","metadata",0,200,[]){Source="radarr"},new("test","condition",300,0,["native","remote"]){Condition=c},new("result","delete",600,0,["test"])]);
        var rule=new Rule("compare","Compare",true,"all",[c]){Circuit=graph};RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule]));
        var facts=new Dictionary<string,JsonElement>{{"added",V(DateTimeOffset.UtcNow.AddDays(-2))},{"radarr.added",V(DateTimeOffset.UtcNow.AddDays(-1))}};Assert.True(RuleEngine.Matches(rule,facts,DateTimeOffset.UtcNow));facts.Remove("radarr.added");Assert.False(RuleEngine.Matches(rule,facts,DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(()=>RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule with{Circuit=null,Conditions=[c with{ReferenceField="radarr.sizeOnDisk"}]}])));
        Assert.Throws<InvalidOperationException>(()=>CircuitRules.Validate(graph with{Nodes=graph.Nodes.Select(n=>n.Id=="test"?n with{Inputs=["native","native"]}:n).ToArray()}));
    }
    public class Proxy:DispatchProxy {public Func<MethodInfo,object?> Call=null!;protected override object? Invoke(MethodInfo? method,object?[]? args)=>Call(method!);}
    private static T Fake<T>(Func<MethodInfo,object?> call)where T:class{var value=DispatchProxy.Create<T,Proxy>();((Proxy)(object)value).Call=call;return value;}
    [Fact]public void NativeEpisodeMetadataRejectsRemovedParentsBeforeAnyBroadeningQuery(){
        var series=new Series{Id=Guid.NewGuid()};var exists=false;var queries=0;
        var library=Fake<ILibraryManager>(m=>m.Name switch{"GetItemById"=>exists?series:null,"GetItemList"=>Items(),_=>throw new NotSupportedException()});
        BaseItem[] Items(){queries++;return [new Episode{Id=Guid.NewGuid(),SeriesId=series.Id}];}
        var native=new JellyfinMetadata(library,Fake<IUserManager>(_=>throw new NotSupportedException()),Fake<IUserDataManager>(_=>throw new NotSupportedException()));
        Assert.Throws<InvalidOperationException>(()=>native.Read(series,["jellyfin.episodeCount"],default));Assert.Equal(0,queries);
        exists=true;Assert.Equal(1,native.Read(series,["jellyfin.episodeCount"],default)["jellyfin.episodeCount"].GetInt32());Assert.Equal(1,queries);
    }
    [Fact]public async Task ArrMetadataIsReadOnlyProviderMatchedAndKeysAreNotReturnedOrStoredAsPlaintext(){
        var root=Path.Combine(Path.GetTempPath(),"arr-metadata-"+Guid.NewGuid().ToString("N"));
        var socket=new TcpListener(IPAddress.Loopback,0);socket.Start();var port=((IPEndPoint)socket.LocalEndpoint).Port;socket.Stop();
        using var server=new HttpListener();server.Prefixes.Add($"http://127.0.0.1:{port}/");server.Start();
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try{
            var connections=new ArrConnections(Fake<IApplicationPaths>(m=>m.Name=="get_DataPath"?root:throw new NotSupportedException()),Fake<IPluginManager>(_=>throw new NotSupportedException()));
            var url=$"http://127.0.0.1:{port}";connections.Save("radarr",new(0,url,"fixture-key"));
            Assert.DoesNotContain("fixture-key",File.ReadAllText(Path.Combine(root,"automatic-deletion-requests","metadata","radarr.json")));
            Assert.DoesNotContain("fixture-key",JsonSerializer.Serialize(connections.Status("radarr")));
            Assert.Throws<InvalidOperationException>(()=>connections.Save("radarr",new(0,url,"other")));
            Assert.Throws<InvalidOperationException>(()=>connections.Save("radarr",new(1,"http://different.invalid",null)));
            var response=Task.Run(async()=>{var request=await server.GetContextAsync().WaitAsync(deadline.Token);Assert.Equal("GET",request.Request.HttpMethod);Assert.Equal("fixture-key",request.Request.Headers["X-Api-Key"]);Assert.Equal("/api/v3/movie?tmdbId=42",request.Request.RawUrl);var bytes=System.Text.Encoding.UTF8.GetBytes("""[{"id":9,"tmdbId":999,"title":"Wrong movie"},{"id":10,"tmdbId":42,"title":"Correct movie","monitored":true,"sizeOnDisk":1048576}]""");request.Response.ContentType="application/json";await request.Response.OutputStream.WriteAsync(bytes,deadline.Token);request.Response.Close();},deadline.Token);
            var movie=new Movie{Id=Guid.NewGuid(),Name="Movie"};movie.ProviderIds["Tmdb"]="42";
            var facts=await new ArrMetadata(connections).ReadAsync(movie,["radarr.title","radarr.monitored","radarr.sizeOnDisk"],deadline.Token);await response;
            Assert.Equal("Correct movie",facts["radarr.title"].GetString());Assert.True(facts["radarr.monitored"].GetBoolean());Assert.Equal(1,facts["radarr.sizeOnDisk"].GetDouble());
            var file=Path.Combine(root,"automatic-deletion-requests","metadata","radarr.json");File.Move(file,file+".fixture");
            Assert.Throws<InvalidOperationException>(()=>connections.Read("radarr"));
        }finally{server.Stop();if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static JsonElement V(object? value)=>JsonSerializer.SerializeToElement(value);
    private static RuleCircuit Circuit(string source="jellyfin")=>new(2,[
        new("data","metadata",0,0,[]){Source=source},
        new("condition","condition",300,0,["data"]){Condition=new("title","contains",V("movie"))},
        new("result","delete",600,0,["condition"])
    ]);
    [Fact]public void VersionTwoUsesTypedMetadataWithoutAnExecutionStart(){
        var graph=Circuit();CircuitRules.Validate(graph);
        Assert.True(CircuitRules.Evaluate(graph,_=>true));Assert.False(CircuitRules.Evaluate(graph,_=>false));
        Assert.Null(CircuitRules.Trace(graph,_=>null)["result"]);
        Assert.Throws<InvalidOperationException>(()=>CircuitRules.Validate(Circuit("sonarr")));
        Assert.Throws<InvalidOperationException>(()=>CircuitRules.Validate(graph with{Nodes=[graph.Nodes[0],graph.Nodes[1] with{Inputs=[]},graph.Nodes[2]]}));
        Assert.Throws<InvalidOperationException>(()=>CircuitRules.Validate(graph with{Nodes=[graph.Nodes[0],graph.Nodes[1],graph.Nodes[2] with{Inputs=["data"]}]}));
    }
    [Theory][InlineData("contains","drama",true)][InlineData("not-contains","drama",false)][InlineData("count-greater",1,true)][InlineData("count-equals",2,true)]
    public void ListOperatorsUseTypedDataAndUnknownDoesNotBecomeTrue(string op,object target,bool expected){
        var condition=new Condition("jellyfin.genres",op,V(target));var rule=new Rule("genres","Genres",true,"all",[condition]);
        RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule]));
        Assert.Equal(expected,RuleEngine.Matches(rule,new Dictionary<string,JsonElement>{[condition.Field]=V(new[]{"Drama","Comedy"})},DateTimeOffset.UtcNow));
        Assert.False(RuleEngine.Matches(rule,new Dictionary<string,JsonElement>{[condition.Field]=V(null)},DateTimeOffset.UtcNow));
    }
    [Fact]public void CatalogHasUniqueTypedFieldsAndScopedOperators(){
        Assert.Equal(MetadataCatalog.Fields.Length,MetadataCatalog.Fields.Select(f=>f.Id).Distinct().Count());
        Assert.All(MetadataCatalog.Fields,f=>{Assert.Contains(f.Source,MetadataCatalog.Sources);Assert.NotEmpty(f.Operators);});
        Assert.DoesNotContain("contains",MetadataCatalog.Find("sonarr.monitored")!.Operators);
        Assert.DoesNotContain("greater",MetadataCatalog.Find("radarr.added")!.Operators);
    }
    public sealed class EnhancedLegacy { public string SonarrUrl{get;set;}="https://sonarr.invalid"; public string SonarrApiKey{get;set;}="secret";public string SonarrInstances{get;set;}="[]"; }
    [Fact]public void EnhancedImportRejectsAmbiguityAndUnsafeUrls(){
        Assert.NotNull(ArrConnections.Discover(new EnhancedLegacy(),"sonarr"));
        Assert.Null(ArrConnections.Discover(new EnhancedLegacy{SonarrInstances="[{\"Url\":\"https://one.invalid\"},{\"Url\":\"https://two.invalid\"}]"},"sonarr"));
        Assert.Null(ArrConnections.Discover(new EnhancedLegacy{SonarrInstances="bad-json"},"sonarr"));
        Assert.False(ArrConnections.Valid("https://user:pass@host.invalid","secret"));
        Assert.False(ArrConnections.Valid("https://host.invalid?key=secret","secret"));
        Assert.False(ArrConnections.Valid("https://host.invalid","bad\nkey"));
    }
}
