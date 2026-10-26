using System.Security.Claims;
using System.Text.Json;
using AccountHub.Api;
using Jellyfin.Data.Enums;
using Jellyfin.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public static class NativeAccess
{
    public static void RequireLibraries(ILibraryManager library,IEnumerable<Guid> ids) {
        var selected=ids.Distinct().ToArray();if(selected.Length==0)return;
        var roots=library.GetVirtualFolders().Select(f=>Guid.TryParse(f.ItemId,out var id)?id:Guid.Empty).Where(id=>id!=Guid.Empty).ToHashSet();
        if(selected.Any(id=>id==Guid.Empty||!roots.Contains(id)))throw new InvalidOperationException("Library scope unavailable");
    }
    public static bool Administrator(IUserManager users, Guid id) {
        var user = users.GetUserById(id); if (user is null) return false;
        var policy = users.GetUserDto(user).Policy;
        return policy is { IsDisabled: false, IsAdministrator: true };
    }
    public static void Require(IUserManager users, ClaimsPrincipal principal) {
        if (principal.Identity?.IsAuthenticated != true || !bool.TryParse(principal.FindFirstValue("Jellyfin-IsApiKey"), out var apiKey) || apiKey ||
            !Guid.TryParse(principal.FindFirstValue("Jellyfin-UserId"), out var id) || !Administrator(users, id)) throw new UnauthorizedAccessException();
    }
}
public sealed class RuleRunner(RuleStore store, ILibraryManager library, IUserManager users, DeletionRequestsClient queue, ArrMetadata? arr=null,JellyfinMetadata? native=null)
{
    private async Task<Dictionary<string,JsonElement>> FactsAsync(BaseItem item,IEnumerable<Condition> conditions,CancellationToken ct) {
        var facts=NativeFacts(item);
        var selected=conditions.ToArray();
        if(native is not null)try{foreach(var field in native.Read(item,MetadataEvidence.Fields(selected),ct))facts[field.Key]=field.Value;}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{/* Incomplete native aggregates remain unknown, independently of scalar facts. */}
        if(arr is not null)try{foreach(var field in await arr.ReadAsync(item,MetadataEvidence.Fields(selected),ct))facts[field.Key]=field.Value;}
        catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
        catch{/* Unavailable external metadata remains unknown; native facts survive. */}
        return facts;
    }
    public async Task<object> TestableAsync(LibraryFilter filter,string search,CancellationToken ct,Guid? authorizer=null) {
        RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[]) {Filters=[filter with{Outputs=[]}]});
        NativeAccess.RequireLibraries(library,[filter.LibraryId]);
        if(search.Length>100)throw new InvalidOperationException("Search too long");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var query=new InternalItemsQuery{ParentId=filter.LibraryId,Recursive=true,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Series],SearchTerm=search,Limit=51};
        if(Guid.TryParse(search,out var itemId)){query.ItemIds=[itemId];query.SearchTerm=null;}
        var candidates=library.GetItemList(query);var result=new List<object>();var config=store.Read(s=>s.Configuration);var now=DateTimeOffset.UtcNow;
        foreach(var item in candidates.Take(50)) {
            deadline.Token.ThrowIfCancellationRequested();var evidence=await FactsAsync(item,RuleEngine.GetConditions(filter.Predicate),deadline.Token);
            if(MetadataEvidence.Fields(RuleEngine.GetConditions(filter.Predicate)).Any(f=>f.StartsWith("seerr.",StringComparison.Ordinal))) {
                try {var extra=await queue.SubmitAsync(new(Guid.NewGuid(),config.ActorId==Guid.Empty?authorizer??Guid.Empty:config.ActorId,item.Id,filter.Predicate.Id){PreviewOnly=true},deadline.Token);foreach(var fact in extra.EnumerateObject())evidence[fact.Name]=fact.Value.Clone();}
                catch(OperationCanceledException)when(deadline.IsCancellationRequested){throw;}
                catch { /* Unknown evidence cannot enter a test path. */ }
            }
            if(RuleEngine.Matches(filter.Predicate,evidence,now))result.Add(new{item.Id,title=item.Name,year=item.ProductionYear});
        }
        return new{items=result,limited=candidates.Count>50,previewOnly=true};
    }
    private static LibraryFilter[] Routes(Configuration config, Rule rule) => config.Filters is null
        ? [new("legacy","Library",rule.LibraryId ?? config.LibraryId,new("scope","All items",false,"all",[new("title","contains",JsonSerializer.SerializeToElement(""))]),[rule.Id])]
        : config.Filters.Where(f=>f.Outputs.Contains(rule.Id)).ToArray();
    public async Task<object> TestAsync(Rule rule, Guid itemId, Guid libraryId, CancellationToken ct, LibraryFilter[]? filters=null,Guid? authorizer=null) {
        RuleEngine.Validate(new(0,false,Guid.Empty,Guid.Empty,24,[rule]) { Filters=filters?.Select(f=>f with{Outputs=f.Outputs.Where(id=>id==rule.Id).ToArray()}).ToArray() });
        if(rule.Circuit is null || itemId==Guid.Empty || libraryId==Guid.Empty)throw new InvalidOperationException("Select media and a circuit");
        NativeAccess.RequireLibraries(library,new[]{libraryId}.Concat((filters??[]).Select(f=>f.LibraryId)));
        var item=library.GetItemList(new InternalItemsQuery { ParentId=libraryId,Recursive=true,ItemIds=[itemId],IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Series],Limit=2 }).SingleOrDefault() ?? throw new InvalidOperationException("Media outside selected library");
        var evidence=await FactsAsync(item,RuleEngine.GetConditions(rule).Concat((filters??[]).SelectMany(f=>RuleEngine.GetConditions(f.Predicate))),ct);var config=store.Read(s=>s.Configuration);
        if(MetadataEvidence.Fields(RuleEngine.GetConditions(rule).Concat((filters??[]).SelectMany(f=>RuleEngine.GetConditions(f.Predicate)))).Any(f=>f.StartsWith("seerr.",StringComparison.Ordinal))) {
            try { var extra=await queue.SubmitAsync(new(Guid.NewGuid(),config.ActorId==Guid.Empty?authorizer??Guid.Empty:config.ActorId,item.Id,rule.Id){PreviewOnly=true},ct);foreach(var fact in extra.EnumerateObject())evidence[fact.Name]=fact.Value.Clone(); }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){throw;}
            catch { /* Unavailable evidence stays unknown, including under NOT. */ }
        }
        var now=DateTimeOffset.UtcNow;
        var trace=CircuitRules.Trace(rule.Circuit,c=>RuleEngine.TestCondition(c,evidence,now));
        bool? scoped=filters is null?true:false;
        foreach(var filter in filters??[]) {
            var member=library.GetItemList(new InternalItemsQuery{ParentId=filter.LibraryId,Recursive=true,ItemIds=[itemId],Limit=2}).Any(i=>i.Id==itemId);
            bool? result=!member?false:RuleEngine.GetConditions(filter.Predicate).Any(c=>RuleEngine.TestCondition(c,evidence,now) is null)?null:RuleEngine.Matches(filter.Predicate,evidence,now);
            trace["filter:"+filter.Id]=result;
            if(result==true)scoped=true;else if(result is null&&scoped!=true)scoped=null;
        }
        if(scoped!=true){if(rule.Circuit.Nodes.SingleOrDefault(n=>n.Kind=="start") is {} start)trace[start.Id]=scoped;trace[rule.Circuit.Nodes.Single(n=>n.Kind=="delete").Id]=scoped;}
        return new { itemId=item.Id,title=item.Name,trace,matches=scoped==true&&RuleEngine.Matches(rule,evidence,now),previewOnly=true };
    }
    private readonly SemaphoreSlim gate = new(1);
    private static Dictionary<string, JsonElement> NativeFacts(BaseItem item) {
        var facts=JsonSerializer.SerializeToElement(new {
        title = item.Name, year = item.ProductionYear,
        added = item.DateCreated == default ? (DateTimeOffset?)null : new DateTimeOffset(DateTime.SpecifyKind(item.DateCreated, DateTimeKind.Utc)),
        rating = item.CommunityRating, kind = item is Movie ? "movie" : "series"
        }).EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        void Put(string name,object? value){facts["jellyfin."+name]=value is null?MetadataEvidence.Absent:JsonSerializer.SerializeToElement(value);}
        Put("genres",item.Genres);Put("tags",item.Tags);Put("studios",item.Studios);
        Put("officialRating",item.OfficialRating);Put("criticRating",item.CriticRating);
        Put("premiereDate",item.PremiereDate);Put("endDate",item.EndDate);Put("originalTitle",item.OriginalTitle);
        Put("productionLocations",item.ProductionLocations);
        Put("runtimeMinutes",item.RunTimeTicks is {} ticks?TimeSpan.FromTicks(ticks).TotalMinutes:null);
        return facts;
    }

    public async Task<string> ValidateAsync(string json, CancellationToken ct) {
        if (json.Length > 4096) throw new InvalidOperationException("Invalid rule verification request");
        var request = JsonSerializer.Deserialize<AutomaticRuleCheck>(json, Contract.Json) ?? throw new InvalidOperationException("Invalid rule verification request");
        await gate.WaitAsync(ct);
        try {
            var snapshot = store.Read(s => s.Configuration); RuleEngine.Validate(snapshot);
            var rule = snapshot.Rules.SingleOrDefault(r => r.Id == request.RuleId && r.Enabled);
            if (!snapshot.Enabled || rule is null || snapshot.ActorId != request.ActorId || !NativeAccess.Administrator(users, snapshot.ActorId)) return Contract.Serialize(new { matches = false });
            var routes=Routes(snapshot,rule);
            NativeAccess.RequireLibraries(library,routes.Select(f=>f.LibraryId));
            var memberships=routes.ToDictionary(f=>f.Id,f=>library.GetItemList(new InternalItemsQuery { ParentId=f.LibraryId,Recursive=true,ItemIds=[request.ItemId],IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Series],Limit=2 }).Any(i=>i.Id==request.ItemId));
            var item = routes.Where(f=>memberships[f.Id]).SelectMany(f=>library.GetItemList(new InternalItemsQuery {ParentId=f.LibraryId,Recursive=true,ItemIds=[request.ItemId],Limit=2})).FirstOrDefault(i=>i.Id==request.ItemId);
            if (item is null) return Contract.Serialize(new { matches = false });
            var evidence = await FactsAsync(item,RuleEngine.GetConditions(rule).Concat(routes.SelectMany(f=>RuleEngine.GetConditions(f.Predicate))),ct);
            if (MetadataEvidence.Fields(RuleEngine.GetConditions(rule).Concat(routes.SelectMany(f=>RuleEngine.GetConditions(f.Predicate)))).Any(f => f.StartsWith("seerr.", StringComparison.Ordinal))) {
                var extra = await queue.SubmitAsync(new(Guid.NewGuid(), snapshot.ActorId, item.Id, rule.Id) { PreviewOnly = true }, ct);
                foreach (var field in extra.EnumerateObject()) evidence[field.Name] = field.Value.Clone();
            }
            var matches = routes.Any(f=>memberships[f.Id]&&RuleEngine.Matches(f.Predicate,evidence,DateTimeOffset.UtcNow)) && RuleEngine.Matches(rule, evidence, DateTimeOffset.UtcNow) && store.Read(s => s.Configuration.Enabled && s.Configuration.Revision == snapshot.Revision);
            return Contract.Serialize(new { matches });
        } finally { gate.Release(); }
    }
    public async Task<object> RunAsync(bool preview, CancellationToken ct) {
        await gate.WaitAsync(ct);
        try {
            var snapshot = store.Read(s => s.Configuration); RuleEngine.Validate(snapshot);
            if (snapshot.LibraryId == Guid.Empty && snapshot.Filters is null) throw new InvalidOperationException("Choose a library first");
            if (!preview && (!snapshot.Enabled || !NativeAccess.Administrator(users, snapshot.ActorId) || !queue.Available)) throw new InvalidOperationException("Automation is disabled or dependencies are unavailable");
            NativeAccess.RequireLibraries(library,snapshot.Rules.Where(r=>r.Enabled).SelectMany(r=>Routes(snapshot,r)).Select(f=>f.LibraryId));
            var scopes = snapshot.Rules.Where(r=>r.Enabled).SelectMany(r=>Routes(snapshot,r)).Select(f=>f.LibraryId).Distinct().ToDictionary(id=>id,id=>library.GetItemList(new InternalItemsQuery { ParentId=id,Recursive=true,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Series],Limit=201 }).ToArray());
            if(scopes.Values.Any(items=>items.Length>200))throw new InvalidOperationException("Preview capacity exceeded; no requests submitted. Narrow the library scope.");
            var now = DateTimeOffset.UtcNow; var matches = new List<object>(); var count = 0;
            var remoteFacts = new Dictionary<Guid, Dictionary<string, JsonElement>>();
            foreach (var rule in snapshot.Rules.Where(r => r.Enabled)) foreach (var item in Routes(snapshot,rule).SelectMany(f=>scopes[f.LibraryId]).DistinctBy(i=>i.Id)) {
                ct.ThrowIfCancellationRequested();
                var evidence = await FactsAsync(item,RuleEngine.GetConditions(rule).Concat(Routes(snapshot,rule).SelectMany(f=>RuleEngine.GetConditions(f.Predicate))),ct);
                if (MetadataEvidence.Fields(RuleEngine.GetConditions(rule).Concat(Routes(snapshot,rule).SelectMany(f=>RuleEngine.GetConditions(f.Predicate)))).Any(f => f.StartsWith("seerr.", StringComparison.Ordinal))) {
                    if (!remoteFacts.TryGetValue(item.Id, out var extra)) {
                        extra = []; remoteFacts[item.Id] = extra;
                        try {
                            var response = await queue.SubmitAsync(new(Guid.NewGuid(), snapshot.ActorId, item.Id, rule.Id) { PreviewOnly = true }, ct);
                            foreach (var field in response.EnumerateObject()) extra[field.Name] = field.Value.Clone();
                        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception) { /* Missing service/evidence is never a match. */ }
                    }
                    foreach (var field in extra) evidence[field.Key] = field.Value;
                }
                if (!Routes(snapshot,rule).Any(f=>scopes[f.LibraryId].Any(i=>i.Id==item.Id)&&RuleEngine.Matches(f.Predicate,evidence,now)) || !RuleEngine.Matches(rule, evidence, now)) continue;
                var prior = store.Read(s => s.Submitted.GetValueOrDefault(rule.Id)?.Contains(item.Id) == true);
                matches.Add(new { ruleId = rule.Id, itemId = item.Id, title = item.Name, alreadySubmitted = prior });
                if (preview || prior || count >= 20) continue;
                if (!store.Read(s => s.Configuration.Enabled && s.Configuration.Revision == snapshot.Revision)) throw new InvalidOperationException("Configuration changed; run stopped");
                await queue.SubmitAsync(new(Guid.NewGuid(), snapshot.ActorId, item.Id, rule.Id), ct);
                store.Write(s => { if (!s.Submitted.TryGetValue(rule.Id, out var ids)) s.Submitted[rule.Id] = ids = []; if (ids.Count >= 10000) throw new InvalidOperationException("Submission history capacity reached"); ids.Add(item.Id); return true; });
                count++;
            }
            if (!preview) store.Write(s => { s.LastRun = now; return true; });
            return new { preview, submitted = count, matches };
        } finally { gate.Release(); }
    }
}
public sealed record SaveRules(Configuration Configuration, bool ConfirmAutomation);
public sealed record TestCircuit(Rule Rule, Guid ItemId, Guid LibraryId) { public LibraryFilter[]? Filters {get;init;} }
public sealed record TestableMedia(LibraryFilter Filter,string Search);
public sealed class RuleApiFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context) {
        if (context.Exception is OperationCanceledException) return;
        context.Result = context.Exception is UnauthorizedAccessException ? new ForbidResult() :
            new ObjectResult(new { reason = "Rule operation unavailable. Review configuration, revision and dependencies." }) { StatusCode = context.Exception is InvalidOperationException ? 409 : 503 };
        context.ExceptionHandled = true;
    }
}
[ApiController, Authorize, RequestSizeLimit(262144), TypeFilter(typeof(RuleApiFilter)), Route("AutomaticDeletionRequests/admin")]
public sealed class RuleController(RuleStore store, RuleRunner runner, IUserManager users, DeletionRequestsClient queue, ILibraryManager library) : ControllerBase
{
    [HttpPost("media/testable")] public async Task<IActionResult> Testable(TestableMedia body,CancellationToken ct) {NativeAccess.Require(users,User);Response.Headers.CacheControl="no-store";return Ok(await runner.TestableAsync(body.Filter,body.Search,ct,Guid.Parse(User.FindFirstValue("Jellyfin-UserId")!)));}
    [HttpPost("test")] public async Task<IActionResult> Test(TestCircuit body,CancellationToken ct) { NativeAccess.Require(users,User);Response.Headers.CacheControl="no-store";return Ok(await runner.TestAsync(body.Rule,body.ItemId,body.LibraryId,ct,body.Filters,Guid.Parse(User.FindFirstValue("Jellyfin-UserId")!))); }
    [HttpGet("media")] public IActionResult Media([FromQuery]Guid libraryId,[FromQuery]string search="") {
        NativeAccess.Require(users,User);Response.Headers.CacheControl="no-store";
        if(libraryId==Guid.Empty||search.Length>100)return BadRequest();
        NativeAccess.RequireLibraries(library,[libraryId]);
        return Ok(library.GetItemList(new InternalItemsQuery{ParentId=libraryId,Recursive=true,SearchTerm=search,IncludeItemTypes=[BaseItemKind.Movie,BaseItemKind.Series],Limit=50}).Select(i=>new{i.Id,title=i.Name,year=i.ProductionYear}));
    }
    [HttpGet] public IActionResult Get() {
        NativeAccess.Require(users, User);
        return Ok(new { configuration = store.Read(s => s.Configuration), queueAvailable = queue.Available, fields = RuleEngine.Fields, metadata = MetadataCatalog.Fields,
            metadataUsers=users.GetUsers().Where(u=>!u.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled)).Take(500).Select(u=>new{u.Id,name=u.Username}),
            libraries = library.GetVirtualFolders().Where(f => Guid.TryParse(f.ItemId, out _)).Take(500).Select(f => new { id = Guid.Parse(f.ItemId), f.Name }),
            status = "Rules are rechecked before every stage and deletion. Missing evidence blocks progress." });
    }
    [HttpPut] public IActionResult Save(SaveRules body) {
        NativeAccess.Require(users, User);
        if (body.Configuration is null) throw new InvalidOperationException("Invalid rule configuration");
        // The authenticated administrator authorizes automation; this identity is
        // not Seerr requester metadata and cannot be supplied by the browser.
        var saved = store.Read(s => s.Configuration);
        var configuration = body.Configuration with {
            ActorId = saved.ActorId == Guid.Empty || !NativeAccess.Administrator(users,saved.ActorId) ? Guid.Parse(User.FindFirstValue("Jellyfin-UserId")!) : saved.ActorId,
            LibraryId = saved.LibraryId
        };
        RuleEngine.Validate(configuration);
        if (Request.Headers.TryGetValue("Origin", out var origin) && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Authority != Request.Host.Value || uri.Scheme != Request.Scheme)) return Forbid();
        if (configuration.Enabled && (!body.ConfirmAutomation || !queue.Available || !NativeAccess.Administrator(users, configuration.ActorId))) return BadRequest(new { reason = "Explicit confirmation, an authorized automation identity and Deletion Queue are required" });
        var updated = store.Write(s => { NativeAccess.Require(users, User); if (s.Configuration.Revision != body.Configuration.Revision || s.Configuration.Revision != saved.Revision) throw new InvalidOperationException("Revision changed");
            if(s.Configuration.Rules.Any(old=>body.Configuration.Filters?.Any(f=>f.Id==old.Id)==true) || (s.Configuration.Filters??[]).Any(old=>body.Configuration.Rules.Any(r=>r.Id==old.Id)))throw new InvalidOperationException("Card type cannot change");
            s.Configuration = configuration with { Revision = s.Configuration.Revision + 1 }; return s.Configuration; });
        return Ok(updated);
    }
    [HttpPost("preview")] public async Task<IActionResult> Preview(CancellationToken ct) { NativeAccess.Require(users, User); return Ok(await runner.RunAsync(true, ct)); }
}
