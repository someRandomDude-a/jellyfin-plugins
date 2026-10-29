using System.Reflection;
using System.Text.Json;
using AccountHub.Api;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.AutomaticDeletionRequests;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Tasks;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.DependencyInjection;

namespace AutomaticDeletionRequests.Tests;

public sealed class LiveRuleTests
{
    public class Proxy : DispatchProxy {
        public Func<MethodInfo, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!, args ?? []);
    }
    private static T Fake<T>(Func<MethodInfo, object?[], object?> call) where T : class { var value = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)value).Call = call; return value; }
    [Fact] public void SaveBindsAuthorizationToNativeSessionAndKeepsLibrariesOnFilterCards() {
        var root=Path.Combine(Path.GetTempPath(),"automatic-session-"+Guid.NewGuid().ToString("N"));
        using var provider=new ServiceCollection().BuildServiceProvider();
        try {
            var store=new RuleStore(Fake<IApplicationPaths>((m,_)=>m.Name=="get_DataPath"?root:throw new NotSupportedException()));
            var actor=new User("fixture","auth","reset"){Id=Guid.NewGuid()};
            var users=Fake<IUserManager>((m,_)=>m.Name switch {"GetUserById"=>actor,"GetUserDto"=>new UserDto{Id=actor.Id,Policy=new UserPolicy{IsAdministrator=true}},_=>throw new NotSupportedException()});
            var context=new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.User=new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                new("Jellyfin-UserId",actor.Id.ToString()),new("Jellyfin-IsApiKey","false")],"fixture"));
            var controller=new RuleController(store,null!,users,new(provider),null!){ControllerContext=new(){HttpContext=context}};
            var filter=new LibraryFilter("scope","Movies",Guid.NewGuid(),new("all","All",false,"all",[new("title","contains",JsonSerializer.SerializeToElement(""))]),[]);
            controller.Save(new(new(0,false,Guid.NewGuid(),Guid.NewGuid(),24,[]){Filters=[filter]},false));
            var saved=store.Read(s=>s.Configuration);
            Assert.Equal(actor.Id,saved.ActorId); Assert.Equal(Guid.Empty,saved.LibraryId);
            Assert.Equal(filter.LibraryId,Assert.Single(saved.Filters!).LibraryId);
            Assert.Throws<InvalidOperationException>(()=>controller.Save(new(saved with{Revision=0},false)));
            context.User=new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([
                new("Jellyfin-UserId",actor.Id.ToString()),new("Jellyfin-IsApiKey","true")],"fixture"));
            Assert.Throws<UnauthorizedAccessException>(()=>controller.Save(new(saved,false)));
        } finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
    [Fact] public async Task LiveRuleChecksRejectChangedMetadataDisabledRulesAndOutOfScopeItems() {
        var root = Path.Combine(Path.GetTempPath(), "automatic-live-rule-" + Guid.NewGuid().ToString("N"));
        using var provider = new ServiceCollection().BuildServiceProvider();
        try {
            var paths = Fake<IApplicationPaths>((m, _) => m.Name == "get_DataPath" ? root : throw new NotSupportedException());
            var store = new RuleStore(paths); var actor = new User("fixture", "auth", "reset") { Id = Guid.NewGuid() };
            var movie = new Movie { Id = Guid.NewGuid(), Name = "Old movie", ProductionYear = 1990 }; var inScope = true;
            var enabled = true; var administrator = true;
            var users = Fake<IUserManager>((m, _) => m.Name switch { "GetUserById" => actor, "GetUserDto" => new UserDto { Id = actor.Id, Policy = new UserPolicy { IsAdministrator = administrator, IsDisabled = !enabled } }, _ => throw new NotSupportedException() });
            var library = Fake<ILibraryManager>((m, _) => m.Name == "GetVirtualFolders" ? VirtualFolders(m,store.Read(s=>s.Configuration.LibraryId)) : m.Name == "GetItemList" ? inScope ? new BaseItem[] { movie } : Array.Empty<BaseItem>() : throw new NotSupportedException());
            var rule = new Rule("old", "Old movies", true, "all", [new("year", "less", JsonSerializer.SerializeToElement(2000))]);
            store.Write(s => { s.Configuration = new(1, true, actor.Id, Guid.NewGuid(), 24, [rule]); return true; });
            var runner = new RuleRunner(store, library, users, new(provider)); var check = Contract.Serialize(new AutomaticRuleCheck(actor.Id, movie.Id, rule.Id));
            async Task<bool> Matches() { using var result = JsonDocument.Parse(await runner.ValidateAsync(check, default)); return result.RootElement.GetProperty("matches").GetBoolean(); }
            Assert.True(await Matches()); movie.ProductionYear = 2026; Assert.False(await Matches()); movie.ProductionYear = 1990;
            enabled = false; Assert.False(await Matches()); enabled = true;
            administrator = false; Assert.False(await Matches()); administrator = true;
            using (var forged = JsonDocument.Parse(await runner.ValidateAsync(Contract.Serialize(new AutomaticRuleCheck(Guid.NewGuid(), movie.Id, rule.Id)), default))) Assert.False(forged.RootElement.GetProperty("matches").GetBoolean());
            inScope = false; Assert.False(await Matches()); inScope = true;
            store.Write(s => { s.Configuration = s.Configuration with { Revision = 2, Rules = [rule with { Enabled = false }] }; return true; }); Assert.False(await Matches());
            store.Write(s => { s.Configuration = s.Configuration with { Revision = 3, Enabled = false, Rules = [rule] }; return true; }); Assert.False(await Matches());
        } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact] public void RuleJobRunsDailyBeforeSafetyAndArtworkJobs() {
        var task = new RuleMaintenanceTask(null!, null!, null!); var trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type); Assert.Equal(TimeSpan.FromHours(2).Ticks, trigger.TimeOfDayTicks);
    }
    [Fact] public async Task MultipleFilterInputsUnionItemsAndLiveChecksRevalidateFilters() {
        var root=Path.Combine(Path.GetTempPath(),"automatic-routing-"+Guid.NewGuid().ToString("N"));
        using var provider=new ServiceCollection().BuildServiceProvider();
        try {
            var store=new RuleStore(Fake<IApplicationPaths>((m,_)=>m.Name=="get_DataPath"?root:throw new NotSupportedException()));
            var actor=new User("fixture","auth","reset"){Id=Guid.NewGuid()};var scope=Guid.NewGuid();
            var movie=new Movie{Id=Guid.NewGuid(),Name="Fixture",ProductionYear=1990};
            var users=Fake<IUserManager>((m,_)=>m.Name switch {"GetUserById"=>actor,"GetUserDto"=>new UserDto{Id=actor.Id,Policy=new UserPolicy{IsAdministrator=true}},_=>throw new NotSupportedException()});
            var rootExists=true;var queryCount=0;
            var library=Fake<ILibraryManager>((m,a)=>{if(m.Name=="GetVirtualFolders")return VirtualFolders(m,rootExists?scope:Guid.Empty);if(m.Name!="GetItemList")throw new NotSupportedException();queryCount++;return ((InternalItemsQuery)a[0]!).ParentId==scope?new BaseItem[]{movie}:Array.Empty<BaseItem>();});
            var rule=new Rule("r","Rule",true,"all",[new("year","less",JsonSerializer.SerializeToElement(2000))]);
            var filter=new LibraryFilter("a","Filter",scope,rule with{Id="predicate"},[rule.Id]);
            store.Write(s=>{s.Configuration=new(1,true,actor.Id,Guid.Empty,24,[rule]){Filters=[filter,filter with{Id="b"}]};return true;});
            var runner=new RuleRunner(store,library,users,new(provider));
            var preview=JsonSerializer.SerializeToElement(await runner.RunAsync(true,default));Assert.Single(preview.GetProperty("matches").EnumerateArray());
            var request=Contract.Serialize(new AutomaticRuleCheck(actor.Id,movie.Id,rule.Id));
            using(var result=JsonDocument.Parse(await runner.ValidateAsync(request,default)))Assert.True(result.RootElement.GetProperty("matches").GetBoolean());
            var candidates=JsonSerializer.SerializeToElement(await runner.TestableAsync(filter,"Fixture",default));Assert.Single(candidates.GetProperty("items").EnumerateArray());
            movie.ProductionYear=2026;
            using(var result=JsonDocument.Parse(await runner.ValidateAsync(request,default)))Assert.False(result.RootElement.GetProperty("matches").GetBoolean());
            candidates=JsonSerializer.SerializeToElement(await runner.TestableAsync(filter,"Fixture",default));Assert.Empty(candidates.GetProperty("items").EnumerateArray());
            movie.ProductionYear=1990;
            rootExists=false;var before=queryCount;
            await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.ValidateAsync(request,default));Assert.Equal(before,queryCount);
            await Assert.ThrowsAsync<InvalidOperationException>(()=>runner.TestableAsync(filter,"Fixture",default));Assert.Equal(before,queryCount);
            rootExists=true;
            store.Write(s=>{s.Configuration=s.Configuration with{Revision=2,Filters=[]};return true;});
            using(var result=JsonDocument.Parse(await runner.ValidateAsync(request,default)))Assert.False(result.RootElement.GetProperty("matches").GetBoolean());
        } finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static object VirtualFolders(MethodInfo method,Guid id) {
        var folders=new List<MediaBrowser.Model.Entities.VirtualFolderInfo>{new(){ItemId=id.ToString()}};
        return method.ReturnType.IsArray?folders.ToArray():folders;
    }
}
