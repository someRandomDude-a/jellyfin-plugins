using System.Text.Json;
using AccountHub.Core;
using AccountHub.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController,Authorize,Route("AccountHub/api/v1/invite-defaults"),TypeFilter(typeof(HubBodyFilter)),RequestSizeLimit(16384)]
public sealed class InviteDefaultsController(InviteDefaultsService defaults,HubHost host,AccountHub.Groups.GroupPolicyService groups):ControllerBase
{
    [HttpGet]public Task<IActionResult> Get(CancellationToken ct)=>Run(async()=>{await host.RequireAdministratorAsync(HttpContext,ct);return await defaults.ReadAsync(ct);});
    [HttpPost]public Task<IActionResult> Save(JsonElement body,CancellationToken ct)=>Run(async()=>{
        var caller=await host.RequireAdministratorAsync(HttpContext,ct);var value=body.GetProperty("settings").Deserialize<InviteDefaults>(SecurityJson.Options)??throw new HubException("invalid_request","Missing settings");
        if(value.DefaultGroupId is {} id)_=await groups.GroupAsync(id,ct);
        return await defaults.SaveAsync(caller.UserId,body.GetProperty("commandId").GetGuid(),body.GetProperty("revision").GetInt64(),value,token=>host.AuthorizeAsync(HttpContext,CommandRisk.Configuration,token),ct);
    });
    private async Task<IActionResult> Run<T>(Func<Task<T>> action){
        Response.Headers.CacheControl="no-store";
        try{return new JsonResult(new{apiVersion="1",ok=true,data=await action()},Contracts.Wire.Options);}
        catch(SecurityFailure e){return Failure(e.Code,e.Status);}
        catch(HubException e){return Failure(e.Code,e.Code=="revision_changed"?409:e.Retryable?503:400);}
        catch(Exception e)when(e is JsonException or FormatException or KeyNotFoundException or ArgumentException){return Failure("invalid_request",400);}
    }
    private IActionResult Failure(string code,int status)=>StatusCode(status,new{apiVersion="1",ok=false,error=new{code}});
}
