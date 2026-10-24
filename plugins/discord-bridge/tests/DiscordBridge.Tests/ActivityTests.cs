using System.Text.Json;
using Jellyfin.Plugin.DiscordBridge;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DiscordBridge.Tests;

public sealed class ActivityTests
{
    [Fact]
    public void BundledActivityAndOriginalDependencyLicensesAreEmbedded()
    {
        var assembly = typeof(Plugin).Assembly;
        using var script = assembly.GetManifestResourceStream("Jellyfin.Plugin.DiscordBridge.Web.activity.bundle.js");
        Assert.NotNull(script);
        using var reader = new StreamReader(script!);
        var source = reader.ReadToEnd(); Assert.Contains("disableConsoleLogOverride", source);
        using var license = assembly.GetManifestResourceStream("Jellyfin.Plugin.DiscordBridge.Web.embedded-app-sdk.LICENSE");
        Assert.NotNull(license);
        using var notices = new StreamReader(license!);
        var text = notices.ReadToEnd(); Assert.Contains("@discord/embedded-app-sdk 2.5.0", text); Assert.Contains("MIT", text);
        Assert.DoesNotContain(assembly.GetManifestResourceNames(), name => name.EndsWith(".map", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledPublicConfigDisclosesNoTransportOrUserConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiscordBridge.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BridgeStore(directory);
            using var services = new ServiceCollection().BuildServiceProvider();
            var runtime = new BridgeRuntime(services, NullLogger<BridgeRuntime>.Instance);
            var controller = new ActivityController(store, runtime, null!, null!) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            var result = Assert.IsType<JsonResult>(controller.Config());
            var json = JsonSerializer.SerializeToElement(result.Value);
            Assert.Equal(3, json.EnumerateObject().Count());
            Assert.False(json.GetProperty("available").GetBoolean());
            Assert.Equal(JsonValueKind.Null, json.GetProperty("applicationId").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.GetProperty("publicServerUrl").ValueKind);
            Assert.Equal("no-store", controller.Response.Headers.CacheControl);
            Assert.IsType<NotFoundResult>(controller.Asset("settings.html"));
            Assert.IsType<NotFoundResult>(controller.Asset("../Plugin.cs"));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
