using System.Security.Claims;
using AccountHub.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class FakeHost : INotificationHost
{
    public List<NotificationRegistration> Registrations { get; } = [];
    public IReadOnlyList<NotificationRegistration> NotificationRegistrations => Registrations;
    public HashSet<string> MissingAccessProviders { get; } = [];
    public bool NotificationAccessAvailable(string source) => !MissingAccessProviders.Contains(source);
    public Guid Alice { get; } = Guid.NewGuid();
    public Guid Bob { get; } = Guid.NewGuid();
    public HashSet<Guid> Admins { get; } = [];
    public HashSet<Guid> Deleted { get; } = [];
    public HashSet<Guid> LibraryDenied { get; } = [];
    public Dictionary<Guid, string> Links { get; } = [];
    public bool SecurityCompatible { get; set; } = true;
    public string SecurityAlertMode { get; set; } = "native";
    public string? SecurityWebhookSecret { get; set; } = new('s', 32);
    public Dictionary<Guid, AuditEvent> Audit { get; } = [];
    public bool DenyStepUp { get; set; }
    public bool FailAudit { get; set; }
    public Task<Guid?> CurrentUserAsync(HttpContext context, CancellationToken ct) => Task.FromResult(Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? (Guid?)id : null);
    public Task<bool> IsAdministratorAsync(Guid userId, CancellationToken ct) => Task.FromResult(Admins.Contains(userId));
    public Task<bool> UserExistsAsync(Guid userId, CancellationToken ct) => Task.FromResult(!Deleted.Contains(userId) && (userId == Alice || userId == Bob || Admins.Contains(userId)));
    public Task<bool> CanAccessNotificationAsync(Guid userId, NotificationEvent entry, CancellationToken ct) => Task.FromResult(!entry.Type.StartsWith("library.", StringComparison.Ordinal) || !LibraryDenied.Contains(userId));
    public Task<IReadOnlyList<Guid>> AdministratorsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Guid>>(Admins.ToArray());
    public Task AuthorizeManagementAsync(HttpContext context, bool configurationWrite, CancellationToken ct)
    {
        if (configurationWrite && DenyStepUp) throw new NotificationException("step_up_required", "Fresh proof required", 403);
        return Task.CompletedTask;
    }
    public Task AuthorizeAuditAsync(HttpContext context, CancellationToken ct) => AuthorizeManagementAsync(context, true, ct);
    public Task<string?> DiscordLinkAsync(Guid userId, CancellationToken ct) => Task.FromResult(Links.GetValueOrDefault(userId));
    public Task<object> AppendAuditAsync(AuditEvent entry, CancellationToken ct)
    {
        if (FailAudit) throw new InvalidOperationException("Core unavailable");
        var duplicate = Audit.ContainsKey(entry.EventId); Audit.TryAdd(entry.EventId, entry);
        return Task.FromResult<object>(new { status = duplicate ? "duplicate" : "accepted", auditId = entry.EventId });
    }
    public Task<object> ExportAuditAsync(string format, CancellationToken ct) => Task.FromResult<object>(Audit.Values.ToArray());
}
public sealed class FakeBot : IDiscordTransport
{
    public List<string> Sent { get; } = [];
    public SendResult Result { get; set; } = new(true, MessageId: "123");
    public Task<SendResult> SendAsync(string destination, bool directMessage, string text, string deliveryId, CancellationToken ct)
    { Sent.Add(destination + ":" + text); return Task.FromResult(Result); }
}
public sealed class Fixture
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "opencode", "notification-tests", Guid.NewGuid().ToString());
    public Clock Clock { get; } = new();
    public FakeHost Host { get; } = new();
    public FakeBot Bot { get; } = new();
    public NotificationOptions Options { get; }
    public IDataProtectionProvider Protection { get; }
    public NotificationStore Store { get; }
    public NotificationService Service { get; }
    public Fixture()
    {
        Directory.CreateDirectory(Root);
        Options = new() { DatabasePath = Path.Combine(Root, "hub.db") };
        Protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(Root, "keys")));
        Store = new(Options, Protection, Clock);
        Service = new(Store, Host, Options);
        Host.Admins.Add(Host.Bob);
    }
    public NotificationEvent Event(bool admin = false) => new(1, Guid.NewGuid(), "deletion-queue", "proposal.updated", "info", "Proposal updated", "Review your proposal.", admin ? [] : [Host.Alice], admin, Clock.Now);
    public Task<PublishResult> Publish(NotificationEvent e) => Service.PublishAsync(e, Hash(e), default);
    public static string Hash(NotificationEvent e) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Contract.Serialize(e))));
    public async Task<string> Dispatch(string operation, object payload, ProducerIdentity? identity = null) => await Service.DispatchAsync(Contract.Serialize(new { apiVersion = "1", operation, requestId = Guid.NewGuid(), payload }), default, identity);
    public static void Ok(string reply, bool expected = true) => Assert.Equal(expected, System.Text.Json.JsonDocument.Parse(reply).RootElement.GetProperty("ok").GetBoolean());
}
