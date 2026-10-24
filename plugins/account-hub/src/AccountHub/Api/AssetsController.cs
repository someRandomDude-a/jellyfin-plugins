using System.Net;
using AccountHub.Core;
using Jellyfin.Plugin.PrivateSyncPlay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub.Api;

[ApiController, AllowAnonymous, Route("AccountHub")]
public sealed class AssetsController(NativeWebStyles styles) : ControllerBase
{
    [HttpGet("assets/{file}")]
    public IActionResult Asset(string file)
    {
        if (file == "native.css") return Content(styles.Css, "text/css");
        if (file is not ("workspace.js" or "workspace.css" or "domain-panel.js" or "seerr-panel.mjs" or "security-client.mjs" or "security-panel.mjs" or "public.js")) return NotFound();
        var stream = typeof(AssetsController).Assembly.GetManifestResourceStream("AccountHub.Web." + file);
        return stream is null ? NotFound() : File(stream, file.EndsWith(".css", StringComparison.Ordinal) ? "text/css" : "text/javascript");
    }
    [HttpGet("reset"), HttpGet("discord/link")]
    public IActionResult PublicPage()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
        var basePath = WebUtility.HtmlEncode(Request.PathBase + "/AccountHub/");
        var kind = Request.Path.Value?.EndsWith("/reset", StringComparison.OrdinalIgnoreCase) == true ? "reset" : "link";
        var theme = WebUtility.HtmlEncode(styles.Theme);
        return Content($"<!doctype html><html lang=\"en\" class=\"theme-{theme}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><base href=\"{basePath}\"><title>Account Hub</title><link rel=\"stylesheet\" href=\"assets/native.css\">{styles.ThemeLink}<link rel=\"stylesheet\" href=\"../Branding/Css\"><link rel=\"stylesheet\" href=\"assets/workspace.css\"></head><body><main class=\"accountHubPublic content-primary\" data-page=\"{kind}\"><h1>Account Hub</h1><p role=\"status\" aria-live=\"polite\" class=\"hubPublicStatus\">Loading…</p><div class=\"hubPublicContent\"></div></main><script src=\"assets/public.js\" defer></script></body></html>", "text/html; charset=utf-8");
    }
}
