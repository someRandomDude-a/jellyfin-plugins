using System.Net;
using Jellyfin.Plugin.UserManager.Services;
using Jellyfin.Plugin.UserManager.Security;
using Xunit;

namespace Jellyfin.Plugin.UserManager.Tests;

public sealed class SeerrSafetyTests
{
    [Theory]
    [InlineData("https://example.com", "https://example.com/api/v1/user")]
    [InlineData("https://example.com/seerr/", "https://example.com/seerr/api/v1/user")]
    [InlineData("https://example.com/seerr/api/v1/", "https://example.com/seerr/api/v1/user")]
    public void ApiPrefixIsAddedExactlyOnce(string input, string expected) => Assert.Equal(expected, SeerrDestination.ApiUri(input, "/user", false).AbsoluteUri);
    [Theory]
    [InlineData("http://example.com")]
    [InlineData("https://user:secret@example.com")]
    [InlineData("https://example.com?key=secret")]
    [InlineData("https://metadata.google.internal")]
    public void UnsafeDestinationsAreRejected(string input) => Assert.Throws<SeerrException>(() => SeerrDestination.ApiUri(input, "/user", false));
    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("0.0.0.0")]
    [InlineData("ff02::1")]
    [InlineData("fe80::1")]
    [InlineData("100.100.100.200")]
    public void MetadataAndSpecialAddressesAreNeverApproved(string input) => Assert.False(SeerrDestination.AllowedAddress(IPAddress.Parse(input), true));
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.5")]
    [InlineData("::1")]
    [InlineData("fd00::1")]
    public void PrivateDestinationsNeedExplicitApproval(string input)
    { Assert.False(SeerrDestination.AllowedAddress(IPAddress.Parse(input), false)); Assert.True(SeerrDestination.AllowedAddress(IPAddress.Parse(input), true)); }
    [Fact]
    public void UnknownPermissionBitsArePreservedAndAdminCannotBeGranted()
    {
        const long unknown = 1L << 45;
        Assert.Equal(unknown | SeerrPermissions.Request, SeerrPermissions.Merge(unknown | SeerrPermissions.RequestMovie, SeerrPermissions.Request));
        Assert.Throws<SeerrException>(() => SeerrPermissions.Merge(0, SeerrPermissions.Admin));
        Assert.Throws<SeerrException>(() => SeerrPermissions.Merge(0, unknown));
    }
    [Fact]
    public void MissingLifecycleKeyCannotBeSilentlyRegenerated()
    {
        using var folder = new TempFolder(); var protector = new SecretProtector(folder.Path); var cipher = protector.Protect("test-secret");
        File.Delete(protector.KeyPath);
        Assert.Throws<System.Security.Cryptography.CryptographicException>(() => new SecretProtector(folder.Path).Unprotect(cipher));
        Assert.False(File.Exists(protector.KeyPath));
    }
}
