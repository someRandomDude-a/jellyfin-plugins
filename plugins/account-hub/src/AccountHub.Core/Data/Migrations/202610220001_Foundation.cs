using Microsoft.EntityFrameworkCore.Migrations;

namespace AccountHub.Core.Data.Migrations;

public sealed partial class Foundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE hub_state (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                Revision INTEGER NOT NULL,
                AuditRetentionDays INTEGER NOT NULL CHECK (AuditRetentionDays BETWEEN 15 AND 365),
                NotificationRetentionDays INTEGER NOT NULL CHECK (NotificationRetentionDays BETWEEN 7 AND 365),
                StepUpLevelMirror TEXT NOT NULL,
                PasswordLoginPolicy TEXT NOT NULL,
                SecurityAlertMode TEXT NOT NULL,
                HeadSequence INTEGER NOT NULL,
                HeadHash TEXT NOT NULL,
                CheckpointSequence INTEGER NOT NULL,
                CheckpointHash TEXT NOT NULL
            );
            CREATE TABLE audit_events (
                Sequence INTEGER NOT NULL PRIMARY KEY,
                Id TEXT NOT NULL,
                EventJson TEXT NOT NULL,
                PreviousHash TEXT NOT NULL,
                EntryHash TEXT NOT NULL,
                RecordedAtUnixMs INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IX_audit_events_Id ON audit_events (Id);
            CREATE INDEX IX_audit_events_RecordedAtUnixMs ON audit_events (RecordedAtUnixMs);
            CREATE TABLE event_receipts (
                Source TEXT NOT NULL,
                EventId TEXT NOT NULL,
                PayloadHash TEXT NOT NULL,
                AuditId TEXT NOT NULL,
                AcceptedAtUnixMs INTEGER NOT NULL,
                PRIMARY KEY (Source, EventId)
            );
            CREATE TABLE command_results (
                ActorUserId TEXT NOT NULL,
                Operation TEXT NOT NULL,
                CommandId TEXT NOT NULL,
                PayloadHash TEXT NOT NULL,
                ResultJson TEXT NOT NULL,
                CompletedAtUnixMs INTEGER NOT NULL,
                PRIMARY KEY (ActorUserId, Operation, CommandId)
            );
            CREATE TABLE integration_secrets (
                Name TEXT NOT NULL PRIMARY KEY,
                Ciphertext TEXT NOT NULL,
                UpdatedAtUnixMs INTEGER NOT NULL
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Account Hub migrations are forward-only. Restore a stopped, consistent database and key backup.");
}
