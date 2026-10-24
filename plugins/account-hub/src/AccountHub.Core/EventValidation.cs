using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AccountHub.Api.Contracts;

namespace AccountHub.Core;

public static partial class EventValidation
{
    private static readonly HashSet<string> Sources = ["account-hub", "deletion-queue", "private-syncplay", "jellyfin-security"];
    // Metadata is a summary contract, not an arbitrary payload archive. Unknown fields fail closed.
    private static readonly HashSet<string> MetadataFields = new(StringComparer.Ordinal)
    {
        "changedFields", "before", "after", "errorCode", "reason", "scope", "skippedAdminCount",
        "proposalId", "partyId", "guestLabel", "revision", "removedCount", "checkpointSequence",
        "fallbackReason", "secretName", "configured", "policy", "summary", "snapshotId", "status"
    };
    private static readonly HashSet<string> ManagedSummaryFields = new(StringComparer.Ordinal)
    {
        "auditRetentionDays", "notificationRetentionDays", "stepUpLevelMirror", "securityAlertMode",
        "passwordLoginPolicy", "MaxActiveSessions", "EnableRemoteAccess", "EnableContentDownloading",
        "EnableAudioPlaybackTranscoding", "EnableVideoPlaybackTranscoding", "EnablePlaybackRemuxing",
        "defaultAuthority", "activityDays", "delayHours", "automaticDeletion", "requireAdministratorApproval",
        "seerrManagedDeletionAcknowledged", "suspended", "accountOverrideCount", "preferencesCount",
        "connectionRevision", "notificationMode", "hubIntegrationEnabled", "publishAudit", "deviceBell",
        "hubFallbackAfterMinutes", "scope", "fields", "field", "before", "after", "userPolicies", "userId",
        "skippedAdminCount", "defaultMode", "createPublic", "createPrivate", "guestInvites",
        "enablePrivateGroups", "allowPublicGroups", "enableGuestInvites", "defaultGuestPermission",
        "maxViewers", "maxInviteUses", "guestLeaseSeconds", "guestVideoBitrateKbps", "maxGuestStreams",
        "guestPlayer", "nativeNotifications", "EnableMediaPlayback", "EnableAllFolders", "EnabledFolders", "MaxParentalRating"
    };

    public static void Audit(AuditEvent value)
    {
        if (value.SchemaVersion != 1 || value.EventId == Guid.Empty || !Sources.Contains(value.Source)
            || !DottedName().IsMatch(value.EventType) || value.EventType.Length > 128
            || value.Outcome is not ("success" or "failure" or "denied")
            || value.OccurredAt == default || value.OccurredAt.Offset != TimeSpan.Zero
            || value.ActorUserId == Guid.Empty || value.CorrelationId == Guid.Empty
            || value.SubjectType?.Length > 64 || value.SubjectId?.Length > 128)
            throw new HubException("invalid_request", "Invalid audit event fields");
        if (value.Metadata.ValueKind != JsonValueKind.Object)
            throw new HubException("invalid_request", "Metadata must be an object");
        foreach (var property in value.Metadata.EnumerateObject())
        {
            if (!MetadataFields.Contains(property.Name))
                throw new HubException("rejected", "Metadata field is not allowlisted");
            if (property.Name is "before" or "after")
            {
                if (property.Value.ValueKind != JsonValueKind.Object
                    || property.Value.EnumerateObject().Any(x => !ManagedSummaryFields.Contains(x.Name)))
                    throw new HubException("rejected", "Policy summary field is not allowlisted");
            }
            ValidateMetadataValue(property.Value, 0);
        }
        if (Encoding.UTF8.GetByteCount(CanonicalJson.Serialize(value)) > Wire.MaxEventBytes)
            throw new HubException("invalid_request", "Audit event exceeds 64 KB");
    }

    private static void ValidateMetadataValue(JsonElement element, int depth)
    {
        if (depth > 6) throw new HubException("rejected", "Metadata nesting exceeds limit");
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var item in element.EnumerateObject())
                {
                    if (!ManagedSummaryFields.Contains(item.Name))
                        throw new HubException("rejected", "Nested metadata field is not allowlisted");
                    ValidateMetadataValue(item.Value, depth + 1);
                }
                break;
            case JsonValueKind.Array:
                if (element.GetArrayLength() > 100) throw new HubException("rejected", "Metadata array exceeds limit");
                foreach (var item in element.EnumerateArray()) ValidateMetadataValue(item, depth + 1);
                break;
            case JsonValueKind.String:
                var text = element.GetString()!;
                if (text.Length > 256 || text.Contains("://", StringComparison.Ordinal) || text.Contains("#token", StringComparison.OrdinalIgnoreCase))
                    throw new HubException("rejected", "Metadata text exceeds summary constraints");
                break;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_-]*(\\.[a-z][a-z0-9_-]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex DottedName();
}
