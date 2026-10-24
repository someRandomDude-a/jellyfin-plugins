using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AccountHub.Api;

namespace AccountHub.Notifications;

public sealed class WebhookSender(TimeProvider time)
{
    public static void ValidateUri(Uri uri, Route route)
    {
        Validation.Require((uri.Scheme == "https" || (route.AllowHttp && uri.Scheme == "http")) &&
            string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.Query), "Unsafe webhook URL");
        Validation.Require(!uri.Host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase), "Metadata destination blocked");
    }
    public static bool AllowedAddress(IPAddress address, bool allowPrivate)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // Link-local/metadata, unspecified, multicast and reserved are NEVER approved.
            if (b[0] == 0 || b[0] >= 224 || (b[0] == 169 && b[1] == 254) ||
                (b[0] == 100 && b[1] is >= 64 and <= 127) ||
                (b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16) ||
                (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) ||
                (b[0] == 198 && b[1] is 18 or 19) ||
                (b[0] == 198 && b[1] == 51 && b[2] == 100) ||
                (b[0] == 203 && b[1] == 0 && b[2] == 113)) return false;
            var privateIp = b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
            return !privateIp || allowPrivate;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return false;
        // Permit global unicast only unless an administrator approved loopback/ULA.
        if ((b[0] == 0x20 && b[1] == 0x02) ||
            (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)) return false; // transition tunnels
        return (b[0] & 0xe0) == 0x20 || (allowPrivate && (IPAddress.IsLoopback(address) || (b[0] & 0xfe) == 0xfc));
    }
    public async Task<SendResult> SendAsync(Delivery delivery, CancellationToken ct)
    {
        try
        {
            var route = delivery.Route;
            var uri = new Uri(route.Destination);
            ValidateUri(uri, route);
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                MaxResponseHeadersLength = 16,
                // Resolve and pin on the actual connection, not a preflight DNS lookup.
                ConnectCallback = async (context, token) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                    if (addresses.Length == 0 || addresses.Any(a => !AllowedAddress(a, route.AllowPrivate)))
                        throw new NotificationException("rejected", "Destination blocked");
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(addresses[0], context.DnsEndPoint.Port, token); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            var stamp = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var signature = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(route.Secret!), Encoding.UTF8.GetBytes(stamp + "." + delivery.Body)));
            using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(delivery.Body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-AccountHub-Timestamp", stamp);
            request.Headers.Add("X-AccountHub-Signature", "sha256=" + signature);
            request.Headers.Add("Idempotency-Key", delivery.Id.ToString());
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            if (status is >= 200 and < 300) return new(true);
            return new(false, status == 429 || status >= 500, "http_" + status, RetryAfter(response.Headers.RetryAfter));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (NotificationException) { return new(false, false, "destination_blocked"); }
        catch (HttpRequestException ex) when (HasPolicyFailure(ex)) { return new(false, false, "destination_blocked"); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or SocketException)
        { return new(false, true, "network_failure"); }
    }
    private static bool HasPolicyFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
            if (current is NotificationException) return true;
        return false;
    }
    private TimeSpan? RetryAfter(RetryConditionHeaderValue? value) => value?.Delta ?? (value?.Date is { } date ? date - time.GetUtcNow() : null);
}
