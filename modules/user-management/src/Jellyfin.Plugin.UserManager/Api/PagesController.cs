using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserManager.Api;

[AllowAnonymous]
[Route("UserManager")]
public sealed class PagesController : ControllerBase
{
    private const string Root = "Jellyfin.Plugin.UserManager.Web.";

    [HttpGet("")]
    public IActionResult Index() => Redirect("/UserManager/account");

    [HttpGet("account")]
    public IActionResult Account() => Html("account.html");

    [HttpGet("reset")]
    public IActionResult Reset() => Html("reset.html");

    [HttpGet("invite/{token}")]
    [HttpGet("invite")]
    public IActionResult Invite(string? token = null) => Html("invite.html");

    [HttpGet("invite-request")]
    public IActionResult InviteRequest() => Html("invite-request.html");

    [HttpGet("client.js")]
    public IActionResult ClientScript() => Resource("client.js", "application/javascript; charset=utf-8");

    [HttpGet("common.js")]
    public IActionResult CommonScript() => Resource("common.js", "application/javascript; charset=utf-8");

    [HttpGet("styles.css")]
    public IActionResult Styles() => Resource("styles.css", "text/css; charset=utf-8");

    [HttpGet("{asset:regex(^(account|admin|invite|reset|invite-request)\\.js$)}")]
    public IActionResult Script(string asset) => Resource(asset, "application/javascript; charset=utf-8");

    private IActionResult Html(string name)
    {
        var result = Resource(name, "text/html; charset=utf-8");
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        Response.Headers.CacheControl = "no-store, max-age=0";
        return result;
    }

    private IActionResult Resource(string name, string contentType)
    {
        var stream = typeof(PagesController).Assembly.GetManifestResourceStream(Root + name);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, contentType);
    }
}
