using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.DeletionQueue;
using Jellyfin.Plugin.DeletionQueue.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DeletionQueue.Tests;

public sealed class DiagnosticsEnvelopeTests
{
    [Fact] public async Task NativeDomainDenialIsClassifiedBeforeRedactionAndRemains403WithItsMachineCode()
    {
        using var services=new ServiceCollection().BuildServiceProvider();var http=new DefaultHttpContext{RequestServices=services};http.Request.Path="/DeletionQueue/admin/account-hub/v1/users/target/policy";
        var action=new ActionContext(http,new RouteData(),new ActionDescriptor());var filters=new List<IFilterMetadata>();
        var context=new ResultExecutingContext(action,filters,new ObjectResult(new{code="AdminTargetReadOnly"}){StatusCode=403},new object());
        var envelope=new HubEnvelopeFilter(null!);
        var type=typeof(Plugin).Assembly.GetType("Jellyfin.PluginSupport.SuiteDiagnostics+DiagnosticsResultFilter",true)!;
        var redaction=(IAsyncAlwaysRunResultFilter)Activator.CreateInstance(type,true)!;
        Assert.Equal(int.MaxValue,Assert.IsAssignableFrom<IOrderedFilter>(redaction).Order);
        envelope.OnResultExecuting(context);
        Assert.Equal(403,Assert.IsType<ContentResult>(context.Result).StatusCode);
        await redaction.OnResultExecutionAsync(context,()=>Task.FromResult(new ResultExecutedContext(action,filters,context.Result,new object())));
        var result=Assert.IsType<JsonResult>(context.Result);Assert.Equal(403,result.StatusCode);
        var body=JsonSerializer.SerializeToElement(result.Value);Assert.Equal("admin_target_read_only",body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("authorization",body.GetProperty("error").GetProperty("nature").GetString());Assert.DoesNotContain("review authorization, inputs",body.ToString());
    }
}
