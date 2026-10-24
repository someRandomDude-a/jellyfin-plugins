using System.Text.Json;
using Xunit;

namespace AccountHub.Security.Tests;

public sealed class NativeSerializationTests
{
    [Theory]
    [InlineData("3d280094885f4a9ba04bbad30b7fe39b")]
    [InlineData("3d280094-885f-4a9b-a04b-bad30b7fe39b")]
    public void RealJellyfinCompactAndStandardGuidsAreAccepted(string id)
    {
        var status = JsonSerializer.Deserialize<UserStatus>("{\"UserId\":\"" + id + "\",\"Username\":\"fixture\",\"TotpEnabled\":false,\"EmailOtpEnabled\":false,\"PasskeyCount\":0,\"RecoveryCodesRemaining\":0}", SecurityJson.Options)!;
        Assert.Equal(Guid.Parse(id), status.UserId); Assert.False(status.HasFactor);
    }
    [Fact]
    public void UnknownIdentityStillFailsClosed() => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UserStatus>("{\"UserId\":\"not-an-id\"}", SecurityJson.Options));
}
