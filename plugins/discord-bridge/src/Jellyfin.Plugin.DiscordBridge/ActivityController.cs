using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.PrivateSyncPlay.Api;

namespace Jellyfin.Plugin.DiscordBridge;

[ApiController, AllowAnonymous, Route("DiscordBridge")]
public sealed class ActivityController(BridgeStore store, BridgeRuntime runtime, Jellyfin.Plugin.PrivateSyncPlay.NativeWebStyles styles, IWatchPartyService parties) : ControllerBase
{
    private void Headers()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    [HttpGet("activity")]
    public IActionResult Index()
    {
        Headers();
        if (Request.Path.Value?.EndsWith('/') != true) return LocalRedirect(Request.PathBase + "/DiscordBridge/activity/");
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self' data:; img-src 'self' data:; frame-ancestors https://discord.com https://*.discord.com https://*.discordsays.com; base-uri 'none'; form-action 'none'";
        using var input = typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.DiscordBridge.Web.activity.html")!;
        using var reader = new StreamReader(input);
        return Content(reader.ReadToEnd().Replace("<!--JELLYFIN_THEME-->", styles.ThemeLink.Replace("../web/", "../../web/")), "text/html; charset=utf-8");
    }
    [HttpGet("activity/config")]
    public IActionResult Config()
    {
        Headers();
        var config = store.Read().Settings;
        var valid = Uri.TryCreate(config.PublicServerUrl, UriKind.Absolute, out var url) && url.Scheme == "https" && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0;
        var ready = config.Enabled && config.EnableWatchPartyCommands && valid && runtime.ApplicationId is not null && parties.IsAvailable;
        return new JsonResult(new { available = ready, applicationId = ready ? runtime.ApplicationId : null, publicServerUrl = ready ? config.PublicServerUrl : null });
    }
    [HttpGet("activity/assets/{name}")]
    public IActionResult Asset(string name)
    {
        Headers();
        if (name == "activity-native.css") return Content(styles.Css.Replace("../../web/", "../../../web/"), "text/css; charset=utf-8");
        var (resource, type) = name switch
        {
            "activity.js" => ("activity.bundle.js", "text/javascript; charset=utf-8"),
            "activity.css" => ("activity.css", "text/css; charset=utf-8"),
            "embedded-app-sdk.LICENSE" => ("embedded-app-sdk.LICENSE", "text/plain; charset=utf-8"),
            _ => (null, null)
        };
        var stream = resource is null ? null : typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.DiscordBridge.Web." + resource);
        return stream is null ? NotFound() : File(stream, type!);
    }
}
