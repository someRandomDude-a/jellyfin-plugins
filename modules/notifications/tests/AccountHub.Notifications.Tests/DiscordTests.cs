using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class DiscordTests
{
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
    [Theory]
    [InlineData(403, false)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    public async Task Forbidden_is_terminal_rate_limit_and_5xx_retry(int status, bool retry)
    {
        using var client = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"retry_after\":123}") })));
        var bot = new DiscordBotTransport(client, () => "bot-secret");
        var result = await bot.SendAsync("111", true, "notice", Guid.NewGuid().ToString(), default);
        Assert.False(result.Delivered); Assert.Equal(retry, result.Retryable);
        if (status == 429) Assert.Equal(TimeSpan.FromSeconds(123), result.RetryAfter);
    }
    [Fact]
    public async Task DM_resolves_recipient_suppresses_mentions_and_reuses_nonce()
    {
        var payloads = new List<string>();
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("Bot", request.Headers.Authorization!.Scheme);
            var body = await request.Content!.ReadAsStringAsync();
            payloads.Add(body);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"id\":\"222\"}", Encoding.UTF8, "application/json") };
        }));
        var bot = new DiscordBotTransport(client, () => "bot-secret"); var id = Guid.NewGuid().ToString();
        Assert.True((await bot.SendAsync("111", true, "@everyone private notice", id, default)).Delivered);
        Assert.True((await bot.SendAsync("111", true, "@everyone private notice", id, default)).Delivered);
        Assert.Equal(payloads[1], payloads[3]);
        using var json = JsonDocument.Parse(payloads[1]); Assert.Empty(json.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
        Assert.True(json.RootElement.GetProperty("enforce_nonce").GetBoolean());
    }
}
