using AccountHub.Api;
using Jellyfin.Data;
using AccountHub.Notifications;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.PluginSupport;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.Notifications;

public sealed class NativeHost(IServiceProvider services, Storage storage) : INotificationHost
{
    public IReadOnlyList<NotificationRegistration> NotificationRegistrations {
        get {
            var registrations = new Dictionary<string, NotificationRegistration>(StringComparer.Ordinal);
            foreach (var descriptor in services.GetServices<Tuple<string, Func<string>>>()) {
                if (!descriptor.Item1.StartsWith(NotificationIntegration.RegistrationPrefix, StringComparison.Ordinal)) continue;
                try {
                    var registration = System.Text.Json.JsonSerializer.Deserialize<NotificationRegistration>(descriptor.Item2(), Contract.Json);
                    if (registration is not null && descriptor.Item1 == NotificationIntegration.RegistrationPrefix + registration.Source)
                        registrations[registration.Source] = registration with { RequireAccess = registration.RequireAccess || services.GetKeyedService<Func<Guid, string, CancellationToken, Task<bool>>>(NotificationIntegration.AccessPrefix + registration.Source) is not null };
                } catch { /* A malformed optional descriptor cannot break other producers. */ }
            }
            return registrations.Values.ToArray();
        }
    }
    private IUserManager Users => services.GetRequiredService<IUserManager>();
    public bool NotificationAccessAvailable(string source)
    {
        try { return services.GetKeyedService<Func<Guid, string, CancellationToken, Task<bool>>>(NotificationIntegration.AccessPrefix + source) is not null; }
        catch { return false; }
    }
    public async Task<Guid?> CurrentUserAsync(HttpContext context, CancellationToken ct)
    {
        try { return (await NativeAuthorization.RequireAsync(context, services, administrator:false, elevated:false)).UserId; }
        catch (NativeAuthorizationFailure) { return null; }
    }
    public Task<bool> UserExistsAsync(Guid id, CancellationToken ct) => Task.FromResult(Users.GetUserById(id) is { } u && !u.HasPermission(PermissionKind.IsDisabled));
    public Task<bool> IsAdministratorAsync(Guid id, CancellationToken ct) => Task.FromResult(Users.GetUserById(id) is { } u && !u.HasPermission(PermissionKind.IsDisabled) && u.HasPermission(PermissionKind.IsAdministrator));
    public Task<IReadOnlyList<Guid>> AdministratorsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>(Users.GetUsers().Where(u=>u.HasPermission(PermissionKind.IsAdministrator)&&!u.HasPermission(PermissionKind.IsDisabled)).Select(u=>u.Id).Take(1001).ToArray());
    public Task<bool> CanAccessNotificationAsync(Guid id, NotificationEvent entry, CancellationToken ct)
    {
        var scopedAccess = services.GetKeyedService<Func<Guid, string, CancellationToken, Task<bool>>>(NotificationIntegration.AccessPrefix + entry.Source);
        if (scopedAccess is not null && !entry.AdminOnly) return CheckAccess(scopedAccess, id, entry, ct);
        if (!entry.AdminOnly && entry.Metadata?.GetValueOrDefault("notificationAccess") == "required") return Task.FromResult(false);
        if (entry.Source == "deletion-queue" && !entry.AdminOnly && entry.Metadata?.ContainsKey("proposalId") == true)
        {
            var access = services.GetKeyedService<Func<Guid, string, CancellationToken, Task<bool>>>("Notifications.Access.deletion-queue.1");
            return access?.Invoke(id, Contract.Serialize(entry), ct) ?? Task.FromResult(false);
        }
        if (!entry.Type.StartsWith("library.", StringComparison.Ordinal)) return UserExistsAsync(id,ct);
        if (entry.Metadata is null || !entry.Metadata.TryGetValue("itemId",out var raw) || !Guid.TryParse(raw,out var itemId)) return Task.FromResult(false);
        try { var user=Users.GetUserById(id); var item=services.GetRequiredService<ILibraryManager>().GetItemById(itemId); return Task.FromResult(user is not null && !user.HasPermission(PermissionKind.IsDisabled) && item?.IsVisibleStandalone(user)==true); }
        catch { return Task.FromResult(false); }
    }
    private static async Task<bool> CheckAccess(Func<Guid, string, CancellationToken, Task<bool>> access, Guid id, NotificationEvent entry, CancellationToken ct)
    {
        try { return await access(id, Contract.Serialize(entry), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
    public async Task AuthorizeManagementAsync(HttpContext context, bool configurationWrite, CancellationToken ct)
    {
        try { await NativeAuthorization.RequireAsync(context,services,administrator:true,elevated:true); }
        catch (NativeAuthorizationFailure) { throw new NotificationException("unauthorized","Administrator required",403); }
    }
    public Task AuthorizeAuditAsync(HttpContext context,CancellationToken ct) => AuthorizeManagementAsync(context,false,ct);
    public Task<string?> DiscordLinkAsync(Guid id,CancellationToken ct) => services.GetKeyedService<Func<Guid,CancellationToken,Task<string?>>>("Notifications.DiscordLink.1")?.Invoke(id,ct) ?? Task.FromResult<string?>(null);
    public async Task<object> AppendAuditAsync(AuditEvent entry,CancellationToken ct)
    {
        var upstream=services.GetKeyedService<Func<AuditEvent,CancellationToken,Task<object>>>("Notifications.HubAudit.1");
        if(upstream is not null && !entry.EventType.StartsWith("notifications.",StringComparison.Ordinal)) return await upstream(entry,ct);
        Directory.CreateDirectory(storage.DirectoryPath);
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(storage.DirectoryPath,"audit.db") }.ToString());
        await db.OpenAsync(ct);
        using var command=db.CreateCommand();
        command.CommandText="CREATE TABLE IF NOT EXISTS notification_audit(id TEXT PRIMARY KEY,hash TEXT NOT NULL,payload TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync(ct);
        using var transaction=db.BeginTransaction(deferred:false);command.Transaction=transaction;
        var json=Contract.Serialize(entry);
        var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)));
        command.CommandText="SELECT hash FROM notification_audit WHERE id=$id";command.Parameters.AddWithValue("$id",entry.EventId.ToString("D"));
        var prior=await command.ExecuteScalarAsync(ct) as string;
        if(prior is not null && prior!=hash)throw new NotificationException("rejected","Audit event identity conflict",409);
        if(prior is null){
            command.CommandText="INSERT INTO notification_audit VALUES($id,$hash,$body)";
            command.Parameters.AddWithValue("$hash",hash);
            command.Parameters.AddWithValue("$body",storage.Protection().CreateProtector("Notifications.Audit.1").Protect(json));
            await command.ExecuteNonQueryAsync(ct);
        }
        transaction.Commit();return new { status=prior is null?"accepted":"duplicate" };
    }
    public Task<object> ExportAuditAsync(string format,CancellationToken ct) => Task.FromException<object>(new NotificationException("unsupported_operation","Use the owning audit provider",409));
    public bool SecurityCompatible => false;
    public string SecurityAlertMode => "normal";
    public string? SecurityWebhookSecret => null;
}
public sealed class BridgeTransport(DiscordBridge.Api.DiscordBridgeClient bridge) : IDiscordTransport
{
    public async Task<SendResult> SendAsync(string destination,bool directMessage,string text,string deliveryId,CancellationToken ct)
    {
        var result=await bridge.CallAsync("send",new { destination,directMessage,text,deliveryId },ct);
        return result is { } value && value.GetProperty("ok").GetBoolean() ? new(true) : new(false,true,"discord_bridge_unavailable");
    }
}
