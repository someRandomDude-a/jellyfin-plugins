namespace AccountHub.Core.Data;

public sealed class DiscordChallenge
{
    public string TokenHash { get; set; } = "";
    public string DiscordId { get; set; } = "";
    public long ExpiresAtUnixMs { get; set; }
    public Guid? ConsumedBy { get; set; }
}

public sealed class HubState
{
    public int Id { get; set; } = 1;
    public long Revision { get; set; }
    public int AuditRetentionDays { get; set; } = 90;
    public int NotificationRetentionDays { get; set; } = 90;
    public string StepUpLevelMirror { get; set; } = "Destructive";
    public string PasswordLoginPolicy { get; set; } = "auto";
    public string SecurityAlertMode { get; set; } = "native";
    public long HeadSequence { get; set; }
    public string HeadHash { get; set; } = AuditLedger.Genesis;
    public long CheckpointSequence { get; set; }
    public string CheckpointHash { get; set; } = AuditLedger.Genesis;
}

public sealed class AuditRow
{
    public long Sequence { get; set; }
    public Guid Id { get; set; }
    public string EventJson { get; set; } = "";
    public string PreviousHash { get; set; } = "";
    public string EntryHash { get; set; } = "";
    public long RecordedAtUnixMs { get; set; }
}

// Kept separately from presentation rows, including after retention pruning.
public sealed class EventReceipt
{
    public string Source { get; set; } = "";
    public Guid EventId { get; set; }
    public string PayloadHash { get; set; } = "";
    public Guid AuditId { get; set; }
    public long AcceptedAtUnixMs { get; set; }
}

public sealed class CommandResult
{
    public Guid ActorUserId { get; set; }
    public string Operation { get; set; } = "";
    public Guid CommandId { get; set; }
    public string PayloadHash { get; set; } = "";
    public string ResultJson { get; set; } = "";
    public long CompletedAtUnixMs { get; set; }
}

public sealed class IntegrationSecret
{
    public string Name { get; set; } = "";
    public string Ciphertext { get; set; } = "";
    public long UpdatedAtUnixMs { get; set; }
}
