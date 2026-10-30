using System.Security.Cryptography;
using System.Text.Json;

namespace DeletionQueue.Core;

public sealed record MediaIdentityDifference(string Field, string Expected, string Current);

public static class MediaIdentity
{
    public static IReadOnlyList<MediaIdentityDifference> Differences(VerifiedTarget saved, VerifiedTarget current)
    {
        var differences = new List<MediaIdentityDifference>();
        void Check<T>(string field, T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) differences.Add(new(field, expected?.ToString() ?? "none", actual?.ToString() ?? "none")); }
        Check("Media key", saved.Key, current.Key); Check("Provider identity", saved.ActivityKey, current.ActivityKey);
        Check("Jellyfin item ID", saved.JellyfinItemId, current.JellyfinItemId); Check("Media kind", saved.Kind, current.Kind);
        Check("Seerr media ID", saved.SeerrMediaId, current.SeerrMediaId); Check("4K edition", saved.Is4K, current.Is4K);
        Check("Sonarr/Radarr target", saved.DestructiveScopeKey, current.DestructiveScopeKey);
        Check("Request ID", saved.RequestId, current.RequestId); Check("Request owner", saved.RequestOwnerId, current.RequestOwnerId);
        Check("Request-only operation", saved.RequestOnly, current.RequestOnly);
        if (!current.MappingVerified) differences.Add(new("Live mapping", "verified", "unverified"));
        return differences;
    }

    public static string Fingerprint(VerifiedTarget target) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
        target.Key, target.ActivityKey, target.JellyfinItemId, target.Kind, target.SeerrMediaId, target.Is4K,
        target.DestructiveScopeKey, target.RequestId, target.RequestOwnerId, target.RequestOnly
    })));

    // Keep existing consent/approval snapshots and durable claims byte-for-byte.
    // Re-resolution verifies concrete identifiers instead of rewriting old hashes.
    public static VerifiedTarget PreserveExistingFingerprint(VerifiedTarget current, IEnumerable<Proposal> proposals)
    {
        var existing = proposals.FirstOrDefault(p => !p.Terminal && Differences(p.Target, current).Count == 0);
        return current with { Fingerprint = existing?.Target.Fingerprint ?? Fingerprint(current) };
    }
}
