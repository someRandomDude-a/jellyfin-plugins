using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.UserManager.Services;
using Xunit;

namespace Jellyfin.Plugin.UserManager.Tests;

public sealed class SeerrClientContractTests
{
    [Fact]
    public async Task StatusUsesDocumentedBooleanAndSnapshotCopiesMutableSettings()
    {
        await using var server = new Api((200, "{\"version\":\"3.5.1\"}"));
        var settings = server.Settings;
        var client = new SeerrClient(null!, null!).Snapshot(settings);
        settings.SeerrUrl = "https://must-not-be-contacted.invalid";
        settings.SeerrApiKey = "changed";
        var status = await client.ReadDocumentAsync("/status?checkUpdateAvailable=false", default);
        Assert.Equal("3.5.1", status.GetProperty("version").GetString());
        await server.Completed;
        Assert.Equal("/api/v1/status?checkUpdateAvailable=false", Assert.Single(server.Requests).Path);
        Assert.Equal("test-key", server.Requests[0].Key);
    }

    [Fact]
    public async Task ImportConflictIsNotTreatedAsSuccessfulImport()
    {
        await using var server = new Api((404, "{}"), (409, "{\"message\":\"PRIVATE\"}"));
        var ex = await Assert.ThrowsAsync<SeerrException>(() => server.Client.EnsureUserAsync(Guid.NewGuid()));
        Assert.Equal(409, ex.StatusCode); Assert.DoesNotContain("PRIVATE", ex.Message);
        await server.Completed;
        Assert.Equal("POST", server.Requests[1].Method);
        Assert.Equal("/api/v1/user/import-from-jellyfin", server.Requests[1].Path);
        using var payload = JsonDocument.Parse(server.Requests[1].Body);
        Assert.Equal(32, payload.RootElement.GetProperty("jellyfinUserIds")[0].GetString()!.Length);
    }

    [Fact]
    public async Task DeleteRechecksIdentityOnFinalProtectedAccountRead()
    {
        var native = Guid.NewGuid();
        string User(Guid id) => JsonSerializer.Serialize(new { id = 8, jellyfinUserId = id.ToString("N"), permissions = 32, createdAt = "created" });
        await using var server = new Api((200, User(native)), (200, User(Guid.NewGuid())));
        var ex = await Assert.ThrowsAsync<SeerrException>(() => server.Client.DeleteVerifiedAsync(8, native, "created", default));
        Assert.Equal(409, ex.StatusCode);
        await server.Completed; Assert.All(server.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Fact]
    public async Task QuotaPostRetainsExplicitNullInheritanceValues()
    {
        await using var server = new Api((200, "{\"id\":8,\"permissions\":32}"), (200, "{}"));
        await server.Client.SetQuotaDocumentAsync(8, JsonSerializer.SerializeToElement(new { username = "native", movieQuotaLimit = (int?)null, movieQuotaDays = (int?)null, tvQuotaLimit = 10, tvQuotaDays = 7 }), default);
        await server.Completed;
        var request = server.Requests[1];
        Assert.Equal("POST", request.Method); Assert.Equal("/api/v1/user/8/settings/main", request.Path);
        using var payload = JsonDocument.Parse(request.Body);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("movieQuotaLimit").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("movieQuotaDays").ValueKind);
    }

    private sealed class Api : IAsyncDisposable
    {
        private readonly HttpListener listener = new();
        public PluginSettings Settings { get; }
        public SeerrClient Client => new SeerrClient(null!, null!).Snapshot(Settings);
        public List<(string Method, string Path, string Body, string? Key)> Requests { get; } = [];
        public Task Completed { get; }
        public Api(params (int Status, string Body)[] responses)
        {
            using var port = new TcpListener(IPAddress.Loopback, 0); port.Start();
            var url = $"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/"; port.Stop();
            Settings = new() { SeerrUrl = url, SeerrApiKey = "test-key", SeerrAllowHttp = true, SeerrAllowPrivateNetwork = true };
            listener.Prefixes.Add(url); listener.Start();
            Completed = Serve(responses);
        }
        private async Task Serve((int Status, string Body)[] responses)
        {
            foreach (var response in responses)
            {
                var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(context.Request.InputStream);
                Requests.Add((context.Request.HttpMethod, context.Request.RawUrl!, await reader.ReadToEndAsync(), context.Request.Headers["X-Api-Key"]));
                context.Response.StatusCode = response.Status; context.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(response.Body); context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close();
            }
        }
        public async ValueTask DisposeAsync() { listener.Close(); try { await Completed; } catch (HttpListenerException) { } }
    }
}
