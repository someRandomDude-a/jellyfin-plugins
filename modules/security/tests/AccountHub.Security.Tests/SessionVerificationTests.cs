using Xunit;

namespace AccountHub.Security.Tests;

public sealed class SessionVerificationTests
{
    public sealed class NativeStore
    {
        public bool Verified;
        public bool Blocked;
        public bool BlockWhileReading;
        public bool Throw;
        public string? SeenToken;
        public bool IsTokenBlocked(string token) { SeenToken = token; if (Throw) throw new IOException(); return Blocked; }
        public bool IsTokenVerified(string token) { SeenToken = token; if (BlockWhileReading) Blocked = true; return Verified; }
    }
    private sealed class MissingContract { public bool IsTokenVerified(string token) => true; }

    [Theory] [InlineData(true, false, true)] [InlineData(false, false, false)] [InlineData(true, true, false)]
    public void ReadsTheExactNativeTokenAndNeverGrantsFromAnUnmarkedOrBlockedSession(bool verified, bool blocked, bool expected)
    {
        var native = new NativeStore { Verified = verified, Blocked = blocked };
        Assert.Equal(expected, JellyfinSecuritySessionVerification.ReadVerdict(native, "exact-native-token"));
        Assert.Equal("exact-native-token", native.SeenToken);
    }
    [Fact] public void NativeContractFailureIsUnknownRatherThanPermission()
    {
        Assert.Null(JellyfinSecuritySessionVerification.ReadVerdict(new MissingContract(), "token"));
        Assert.Null(JellyfinSecuritySessionVerification.ReadVerdict(new NativeStore { Throw = true }, "token"));
    }
    [Fact] public void AConcurrentNativeBlockWinsOverAVerifiedMarker()
        => Assert.False(JellyfinSecuritySessionVerification.ReadVerdict(new NativeStore { Verified = true, BlockWhileReading = true }, "token"));
}
