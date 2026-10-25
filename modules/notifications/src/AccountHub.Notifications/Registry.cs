using System.Text.Json;
using AccountHub.Api;
using Microsoft.Data.Sqlite;

namespace AccountHub.Notifications;

public sealed partial class NotificationStore
{
    private static bool RegistryExists(SqliteConnection db) => Convert.ToInt32(Scalar(db, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='notification_registrations'")) == 1;
    public void RegisterTypes(NotificationRegistration registration) => Run(db =>
    {
        var json = Contract.Serialize(registration);
        if (RegistryExists(db) && Scalar(db, "SELECT json FROM notification_registrations WHERE source=$0", registration.Source) == json) return 0;
        using var tx = db.BeginTransaction();
        Execute(db, "CREATE TABLE IF NOT EXISTS notification_registrations(source TEXT PRIMARY KEY,revision INTEGER NOT NULL,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS notification_layouts(source TEXT NOT NULL,type TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(source,type));");
        if (Scalar(db, "SELECT json FROM notification_registrations WHERE source=$0", registration.Source) == json) return 0;
        Execute(db, "INSERT INTO notification_registrations VALUES($0,1,$1) ON CONFLICT(source) DO UPDATE SET revision=revision+1,json=excluded.json", registration.Source, json);
        var removed = new List<string>();
        using (var command = Command(db, "SELECT type FROM notification_layouts WHERE source=$0", registration.Source)) {
            using var rows = command.ExecuteReader(); while (rows.Read()) if (!registration.Types.Any(t => t.Type == rows.GetString(0))) removed.Add(rows.GetString(0));
        }
        foreach (var type in removed) Execute(db, "DELETE FROM notification_layouts WHERE source=$0 AND type=$1", registration.Source, type);
        tx.Commit(); return 0;
    });
    public NotificationCatalogEntry[] NotificationTypes() => Run(db =>
    {
        if (!RegistryExists(db)) return [];
        var catalogs = new List<NotificationCatalogEntry>();
        using var command = Command(db, "SELECT source,revision,json FROM notification_registrations ORDER BY source");
        using var rows = command.ExecuteReader();
        while (rows.Read()) {
            var registration = JsonSerializer.Deserialize<NotificationRegistration>(rows.GetString(2), Contract.Json)!;
            var types = registration.Types.Select(type => {
                var layout = Scalar(db, "SELECT json FROM notification_layouts WHERE source=$0 AND type=$1", registration.Source, type.Type);
                return layout is null ? type : type with { Layout = JsonSerializer.Deserialize<NotificationLayout>(layout, Contract.Json)! };
            }).ToArray();
            catalogs.Add(new(registration.Source, rows.GetInt64(1), registration.Version, types) { RequireAccess = registration.RequireAccess });
        }
        return catalogs.ToArray();
    });
    public void SaveNotificationLayout(NotificationLayoutUpdate update) => Run(db =>
    {
        if (!RegistryExists(db)) throw new NotificationException("not_found", "Notification type unavailable", 404);
        using var tx = db.BeginTransaction();
        var json = Scalar(db, "SELECT json FROM notification_registrations WHERE source=$0", update.Source);
        if (json is null || !JsonSerializer.Deserialize<NotificationRegistration>(json, Contract.Json)!.Types.Any(type => type.Type == update.Type))
            throw new NotificationException("not_found", "Notification type unavailable", 404);
        using var change = Command(db, "UPDATE notification_registrations SET revision=revision+1 WHERE source=$0 AND revision=$1", update.Source, update.Revision);
        if (change.ExecuteNonQuery() != 1) throw new NotificationException("revision_changed", "Reload notification types", 409);
        Execute(db, "INSERT INTO notification_layouts VALUES($0,$1,$2) ON CONFLICT(source,type) DO UPDATE SET json=excluded.json", update.Source, update.Type, Contract.Serialize(update.Layout));
        tx.Commit(); return 0;
    });
}
