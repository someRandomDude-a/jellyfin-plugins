using System.Net;
using System.Text.Json;
using DeletionQueue.Core;
using DeletionQueue.Infrastructure;

namespace DeletionQueue.Tests;

public sealed class SeerrDelegationTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Calls++; return Task.FromResult(send(request)); }
    }
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();
    private sealed class DeadlineContent : HttpContent
    {
        public bool SawDeadline;
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new InvalidOperationException("A body deadline is required");
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct) {
            SawDeadline = ct.CanBeCanceled; Assert.True(SawDeadline);
            await stream.WriteAsync("{}"u8.ToArray(), ct);
        }
    }
    [Fact] public async Task SeerrBodyReadsHaveADeadlineEvenWithoutCallerCancellation() {
        using var content = new DeadlineContent();
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = content });
        using var client = new SeerrClient(new Uri("https://seerr.invalid"), "fixture-secret", handler);
        await client.StatusAsync(CancellationToken.None);
        Assert.True(content.SawDeadline); Assert.Equal(1, handler.Calls);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task TransportUsesOnlyTheSeerrFileDeletionEndpointAndRequires204(bool is4K)
    {
        using var handler = new Handler(r =>
        {
            Assert.Equal(HttpMethod.Delete, r.Method);
            Assert.Equal($"/prefix/api/v1/media/42/file?is4k={(is4K ? "true" : "false")}", r.RequestUri!.PathAndQuery);
            Assert.Equal("fixture-secret", Assert.Single(r.Headers.GetValues("X-Api-Key")));
            Assert.Null(r.Content);
            return new(HttpStatusCode.NoContent);
        });
        using var client = new SeerrClient(new Uri("https://seerr.invalid/prefix"), "fixture-secret", handler);
        await client.DeleteFilesOnceAsync(42, is4K, default); Assert.Equal(1, handler.Calls);
    }

    [Theory] [InlineData(200)] [InlineData(302)] [InlineData(404)] [InlineData(409)] [InlineData(500)]
    public async Task UnexpectedDeletionResponsesNeverRetry(int status)
    {
        using var handler = new Handler(_ => new((HttpStatusCode)status));
        using var client = new SeerrClient(new Uri("https://seerr.invalid"), "fixture-secret", handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteFilesOnceAsync(42, false, default));
        Assert.Equal(1, handler.Calls);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.DeleteFilesOnceAsync(0, false, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact] public async Task SeriesOwnershipRequiresEveryManagedSeasonAndNoOtherApprovedOwner()
    {
        var owner = Guid.NewGuid(); var other = Guid.NewGuid();
        Task<JsonElement> User(int id, CancellationToken _) => Task.FromResult(Json($$"""{"jellyfinUserId":"{{(id == 1 ? owner : other)}}"}"""));
        var info = Json("""{"seasons":[{"seasonNumber":1,"status":5},{"seasonNumber":2,"status":4}],"requests":[{"status":2,"is4k":false,"requestedBy":{"id":1},"seasons":[{"seasonNumber":1},{"seasonNumber":2}]}]}""");
        Assert.True(await SeerrOwnership.OwnsAsync(info, owner, false, true, User, default));
        var partial = Json(info.GetRawText().Replace(",{" + "\"seasonNumber\":2}", ""));
        Assert.False(await SeerrOwnership.OwnsAsync(partial, owner, false, true, User, default));
        var mixed = Json(info.GetRawText().Replace("\"id\":1", "\"id\":2"));
        Assert.False(await SeerrOwnership.OwnsAsync(mixed, owner, false, true, User, default));
        Assert.False(await SeerrOwnership.OwnsAsync(info, owner, true, true, User, default));
        Assert.False(await SeerrOwnership.OwnsAsync(Json("{}"), owner, false, true, User, default));
    }

    [Fact] public async Task MissingOrExcessiveOwnershipEvidenceFailsClosed()
    {
        var owner = Guid.NewGuid();
        Task<JsonElement> User(int id, CancellationToken _) => Task.FromResult(Json("{}"));
        var info = Json("""{"requests":[{"status":2,"requestedBy":{"id":1}}]}""");
        Assert.False(await SeerrOwnership.OwnsAsync(info, owner, false, false, User, default));
        info = Json("{\"requests\":[" + string.Join(',', Enumerable.Repeat("{}", 65)) + "]}");
        await Assert.ThrowsAsync<QueueException>(() => SeerrOwnership.OwnsAsync(info, owner, false, false, User, default));
    }

    [Fact] public async Task RequesterMetadataUsesVerifiedSeerrMappingsNotAnAdministratorSelection() {
        var owner=Guid.NewGuid();var other=Guid.NewGuid();
        Task<JsonElement> User(int id,CancellationToken _) => Task.FromResult(Json($$"""{"id":{{id}},"jellyfinUserId":"{{(id==1?owner:other)}}"}"""));
        var info=Json("""{"requests":[{"id":10,"status":2,"is4k":false,"requestedBy":{"id":1}}]}""");
        Assert.Equal(owner,await SeerrOwnership.RequesterAsync(info,false,User,_=>true,default));
        Assert.Null(await SeerrOwnership.RequesterAsync(info,true,User,_=>true,default));
        Assert.Null(await SeerrOwnership.RequesterAsync(info,false,User,_=>false,default));
        Assert.Null(await SeerrOwnership.RequesterAsync(info,false,(_,_)=>Task.FromResult(Json("{}")),_=>true,default));
        Assert.Null(await SeerrOwnership.RequesterAsync(info,false,(_,_)=>Task.FromResult(Json($$"""{"id":9,"jellyfinUserId":"{{owner}}"}""")),_=>true,default));
        var mixed=Json("""{"requests":[{"id":10,"status":2,"is4k":false,"requestedBy":{"id":1}},{"id":11,"status":2,"is4k":false,"requestedBy":{"id":2}}]}""");
        Assert.Null(await SeerrOwnership.RequesterAsync(mixed,false,User,_=>true,default));
        foreach(var invalid in new[]{"{}","{\"requests\":[]}","{\"requests\":[{}]}","{\"requests\":["+string.Join(',',Enumerable.Repeat("{}",65))+"]}"})
            Assert.Null(await SeerrOwnership.RequesterAsync(Json(invalid),false,User,_=>true,default));
    }

    [Fact] public void LegacyPreviewOptInDoesNotAcknowledgeRealDeletion()
    {
        var policy = JsonSerializer.Deserialize<Policy>("""{"AutomaticDeletion":true}""")!;
        Assert.True(policy.AutomaticDeletion); Assert.False(policy.SeerrManagedDeletionAcknowledged);
    }
}
