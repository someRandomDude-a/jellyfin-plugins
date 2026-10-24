namespace AccountHub.Security;

// Request-scoped only. The host resolves this from Jellyfin's authorization context,
// never from request body IDs. Endpoint is trusted server configuration, NOT Host header.
public sealed class CallerSession(Guid userId, string accessToken, Uri serverBaseUri)
{
    public Guid UserId { get; } = userId;
    internal string AccessToken { get; } = accessToken;
    internal Uri ServerBaseUri { get; } = Validate(serverBaseUri);
    public override string ToString() => "[redacted caller session]";

    private static Uri Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http") || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith('/'))
            throw new ArgumentException("A trusted Jellyfin base URI ending in / is required.");
        return uri;
    }
}
