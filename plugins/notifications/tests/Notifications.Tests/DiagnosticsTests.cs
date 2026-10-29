using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Notifications;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Configuration;
using Moq;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Xunit;

namespace Notifications.Tests;

public sealed class DiagnosticsTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"opencode","suite-diagnostics-tests-"+Guid.NewGuid().ToString("N"));
    private static readonly Assembly Assembly=typeof(Plugin).Assembly;
    private static readonly Type StoreType=typeof(Jellyfin.Diagnostics.DiagnosticStore);
    private static readonly Type RecordType=typeof(Jellyfin.Diagnostics.DiagnosticRecord);
    private object Store()=>Activator.CreateInstance(StoreType,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[root],null)!;
    private static object? Call(object store,string name,params object?[] args)=>StoreType.GetMethod(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(store,args);
    private static JsonElement Value(object? value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static object Record(string id,string source="notifications",string level="warning")=>JsonSerializer.Deserialize(JsonSerializer.Serialize(new{id,source,level,code="native_denied",operation="TestController.Action",createdAt=DateTimeOffset.UtcNow,password="must-not-be-recorded",token="must-not-be-recorded"}),RecordType,new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    [Fact] public void IndependentPluginWritersShareTheSameDatabaseAndExactErrorIds()
    {
        var first=Store();var second=Store();var id=Guid.NewGuid().ToString("D");Call(first,"Record",Record(id,"discord-bridge"));
        var found=Value(Call(second,"Search",id,"","",0));Assert.Equal(id,found[0].GetProperty("id").GetString());Assert.Equal("discord-bridge",found[0].GetProperty("source").GetString());
        Assert.DoesNotContain("must-not-be-recorded",found.ToString());Assert.Empty(Value(Call(second,"Search",Guid.NewGuid().ToString("D"),"","",0)).EnumerateArray());
    }
    [Fact] public void RecordingLevelIsRevisionedAndClearOnlyRemovesExplicitlySelectedDiagnostics()
    {
        var store=Store();Assert.True((bool)Call(store,"Configure",1L,"error")!);Assert.False((bool)Call(store,"Configure",1L,"debug")!);
        var warning=Guid.NewGuid().ToString("D");var error=Guid.NewGuid().ToString("D");Call(store,"Record",Record(warning));Call(store,"Record",Record(error,level:"error"));
        Assert.Single(Value(Call(store,"Search","","","",0)).EnumerateArray());
        var unrelated=Path.Combine(root,"unrelated-state");File.WriteAllText(unrelated,"preserve");Call(store,"Clear",new Guid[]{Guid.Parse(error)});
        Assert.Empty(Value(Call(store,"Search","","","",0)).EnumerateArray());Assert.Equal("preserve",File.ReadAllText(unrelated));
    }
    [Fact] public void MissingOrUnknownDiagnosticStorageIsNeverSilentlyReplaced()
    {
        var store=Store();Call(store,"Settings");var file=Path.Combine(root,"diagnostics.db");File.Move(file,file+".preserved");
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(()=>Call(store,"Settings")).InnerException);Assert.False(File.Exists(file));
        File.Move(file+".preserved",file);using(var db=new SqliteConnection("Data Source="+file)){db.Open();using var command=db.CreateCommand();command.CommandText="PRAGMA user_version=99";command.ExecuteNonQuery();}
        Assert.IsType<InvalidOperationException>(Assert.Throws<TargetInvocationException>(()=>Call(store,"Settings")).InnerException);
    }
    [Fact] public void FrameworkDelegateOwnerRegistersOnceWithoutRequiringHubOrNotifications()
    {
        var services=new ServiceCollection();var api=Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics",true)!;var register=api.GetMethod("Register",BindingFlags.Static|BindingFlags.NonPublic)!;
        register.Invoke(null,[services,"discord-bridge"]);register.Invoke(null,[services,"notifications"]);register.Invoke(null,[services,"deletion-queue"]);
        Assert.Single(services,value=>value.ServiceType==typeof(IStartupFilter)&&value.ImplementationType?.DeclaringType==api);
        Assert.Single(services,value=>value.ServiceType==typeof(Func<string,string>)&&Equals(value.ServiceKey,"Suite.Diagnostics.1"));
        Assert.Equal(3,services.Count(value=>value.ServiceType==typeof(Func<string>)&&value.ServiceKey is string key&&key.StartsWith("Suite.Diagnostics.1.Plugin.",StringComparison.Ordinal)));
        var options=new MvcOptions();
        foreach(var descriptor in services)if(descriptor.ImplementationInstance is IConfigureOptions<MvcOptions> configure)configure.Configure(options);
        var redaction=Assert.Single(options.Filters.OfType<TypeFilterAttribute>(),value=>value.ImplementationType.Name=="DiagnosticsResultFilter");Assert.Equal(int.MaxValue,redaction.Order);
    }
    [Fact] public void DiagnosticLoggingHonorsLevelsWithoutFormattingOrPersistingSecretStateOrExceptionMessages()
    {
        var services=new ServiceCollection();var paths=new Mock<IApplicationPaths>();paths.SetupGet(value=>value.PluginConfigurationsPath).Returns(root);services.AddSingleton(paths.Object);
        Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics",true)!.GetMethod("Register",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[services,"notifications"]);
        using var provider=services.BuildServiceProvider();var logger=Assert.Single(provider.GetServices<ILoggerProvider>()).CreateLogger("Jellyfin.Plugin.DiscordBridge.Runtime");
        var formatted=false;logger.Log(LogLevel.Information,new EventId(42),"private-token-value",new InvalidOperationException("private-password-value"),(_,_)=>{formatted=true;return "private-link-value";});
        var store=new Jellyfin.Diagnostics.DiagnosticStore(Path.Combine(root,"SuiteDiagnostics"));var first=Assert.Single(store.Search("","","",0));
        Assert.Equal("discord-bridge",first.Source);Assert.Equal("info",first.Level);Assert.False(formatted);Assert.DoesNotContain("private-",JsonSerializer.Serialize(first));
        logger.LogDebug("Private {secret}","private-input");Assert.Single(store.Search("","","",0));
        Assert.True(store.Configure(1,"debug"));logger.LogDebug("Private {secret}","private-input");Assert.Equal(2,store.Search("","","",0).Length);
    }
    [Fact] public void UserFacingFailuresRemainSearchableEvenWhenBackgroundLoggingIsRestricted()
    {
        var store=new Jellyfin.Diagnostics.DiagnosticStore(root);Assert.True(store.Configure(1,"critical"));
        var id=Guid.NewGuid().ToString("D");store.Record(new(id,"discord-bridge","warning","link_required","Discord link command",DateTimeOffset.UtcNow){IsFailure=true});
        Assert.Equal(id,Assert.Single(store.Search(id,"","",0)).Id);
    }
    [Fact] public void DiagnosticDetailLevelWorksThroughNativeLoggerFactoryWithoutLoweringOtherProvidersThresholds()
    {
        var services=new ServiceCollection();var paths=new Mock<IApplicationPaths>();paths.SetupGet(value=>value.PluginConfigurationsPath).Returns(root);services.AddSingleton(paths.Object);
        services.AddLogging(builder=>builder.SetMinimumLevel(LogLevel.Critical));
        Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics",true)!.GetMethod("Register",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[services,"notifications"]);
        using var provider=services.BuildServiceProvider();var store=new Jellyfin.Diagnostics.DiagnosticStore(Path.Combine(root,"SuiteDiagnostics"));Assert.True(store.Configure(1,"debug"));
        var logger=provider.GetRequiredService<ILoggerFactory>().CreateLogger("Jellyfin.Plugin.Notifications.Test");var formatted=false;
        logger.Log(LogLevel.Debug,new EventId(73),"private-state",new InvalidOperationException("private-exception"),(_,_)=>{formatted=true;return "private-output";});
        var record=Assert.Single(store.Search("","","",0));Assert.Equal("debug",record.Level);Assert.False(formatted);Assert.DoesNotContain("private-",JsonSerializer.Serialize(record));
        Assert.Equal(LogLevel.Critical,provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.MinLevel);
    }
    [Fact] public async Task ConcurrentPluginWritersInitializeAndAppendWithoutResettingSharedHistory()
    {
        await Task.WhenAll(Enumerable.Range(0,12).Select(index=>Task.Run(()=>new Jellyfin.Diagnostics.DiagnosticStore(root).Record(new(Guid.NewGuid().ToString("D"),index%2==0?"notifications":"discord-bridge","error","test_failure","Owned test",DateTimeOffset.UtcNow)))));
        Assert.Equal(12,new Jellyfin.Diagnostics.DiagnosticStore(root).Search("","","",0).Length);
    }
    [Fact] public async Task ErrorResponsesExposeOpaqueIdsAndSafeCodesButNeverProviderDetailsOrModelStateValues()
    {
        string? captured=null;using var services=new ServiceCollection().AddKeyedSingleton<Func<string,string>>("Suite.Diagnostics.1",(_,_)=>payload=>{captured=payload;return "stored";}).BuildServiceProvider();
        var http=new DefaultHttpContext{RequestServices=services};http.Request.Path="/Notifications/api/v1/settings/notifications";http.Response.StatusCode=400;
        var action=new ActionContext(http,new RouteData(),new ActionDescriptor());var filters=new List<IFilterMetadata>();var original=new JsonResult(new{ok=false,error=new{code="invalid_request",message="private-password",details="private-token"},errors=new{password="private-model-value"},traceId="private-trace",twoFactorRequired=true}){StatusCode=400};
        var context=new ResultExecutingContext(action,filters,original,new object());var type=Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics+DiagnosticsResultFilter",true)!;var filter=(IAsyncResultFilter)Activator.CreateInstance(type,true)!;
        await filter.OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(action,filters,context.Result,new object())));
        var result=Value(((JsonResult)context.Result).Value);Assert.False(result.GetProperty("ok").GetBoolean());Assert.True(result.GetProperty("twoFactorRequired").GetBoolean());Assert.Equal("invalid_request",result.GetProperty("error").GetProperty("code").GetString());
        Assert.True(Guid.TryParse(result.GetProperty("errorId").GetString(),out _));Assert.DoesNotContain("private-",result.ToString());Assert.DoesNotContain("private-",captured!);Assert.Equal(400,((JsonResult)context.Result).StatusCode);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentAndStatusOnlyFailuresUseTheSameAlwaysRunRedactionBoundary(bool content)
    {
        using var services=new ServiceCollection().BuildServiceProvider();var http=new DefaultHttpContext{RequestServices=services};http.Request.Path="/UserManager/api/account";
        var action=new ActionContext(http,new RouteData(),new ActionDescriptor());var filters=new List<IFilterMetadata>();
        IActionResult original=content?new ContentResult{Content="private-provider-detail",StatusCode=403}:new StatusCodeResult(403);
        var context=new ResultExecutingContext(action,filters,original,new object());var type=Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics+DiagnosticsResultFilter",true)!;
        var filter=Assert.IsAssignableFrom<IAsyncAlwaysRunResultFilter>(Activator.CreateInstance(type,true)!);
        await filter.OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(action,filters,context.Result,new object())));
        var result=Assert.IsType<JsonResult>(context.Result);Assert.Equal(403,result.StatusCode);var body=Value(result.Value);
        Assert.Equal("authorization",body.GetProperty("error").GetProperty("nature").GetString());Assert.DoesNotContain("private-",body.ToString());
    }
    [Fact] public void FrameworkProviderFailuresStripAllUnreviewedFieldsWhilePreservingFailureStatusAndSafeCode()
    {
        using var services=new ServiceCollection().BuildServiceProvider();var method=Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics",true)!.GetMethod("WithErrorId",BindingFlags.Static|BindingFlags.NonPublic)!;
        foreach(var reply in new[]{"{\"ok\":false,\"data\":{\"password\":\"private-payload\"},\"error\":{\"code\":\"forbidden\",\"message\":\"private-message\",\"exception\":\"private-exception\",\"retryable\":false}}","{\"statusCode\":403,\"error\":\"private-message\",\"stackTrace\":\"private-stack\"}"}){
            var safe=(string)method.Invoke(null,[services,"private-syncplay","Test operation",reply])!;
            Assert.DoesNotContain("private-",safe);Assert.True(Guid.TryParse(JsonDocument.Parse(safe).RootElement.GetProperty("errorId").GetString(),out _));
        }
    }
    [Fact] public async Task AlreadyWrittenAdapterFailuresAreNotRewrittenOrAppendedTo()
    {
        using var services=new ServiceCollection().BuildServiceProvider();var feature=new Mock<IHttpResponseFeature>(MockBehavior.Strict);feature.SetupGet(value=>value.HasStarted).Returns(true);
        var http=new DefaultHttpContext{RequestServices=services};http.Request.Path="/Notifications/api/v1/notifications";http.Features.Set(feature.Object);
        var action=new ActionContext(http,new RouteData(),new ActionDescriptor());var filters=new List<IFilterMetadata>();var original=new EmptyResult();var context=new ResultExecutingContext(action,filters,original,new object());
        var filter=(IAsyncAlwaysRunResultFilter)Activator.CreateInstance(Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics+DiagnosticsResultFilter",true)!,true)!;var continued=false;
        await filter.OnResultExecutionAsync(context,()=>{continued=true;return Task.FromResult(new ResultExecutedContext(action,filters,context.Result,new object()));});
        Assert.True(continued);Assert.Same(original,context.Result);feature.VerifyGet(value=>value.HasStarted,Times.Once);feature.VerifyNoOtherCalls();
    }
    [Fact] public async Task ForwardedDomainFailuresRetainTheVerifiedOwningPluginDiagnosticId()
    {
        var store=new Jellyfin.Diagnostics.DiagnosticStore(root);var id=Guid.NewGuid().ToString("D");store.Record(new(id,"deletion-queue","warning","forbidden","Owned domain command",DateTimeOffset.UtcNow){IsFailure=true});
        using var services=new ServiceCollection().AddSingleton(store).BuildServiceProvider();var http=new DefaultHttpContext{RequestServices=services};http.Request.Path="/AccountHub/api/v1/domains/deletion-queue/command";
        var action=new ActionContext(http,new RouteData(),new ActionDescriptor());var filters=new List<IFilterMetadata>();
        var original=new ContentResult{StatusCode=403,ContentType="application/json",Content=JsonSerializer.Serialize(new{ok=false,error=new{code="forbidden",errorId=id,message="private-provider-message"}})};
        var context=new ResultExecutingContext(action,filters,original,new object());var filter=(IAsyncAlwaysRunResultFilter)Activator.CreateInstance(Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics+DiagnosticsResultFilter",true)!,true)!;
        await filter.OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(action,filters,context.Result,new object())));
        var body=Value(Assert.IsType<JsonResult>(context.Result).Value);Assert.Equal(id,body.GetProperty("errorId").GetString());Assert.DoesNotContain("private-",body.ToString());Assert.Equal("deletion-queue",Assert.Single(store.Search(id,"","",0)).Source);
    }
    [Fact] public async Task UnavailableHealthRetainsItsMinimalMachineContractButNotTechnicalDetails()
    {
        using var services=new ServiceCollection().BuildServiceProvider();var http=new DefaultHttpContext{RequestServices=services};http.Request.Path="/AccountHub/api/v1/health";
        var action=new ActionContext(http,new RouteData(),new ActionDescriptor());var filters=new List<IFilterMetadata>();
        var original=new JsonResult(new{ok=false,data=new{ready=false,errorCode="storage_recovery_required",message="private-provider-message",path="private-storage-path"}}){StatusCode=503};
        var context=new ResultExecutingContext(action,filters,original,new object());var filter=(IAsyncAlwaysRunResultFilter)Activator.CreateInstance(Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics+DiagnosticsResultFilter",true)!,true)!;
        await filter.OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(action,filters,context.Result,new object())));
        var result=Assert.IsType<JsonResult>(context.Result);Assert.Equal(503,result.StatusCode);var body=Value(result.Value);
        Assert.False(body.GetProperty("data").GetProperty("ready").GetBoolean());Assert.Equal("storage_recovery_required",body.GetProperty("data").GetProperty("errorCode").GetString());Assert.DoesNotContain("private-",body.ToString());
    }
    public void Dispose(){SqliteConnection.ClearAllPools();if(Directory.Exists(root))Directory.Delete(root,true);}
}
