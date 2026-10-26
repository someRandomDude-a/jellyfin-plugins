using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed record MetadataRevision(long Revision);
[ApiController,Authorize,RequestSizeLimit(16384),TypeFilter(typeof(RuleApiFilter)),Route("AutomaticDeletionRequests/admin/metadata/{source}")]
public sealed class MetadataConnectionsController(ArrConnections connections,IUserManager users):ControllerBase
{
    private void Require(bool write=false){NativeAccess.Require(users,User);Response.Headers.CacheControl="no-store";
        if(write&&Request.Headers.TryGetValue("Origin",out var origin)&&(!Uri.TryCreate(origin,UriKind.Absolute,out var uri)||uri.Authority!=Request.Host.Value||uri.Scheme!=Request.Scheme))throw new UnauthorizedAccessException();}
    [HttpGet]public IActionResult Get(string source){Require();return Ok(connections.Status(source));}
    [HttpPut]public IActionResult Save(string source,ArrConnectionUpdate body){Require(true);connections.Save(source,body);return Ok(connections.Status(source));}
    [HttpPost("test")]public async Task<IActionResult> Test(string source,CancellationToken ct){Require(true);await connections.TestAsync(source,ct);return Ok(new{working=true});}
    [HttpPost("import-enhanced")]public async Task<IActionResult> Import(string source,MetadataRevision body,CancellationToken ct){Require(true);await connections.ImportAsync(source,body.Revision,ct);return Ok(connections.Status(source));}
}
