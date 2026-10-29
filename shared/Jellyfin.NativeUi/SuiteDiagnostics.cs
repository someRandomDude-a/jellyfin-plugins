using System.Text.Json;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using MediaBrowser.Common.Api;
using Jellyfin.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.PluginSupport;

// Compiled into each independent plugin. Only framework JSON delegates cross
// load contexts; one owner registration and one server-wide SQLite file are used.
internal static class SuiteDiagnostics
{
    internal const string ServiceKey="Suite.Diagnostics.1";
    internal static readonly IReadOnlyDictionary<string,(string Prefix,string Page)> Plugins=new Dictionary<string,(string,string)> {
        ["account-hub"]=("AccountHub","AccountHub"),["notifications"]=("Notifications","Notifications"),
        ["discord-bridge"]=("DiscordBridge","DiscordBridge"),["private-syncplay"]=("PrivateSyncPlay","privateSyncPlay"),
        ["deletion-queue"]=("DeletionQueue","DeletionQueueAdmin"),["automatic-deletion-requests"]=("AutomaticDeletionRequests","AutomaticDeletionRequests")
    };
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static void Register(IServiceCollection services,string source)
    {
        if(!Plugins.ContainsKey(source))throw new ArgumentException("Unknown diagnostics owner");
        services.AddKeyedSingleton<Func<string>>(ServiceKey+".Plugin."+source,(_,_)=>()=>Plugins[source].Page);
        services.AddKeyedSingleton<Func<string>>(ServiceKey+".Version."+source,(_,_)=>()=>typeof(SuiteDiagnostics).Assembly.GetName().Version?.ToString()??"");
        services.AddKeyedSingleton<Func<string,bool>>(ServiceKey+".Category."+source,(_,_)=>name=>typeof(SuiteDiagnostics).Assembly.GetType(name,false) is not null);
        if(services.Any(value=>value.ServiceType==typeof(Func<string,string>)&&Equals(value.ServiceKey,ServiceKey)))return;
        services.AddKeyedSingleton<Func<string,string>>(ServiceKey,(provider,_)=>{
            var paths=provider.GetRequiredService<IApplicationPaths>();var store=new DiagnosticStore(Path.Combine(paths.PluginConfigurationsPath,"SuiteDiagnostics"));
            return payload=>{var value=JsonSerializer.Deserialize<DiagnosticRecord>(payload,Json)??throw new ArgumentException("Missing diagnostic");return store.Record(value);};
        });
        services.AddSingleton(provider=>new DiagnosticStore(Path.Combine(provider.GetRequiredService<IApplicationPaths>().PluginConfigurationsPath,"SuiteDiagnostics")));
        services.AddSingleton<IStartupFilter,DiagnosticsStartup>();
        services.AddSingleton<ILoggerProvider>(provider=>new DiagnosticsLoggerProvider(provider));
        // The shared store owns its recording threshold. Native host defaults
        // must not discard debug events before this provider can apply it;
        // other providers keep their existing native filters and levels.
        services.AddLogging(builder=>builder.AddFilter<DiagnosticsLoggerProvider>(level=>level>=LogLevel.Debug));
        services.Configure<MvcOptions>(options=>{options.Filters.Add<DiagnosticsResultFilter>(int.MaxValue);options.Filters.Add<DiagnosticsExceptionFilter>();});
        services.AddControllers().ConfigureApplicationPartManager(manager=>manager.FeatureProviders.Add(new DiagnosticsControllerOwner(typeof(SuiteDiagnosticsController))));
    }
    private sealed class DiagnosticsControllerOwner(Type owner):IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts,ControllerFeature feature){foreach(var controller in feature.Controllers.Where(value=>value.FullName==typeof(SuiteDiagnosticsController).FullName&&value.AsType()!=owner).ToArray())feature.Controllers.Remove(controller);}
    }
    internal static string? Source(HttpContext context)
    {
        var first=context.Request.Path.Value?.Split('/',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if(string.Equals(first,"UserManager",StringComparison.OrdinalIgnoreCase))return "account-hub";
        var source=Plugins.FirstOrDefault(value=>string.Equals(value.Value.Prefix,first,StringComparison.OrdinalIgnoreCase)).Key;
        if(source is not null)return source;
        first=(context.GetEndpoint() as Microsoft.AspNetCore.Routing.RouteEndpoint)?.RoutePattern.RawText?.Split('/',StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return Plugins.FirstOrDefault(value=>string.Equals(value.Value.Prefix,first,StringComparison.OrdinalIgnoreCase)).Key;
    }
    internal static string Record(HttpContext context,string source,string code,Exception? error=null)
    {
        if(context.Items.TryGetValue(ServiceKey,out var prior)&&prior is string existing)return existing;
        var id=Guid.NewGuid().ToString("D");context.Items[ServiceKey]=id;
        var operation=context.GetEndpoint()?.DisplayName??context.Request.Method+" "+Plugins[source].Prefix;
        // Exception messages, filesystem paths, HTTP bodies, headers, query strings,
        // usernames, tokens and third-party response bodies are never captured.
        var frames=error is null?null:string.Join('\n',new System.Diagnostics.StackTrace(error,false).GetFrames().Take(16).Select(frame=>frame.GetMethod()).Where(method=>method is not null).Select(method=>method!.DeclaringType?.FullName+"."+method.Name));
        var row=new DiagnosticRecord(id,source,context.Response.StatusCode>=500?"error":"warning",code,operation,DateTimeOffset.UtcNow,error?.GetType().FullName,frames){IsFailure=true,StatusCode=context.Response.StatusCode,PluginVersion=context.RequestServices.GetKeyedService<Func<string>>(ServiceKey+".Version."+source)?.Invoke()};
        try {context.RequestServices.GetRequiredKeyedService<Func<string,string>>(ServiceKey)(JsonSerializer.Serialize(row,Json));}
        catch {context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("SuiteDiagnostics").LogWarning("Diagnostic {ErrorId} could not be stored. Restore the original shared diagnostics storage; no application action was retried.",id);}
        return id;
    }
    internal static string Record(IServiceProvider services,string source,string code,string operation)
    {
        var id=Guid.NewGuid().ToString("D");
        try {services.GetRequiredKeyedService<Func<string,string>>(ServiceKey)(JsonSerializer.Serialize(new DiagnosticRecord(id,source,"warning",DiagnosticStore.Code(code),operation,DateTimeOffset.UtcNow){IsFailure=true,PluginVersion=services.GetKeyedService<Func<string>>(ServiceKey+".Version."+source)?.Invoke()},Json));}
        catch {services.GetService<ILoggerFactory>()?.CreateLogger("SuiteDiagnostics").LogWarning("Diagnostic {ErrorId} could not be stored. No operation was replayed.",id);}
        return id;
    }
    internal static string WithErrorId(IServiceProvider services,string source,string operation,string reply)
    {
        try {
            if(JsonNode.Parse(reply) is not JsonObject body)return reply;
            var failed=body["ok"] is JsonValue ok&&ok.TryGetValue<bool>(out var success)&&!success;
            var status=body["statusCode"] is JsonValue number&&number.TryGetValue<int>(out var value)?value:0;
            if(!failed&&status<400)return reply;
            var failure=body["error"] as JsonObject;var code=failure?["code"] is JsonValue text&&text.TryGetValue<string>(out var known)?DiagnosticStore.Code(known):status>=500?"provider_unavailable":"provider_rejected";
            var id=Record(services,source,code,operation);
            var safe=new JsonObject{["errorId"]=id};
            if(failed)safe["ok"]=false;
            if(status>=400)safe["statusCode"]=status;
            if(body["apiVersion"] is JsonValue version&&version.TryGetValue<string>(out var apiVersion)&&apiVersion=="1")safe["apiVersion"]="1";
            if(failure is not null){
                var mapped=new JsonObject{["code"]=code,["errorId"]=id,["nature"]=AccountHub.Api.UserErrors.Nature(code,status),["message"]=AccountHub.Api.UserErrors.Message(code,status)};
                if(failure["retryable"] is JsonValue retry&&retry.TryGetValue<bool>(out var retryable))mapped["retryable"]=retryable;
                safe["error"]=mapped;
            }else safe["error"]=AccountHub.Api.UserErrors.Message(code,status)+" Error ID: "+id;
            return safe.ToJsonString(Json);
        }catch(JsonException){return reply;}
    }
    private sealed class DiagnosticsStartup : IStartupFilter
    {
        public DiagnosticsStartup() { }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>{
            app.Use(async(context,following)=>{
                context.Response.OnStarting(()=>{if(context.Response.StatusCode>=400&&Source(context) is {} source){var id=Record(context,source,"http_"+context.Response.StatusCode);context.Response.Headers["X-Suite-Error-Id"]=id;}return Task.CompletedTask;});
                await following();
            });next(app);
        };
    }
    private sealed class DiagnosticsLoggerProvider(IServiceProvider services):ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)=>new DiagnosticLogger(services,categoryName);
        public void Dispose() { }
    }
    private sealed class DiagnosticLogger(IServiceProvider services,string category):ILogger
    {
        private readonly string? source=category.StartsWith("AccountHub.Notifications",StringComparison.Ordinal)?"notifications":category.StartsWith("AccountHub",StringComparison.Ordinal)?"account-hub":Plugins.FirstOrDefault(plugin=>category.StartsWith("Jellyfin.Plugin."+plugin.Value.Prefix,StringComparison.Ordinal)).Key;
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>source is not null&&level is >=LogLevel.Debug and <LogLevel.None;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? error,Func<TState,Exception?,string> formatter){
            if(!IsEnabled(level))return;
            // Never call the formatter or serialize state/scope arguments. Those
            // may contain tokens, passwords, request payloads or private names.
            var name=level switch{LogLevel.Debug=>"debug",LogLevel.Information=>"info",LogLevel.Warning=>"warning",LogLevel.Critical=>"critical",_=>"error"};
            try{var operation=services.GetKeyedService<Func<string,bool>>(ServiceKey+".Category."+source)?.Invoke(category)==true?category:"Plugin log event";var frames=error is null?null:string.Join('\n',new System.Diagnostics.StackTrace(error,false).GetFrames().Take(16).Select(frame=>frame.GetMethod()).Where(method=>method is not null).Select(method=>method!.DeclaringType?.FullName+"."+method.Name));var row=new DiagnosticRecord(Guid.NewGuid().ToString("D"),source!,name,"log_"+eventId.Id,operation,DateTimeOffset.UtcNow,error?.GetType().FullName,frames){PluginVersion=services.GetKeyedService<Func<string>>(ServiceKey+".Version."+source)?.Invoke()};services.GetRequiredKeyedService<Func<string,string>>(ServiceKey)(JsonSerializer.Serialize(row,Json));}catch{ /* Never recurse through logging or retry an application effect. */ }
        }
    }
    internal static async Task Endpoint(HttpContext context,string path)
    {
        context.Response.Headers.CacheControl="no-store";context.Response.Headers["X-Content-Type-Options"]="nosniff";
        try {
            var authentication=await context.AuthenticateAsync();if(authentication.Succeeded&&authentication.Principal is not null)context.User=authentication.Principal;
            await NativeAuthorization.RequireAsync(context,context.RequestServices,administrator:true,elevated:true);
        }catch {context.Response.StatusCode=403;await context.Response.WriteAsJsonAsync(new{ok=false,error=new{code="forbidden",message="Administrator access is required."}},Json);return;}
        var store=context.RequestServices.GetRequiredService<DiagnosticStore>();
        try {
            object result;
            if(context.Request.Method=="GET"&&path=="/errors"){
                var id=context.Request.Query["id"].ToString();var source=context.Request.Query["source"].ToString();var level=context.Request.Query["level"].ToString();
                if(id.Length>0&&!Guid.TryParseExact(id,"D",out _)||source.Length>0&&!Plugins.ContainsKey(source)||level.Length>0&&!DiagnosticStore.Levels.Contains(level))throw new ArgumentException();
                var skip=int.TryParse(context.Request.Query["skip"],out var offset)?Math.Clamp(offset,0,10000):0;
                result=new{items=store.Search(id,source,level,skip),settings=store.Settings(),plugins=Plugins.Where(plugin=>context.RequestServices.GetKeyedService<Func<string>>(ServiceKey+".Plugin."+plugin.Key) is not null).Select(plugin=>new{source=plugin.Key,page=plugin.Value.Page})};
            }else if(context.Request.Method=="POST"&&(path=="/clear"||path=="/settings")){
                if(context.Request.ContentLength is >16384)throw new ArgumentException();
                using var reader=new StreamReader(context.Request.Body);var buffer=new char[16385];var count=await reader.ReadBlockAsync(buffer.AsMemory(),context.RequestAborted);if(count>16384)throw new ArgumentException();
                using var body=JsonDocument.Parse(new string(buffer,0,count));var value=body.RootElement;
                if(path=="/clear"){
                    if(value.GetProperty("confirmation").GetString()!="CLEAR DIAGNOSTICS")throw new ArgumentException();
                    var ids=value.GetProperty("ids").EnumerateArray().Select(item=>item.GetGuid()).ToArray();if(ids.Length is <1 or >100||ids.Contains(Guid.Empty))throw new ArgumentException();
                    store.Clear(ids);result=new{status="cleared"};
                }else{
                    var level=value.GetProperty("minimumLevel").GetString()!;if(!DiagnosticStore.Levels.Contains(level))throw new ArgumentException();
                    if(!store.Configure(value.GetProperty("revision").GetInt64(),level)){context.Response.StatusCode=409;await context.Response.WriteAsJsonAsync(new{ok=false,error=new{code="revision_changed",message="Reload diagnostics settings."}},Json);return;}
                    result=new{status="updated"};
                }
            }else{context.Response.StatusCode=404;return;}
            await context.Response.WriteAsJsonAsync(new{ok=true,data=result},Json);
        }catch(Exception error)when(error is ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException){context.Response.StatusCode=400;await context.Response.WriteAsJsonAsync(new{ok=false,error=new{code="invalid_request",message="Check the diagnostics request."}},Json);}
        catch{context.Response.StatusCode=503;await context.Response.WriteAsJsonAsync(new{ok=false,error=new{code="diagnostics_unavailable",message="Diagnostics storage is unavailable. Restore the original shared database; it was not replaced."}},Json);}
    }
    private sealed class DiagnosticsResultFilter : IAsyncAlwaysRunResultFilter,IOrderedFilter
    {
        public DiagnosticsResultFilter() { }
        // Domain envelope filters must classify their native result before we
        // redact it. Running first would turn an ObjectResult into JsonResult
        // before those filters inspect it, changing a deliberate denial to 500.
        public int Order=>int.MaxValue;
        public async Task OnResultExecutionAsync(ResultExecutingContext context,ResultExecutionDelegate next)
        {
            // Task-based endpoint adapters may have already written their safe
            // response. Never change status/headers or append a second body.
            if(context.HttpContext.Response.HasStarted){await next();return;}
            var source=Source(context.HttpContext);var status=(context.Result as IStatusCodeActionResult)?.StatusCode??context.HttpContext.Response.StatusCode;
            if(source is not null&&status>=400){
                context.HttpContext.Response.StatusCode=status;
                var value=context.Result switch{JsonResult json=>json.Value,ObjectResult result=>result.Value,_=>null};
                JsonObject? body=null;try{
                    body=context.Result is ContentResult{Content:{Length:<=262144} content,ContentType:{} contentType}&&contentType.StartsWith("application/json",StringComparison.OrdinalIgnoreCase)
                        ?JsonNode.Parse(content) as JsonObject:JsonSerializer.SerializeToNode(value,Json) as JsonObject;
                }catch{}
                static string? Text(JsonNode? node)=>node is JsonValue text&&text.TryGetValue<string>(out var value)?value:null;
                var code=body?["error"] is JsonObject failure?Text(failure["code"]):Text(body?["code"])??Text((body?["data"] as JsonObject)?["code"]);
                var providerId=Text((body?["error"] as JsonObject)?["errorId"])??Text(body?["errorId"]);
                if(Guid.TryParseExact(providerId,"D",out _))try{
                    if(context.HttpContext.RequestServices.GetService<DiagnosticStore>()?.Search(providerId!,"","",0) is {Length:1})context.HttpContext.Items[ServiceKey]=providerId;
                }catch{ /* A missing diagnostic store must not change the application outcome. */ }
                var id=Record(context.HttpContext,source,DiagnosticStore.Code(code));
                var message=AccountHub.Api.UserErrors.Message(code,status);var nature=AccountHub.Api.UserErrors.Nature(code,status);
                var safe=new JsonObject{["ok"]=false,["errorId"]=id};
                foreach(var key in new[]{"apiVersion","code","retryable","status","statusCode","twoFactorRequired"})if(body?[key] is {} field)safe[key]=field.DeepClone();
                JsonObject Failure(JsonObject? original){var error=new JsonObject{["code"]=Text(original?["code"])??DiagnosticStore.Code(code),["nature"]=nature,["message"]=message,["errorId"]=id};if(original?["retryable"] is JsonValue retry&&retry.TryGetValue<bool>(out var allowed))error["retryable"]=allowed;return error;}
                safe["error"]=body?["error"] is JsonValue?JsonValue.Create(message):Failure(body?["error"] as JsonObject);
                if(body?["data"] is JsonObject data&&data.ContainsKey("code"))safe["data"]=Failure(data);
                else if(body?["data"] is JsonObject health&&health["ready"] is JsonValue readiness&&readiness.TryGetValue<bool>(out var ready)&&!ready){
                    // Keep the existing machine-readable unavailable-health
                    // contract without exposing storage paths or error prose.
                    safe["data"]=new JsonObject{["ready"]=false,["errorCode"]=DiagnosticStore.Code(Text(health["errorCode"]))};
                }
                if(body?.ContainsKey("message")==true||body?.ContainsKey("detail")==true)safe["message"]=message;
                // Preserve native second-factor signals, error codes, status and
                // durable-command projections. Never turn denial into success.
                context.Result=new JsonResult(safe,Json){StatusCode=status};
            }
            await next();
        }
    }
    private sealed class DiagnosticsExceptionFilter : IAsyncExceptionFilter
    {
        public DiagnosticsExceptionFilter() { }
        public Task OnExceptionAsync(ExceptionContext context){
            if(Source(context.HttpContext) is not {} source||context.HttpContext.Response.HasStarted||context.Exception is OperationCanceledException&&context.HttpContext.RequestAborted.IsCancellationRequested)return Task.CompletedTask;
            context.HttpContext.Response.StatusCode=500;var id=Record(context.HttpContext,source,"unhandled_exception",context.Exception);
            context.Result=new JsonResult(new{ok=false,error=new{code="outcome_uncertain",nature="uncertain",message=AccountHub.Api.UserErrors.Message("outcome_uncertain"),errorId=id,retryable=false}},Json){StatusCode=500};context.ExceptionHandled=true;return Task.CompletedTask;
        }
    }
}

[ApiController,Authorize(Policy=Policies.RequiresElevation),Route("SuiteDiagnostics/api/v1"),RequestSizeLimit(16384)]
public sealed class SuiteDiagnosticsController:ControllerBase
{
    [HttpGet("errors")]public Task Errors()=>SuiteDiagnostics.Endpoint(HttpContext,"/errors");
    [HttpPost("clear")]public Task Clear()=>SuiteDiagnostics.Endpoint(HttpContext,"/clear");
    [HttpPost("settings")]public Task Settings()=>SuiteDiagnostics.Endpoint(HttpContext,"/settings");
}
