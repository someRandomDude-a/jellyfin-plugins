using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.UserManager.Services;

public static class SeerrDestination
{
    public static Uri ApiUri(string baseUrl, string path, bool allowHttp)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == "https" || allowHttp && uri.Scheme == "http") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase))
            throw new SeerrException(400, "Unsafe Seerr destination. HTTP requires explicit approval.");
        var prefix = uri.AbsoluteUri.TrimEnd('/');
        if (!prefix.EndsWith("/api/v1", StringComparison.Ordinal)) prefix += "/api/v1";
        return new Uri(prefix + '/' + path.TrimStart('/'));
    }
    public static bool AllowedAddress(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (b[0] is 0 or >= 224 || b[0] == 169 && b[1] == 254 || b[0] == 100 && b[1] is >= 64 and <= 127 ||
                b[0] == 192 && b[1] == 0 && b[2] is 0 or 2 || b[0] == 198 && b[1] is 18 or 19 || b[0] == 198 && b[1] == 51 && b[2] == 100 || b[0] == 203 && b[1] == 0 && b[2] == 113) return false;
            var local = b[0] == 10 || b[0] == 127 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
            return !local || allowPrivate;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.Equals(IPAddress.IPv6Loopback) || (b[0] & 0xfe) == 0xfc) return allowPrivate;
        return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8);
    }
    public static SocketsHttpHandler Handler(Uri target, bool allowPrivate) => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (context, ct) =>
        {
            if (!string.Equals(context.DnsEndPoint.Host, target.IdnHost, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != target.Port) throw new HttpRequestException("Seerr destination changed");
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            if (addresses.Length == 0 || addresses.Any(a => !AllowedAddress(a, allowPrivate))) throw new HttpRequestException("Seerr destination address is not approved");
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try { await socket.ConnectAsync(address, target.Port, ct); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); if (ct.IsCancellationRequested) throw; }
            }
            throw new HttpRequestException("Seerr connection unavailable");
        }
    };
}
