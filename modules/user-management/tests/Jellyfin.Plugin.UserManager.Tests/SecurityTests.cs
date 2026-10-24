using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.UserManager.Security;
using Xunit;

namespace Jellyfin.Plugin.UserManager.Tests;

public sealed class TokenToolsTests
{
    [Fact]
    public void CreateUrlToken_IsRandomUrlSafeAndLong()
    {
        var first = TokenTools.CreateUrlToken();
        var second = TokenTools.CreateUrlToken();

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length);
        Assert.DoesNotContain('+', first);
        Assert.DoesNotContain('/', first);
        Assert.DoesNotContain('=', first);
    }

    [Fact]
    public void HashToken_IsStableSha256Hex()
    {
        var hash = TokenTools.HashToken("hello world");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, TokenTools.HashToken("hello world"));
        Assert.Equal("b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9", hash);
        Assert.True(hash.All(c => "0123456789abcdef".Contains(c, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("K7P2-Q9XM", "K7P2Q9XM")]
    [InlineData(" k7p2 q9xm ", "K7P2Q9XM")]
    [InlineData("k7p2.q9xm", "K7P2Q9XM")]
    public void NormalizeCode_IgnoresCaseAndSeparators(string input, string expected)
    {
        Assert.Equal(expected, TokenTools.NormalizeCode(input));
    }

    [Fact]
    public void HashCode_MatchesAcrossFormatting()
    {
        Assert.Equal(TokenTools.HashCode("K7P2-Q9XM"), TokenTools.HashCode("k7p2q9xm"));
    }

    [Fact]
    public void CreateLinkCode_HasExpectedShape()
    {
        var code = TokenTools.CreateLinkCode();

        Assert.Equal(9, code.Length);
        Assert.Equal('-', code[4]);
        Assert.All(code.Where((_, index) => index != 4), c => Assert.True("ABCDEFGHJKLMNPQRSTUVWXYZ23456789".Contains(c)));
    }

    [Fact]
    public void FixedTimeEquals_ComparesExactValues()
    {
        Assert.True(TokenTools.FixedTimeEquals("abc", "abc"));
        Assert.False(TokenTools.FixedTimeEquals("abc", "abd"));
        Assert.False(TokenTools.FixedTimeEquals("abc", "abcd"));
    }
}

public sealed class Base64UrlTests
{
    [Fact]
    public void EncodeDecode_RoundTrips()
    {
        for (var length = 0; length < 20; length++)
        {
            var data = Enumerable.Range(0, length).Select(i => (byte)(i * 17)).ToArray();
            var encoded = Base64Url.Encode(data);

            Assert.DoesNotContain('=', encoded);
            Assert.DoesNotContain('+', encoded);
            Assert.DoesNotContain('/', encoded);
            Assert.Equal(data, Base64Url.Decode(encoded));
        }
    }

    [Fact]
    public void Decode_RejectsInvalidLength()
    {
        Assert.Throws<FormatException>(() => Base64Url.Decode("a"));
    }
}

public sealed class SecretProtectorTests
{
    [Fact]
    public void Protect_Unprotect_RoundTrips()
    {
        using var folder = new TempFolder();
        var protector = new SecretProtector(folder.Path);

        var cipher = protector.Protect("super-secret-token");

        Assert.NotEqual("super-secret-token", cipher);
        Assert.StartsWith("v1.", cipher, StringComparison.Ordinal);
        Assert.Equal("super-secret-token", protector.Unprotect(cipher));
        Assert.True(File.Exists(protector.KeyPath));
        Assert.Equal(32, new FileInfo(protector.KeyPath).Length);
    }

    [Fact]
    public void Protect_UsesFreshNonceEveryTime()
    {
        using var folder = new TempFolder();
        var protector = new SecretProtector(folder.Path);

        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }

    [Fact]
    public void Unprotect_WithDifferentKey_Fails()
    {
        using var first = new TempFolder();
        using var second = new TempFolder();

        var cipher = new SecretProtector(first.Path).Protect("secret");
        var other = new SecretProtector(second.Path);

        Assert.False(other.TryUnprotect(cipher, out var plaintext));
        Assert.Null(plaintext);
        Assert.ThrowsAny<Exception>(() => other.Unprotect(cipher));
    }

    [Fact]
    public void TryUnprotect_RejectsMalformedAndTamperedValues()
    {
        using var folder = new TempFolder();
        var protector = new SecretProtector(folder.Path);
        var cipher = protector.Protect("secret");
        var parts = cipher.Split('.');
        var tampered = string.Join('.', parts[0], parts[1], parts[2], parts[3].Substring(0, parts[3].Length - 2) + "AA");

        Assert.False(protector.TryUnprotect(null, out _));
        Assert.False(protector.TryUnprotect("", out _));
        Assert.False(protector.TryUnprotect("not-a-secret", out _));
        Assert.False(protector.TryUnprotect(tampered, out _));
    }

    [Fact]
    public void KeyFile_IsReusedAcrossInstances()
    {
        using var folder = new TempFolder();
        var first = new SecretProtector(folder.Path);
        var cipher = first.Protect("value");

        var second = new SecretProtector(folder.Path);

        Assert.Equal("value", second.Unprotect(cipher));
    }
}

public sealed class PasswordPolicyTests
{
    [Theory]
    [InlineData("CorrectHorse42!")]
    [InlineData("aB3$aB3$aB3$")]
    [InlineData("LongEnoughPassword1")]
    public void Validate_AcceptsStrongPasswords(string password)
    {
        Assert.True(PasswordPolicy.Validate(password).IsValid);
    }

    [Fact]
    public void Validate_RejectsShortPasswords()
    {
        var result = PasswordPolicy.Validate("Ab1!");

        Assert.False(result.IsValid);
        Assert.Contains("10", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsCommonPasswords()
    {
        Assert.False(PasswordPolicy.Validate("password123").IsValid);
    }

    [Fact]
    public void Validate_RejectsUsernameInPassword()
    {
        Assert.False(PasswordPolicy.Validate("AliceStrong42!", "alice").IsValid);
    }

    [Fact]
    public void Validate_RequiresThreeCharacterClasses()
    {
        Assert.False(PasswordPolicy.Validate("onlylowercase").IsValid);
        Assert.False(PasswordPolicy.Validate("lowercase1234").IsValid);
        Assert.True(PasswordPolicy.Validate("Lowercase1234").IsValid);
    }

    [Fact]
    public void ThrowIfInvalid_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => PasswordPolicy.ThrowIfInvalid("short", "user"));
        PasswordPolicy.ThrowIfInvalid("ValidPassword42", "user");
    }
}

public sealed class RateLimiterTests
{
    [Fact]
    public void IsAllowed_EnforcesFixedWindowLimit()
    {
        var limiter = new RateLimiter();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.IsAllowed("key", 3, TimeSpan.FromMinutes(1)));
        }

        Assert.False(limiter.IsAllowed("key", 3, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void IsAllowed_TracksKeysIndependently()
    {
        var limiter = new RateLimiter();

        Assert.True(limiter.IsAllowed("a", 1, TimeSpan.FromMinutes(1)));
        Assert.False(limiter.IsAllowed("a", 1, TimeSpan.FromMinutes(1)));
        Assert.True(limiter.IsAllowed("b", 1, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void IsAllowed_RejectsNonPositiveLimit()
    {
        var limiter = new RateLimiter();

        Assert.False(limiter.IsAllowed("key", 0, TimeSpan.FromMinutes(1)));
        Assert.False(limiter.IsAllowed("key", -1, TimeSpan.FromMinutes(1)));
    }
}
