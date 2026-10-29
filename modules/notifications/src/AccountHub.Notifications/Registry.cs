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
        Execute(db, "CREATE TABLE IF NOT EXISTS notification_registrations(source TEXT PRIMARY KEY,revision INTEGER NOT NULL,json TEXT NOT NULL); CREATE TABLE IF NOT EXISTS notification_layouts(source TEXT NOT NULL,type TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(source,type)); CREATE TABLE IF NOT EXISTS notification_defaults(source TEXT NOT NULL,type TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(source,type));");
        if (Scalar(db, "SELECT json FROM notification_registrations WHERE source=$0", registration.Source) == json) return 0;
        Execute(db, "INSERT INTO notification_registrations VALUES($0,1,$1) ON CONFLICT(source) DO UPDATE SET revision=revision+1,json=excluded.json", registration.Source, json);
        // Overrides remain inert while a type is absent, and survive producer
        // upgrades/reinstallation instead of silently resetting administrator work.
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
                var defaults = Convert.ToInt32(Scalar(db,"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='notification_defaults'")) == 0 ? null : Scalar(db,"SELECT json FROM notification_defaults WHERE source=$0 AND type=$1",registration.Source,type.Type);
                return type with { Layout = layout is null ? type.Layout : JsonSerializer.Deserialize<NotificationLayout>(layout, Contract.Json)!, Defaults = defaults is null ? type.Defaults : JsonSerializer.Deserialize<NotificationDefaults>(defaults, Contract.Json)! };
            }).ToArray();
            var grouping = registration.Grouping;
            if (grouping is not null && Convert.ToInt32(Scalar(db,"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='notification_grouping'")) != 0 && Scalar(db,"SELECT enabled FROM notification_grouping WHERE source=$0",registration.Source) is { } enabled)
                grouping = grouping with { DefaultEnabled = enabled == "1" };
            catalogs.Add(new(registration.Source, rows.GetInt64(1), registration.Version, types) { RequireAccess = registration.RequireAccess, Grouping = grouping });
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
    public void SaveNotificationDefaults(NotificationDefaultsUpdate update) => Run(db =>
    {
        if (!RegistryExists(db)) throw new NotificationException("not_found", "Notification type unavailable", 404);
        using var tx = db.BeginTransaction(deferred:false);
        var json = Scalar(db,"SELECT json FROM notification_registrations WHERE source=$0",update.Source);
        if (json is null || !JsonSerializer.Deserialize<NotificationRegistration>(json,Contract.Json)!.Types.Any(t=>t.Type==update.Type)) throw new NotificationException("not_found","Notification type unavailable",404);
        if (Execute(db,"UPDATE notification_registrations SET revision=revision+1 WHERE source=$0 AND revision=$1",update.Source,update.Revision)!=1) throw new NotificationException("revision_changed","Reload notification types",409);
        Execute(db,"CREATE TABLE IF NOT EXISTS notification_defaults(source TEXT NOT NULL,type TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(source,type))");
        Execute(db,"INSERT INTO notification_defaults VALUES($0,$1,$2) ON CONFLICT(source,type) DO UPDATE SET json=excluded.json",update.Source,update.Type,Contract.Serialize(update.Defaults));
        tx.Commit(); return 0;
    });
    public void SaveNotificationGrouping(NotificationGroupingUpdate update) => Run(db => {
        if (!RegistryExists(db)) throw new NotificationException("not_found", "Notification plugin unavailable",404);
        using var tx=db.BeginTransaction(deferred:false);
        var json=Scalar(db,"SELECT json FROM notification_registrations WHERE source=$0",update.Source);
        if(json is null || JsonSerializer.Deserialize<NotificationRegistration>(json,Contract.Json)!.Grouping is null) throw new NotificationException("not_found","Plugin grouping unavailable",404);
        if(Execute(db,"UPDATE notification_registrations SET revision=revision+1 WHERE source=$0 AND revision=$1",update.Source,update.Revision)!=1) throw new NotificationException("revision_changed","Reload notification types",409);
        Execute(db,"CREATE TABLE IF NOT EXISTS notification_grouping(source TEXT PRIMARY KEY,enabled INTEGER NOT NULL)");
        Execute(db,"INSERT INTO notification_grouping VALUES($0,$1) ON CONFLICT(source) DO UPDATE SET enabled=excluded.enabled",update.Source,update.Enabled?1:0);
        tx.Commit();return 0;
    });
    public NotificationGroupingPreference[] GroupingPreferences(Guid user) => Run(db => {
        if(Convert.ToInt32(Scalar(db,"SELECT count(*) FROM sqlite_master WHERE type='table' AND name='notification_grouping_preferences'"))==0)return [];
        return JsonSerializer.Deserialize<NotificationGroupingPreference[]>(Scalar(db,"SELECT json FROM notification_grouping_preferences WHERE user_id=$0",user)??"[]",Contract.Json)!;
    });
    public void SetGroupingPreference(Guid user,NotificationGroupingPreference preference) => Run(db => {
        Validation.Require(user!=Guid.Empty && preference.Source is {Length:>0 and <=100},"Invalid grouping preference");
        using var tx=db.BeginTransaction(deferred:false);
        Execute(db,"CREATE TABLE IF NOT EXISTS notification_grouping_preferences(user_id TEXT PRIMARY KEY,json TEXT NOT NULL)");
        var current=JsonSerializer.Deserialize<NotificationGroupingPreference[]>(Scalar(db,"SELECT json FROM notification_grouping_preferences WHERE user_id=$0",user)??"[]",Contract.Json)!;
        var updated=current.Where(value=>value.Source!=preference.Source).Concat(preference.Enabled is null?[]:[preference]).ToArray();
        Validation.Require(updated.Length<=100,"Too many grouping preferences");
        Execute(db,"INSERT INTO notification_grouping_preferences VALUES($0,$1) ON CONFLICT(user_id) DO UPDATE SET json=excluded.json",user,Contract.Serialize(updated));
        tx.Commit();return 0;
    });
}
