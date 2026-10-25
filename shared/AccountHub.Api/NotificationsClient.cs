using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AccountHub.Api;

/// <summary>Declarative producer contract. Only framework JSON delegates cross plugin load contexts.</summary>
public sealed record NotificationLayout(string Density = "compact", int SummaryLength = 140, bool Expandable = true)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MessageTemplate { get; init; }
    public int ToastSeconds { get; init; }
}
public sealed record NotificationActionDefinition(string Label, string Size = "compact");
public sealed record NotificationTypeDefinition(string Type, string Name, string Trigger, string Recipients,
    NotificationLayout Layout, NotificationActionDefinition[] Actions)
{
    public string[] Commands { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool KeepNavigationActions { get; init; }
}
public sealed record NotificationRegistration(string Source, string Version, NotificationTypeDefinition[] Types)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool RequireAccess { get; init; }
}
public sealed record NotificationCatalogEntry(string Source, long Revision, string Version, NotificationTypeDefinition[] Types)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool RequireAccess { get; init; }
}
public sealed record NotificationLayoutUpdate(string Source, string Type, long Revision, NotificationLayout Layout);

public static class NotificationIntegration
{
    public const string RegistrationPrefix = "Notifications.Types.1.";
    public const string AccessPrefix = "Notifications.Access.1.";
    public const string CommandsPrefix = "Notifications.Commands.1.";
    // Null request projects live per-recipient controls; a JSON request invokes one declared command.
    public static IServiceCollection RegisterNotificationCommands(this IServiceCollection services, string source,
        Func<IServiceProvider, Func<Guid, string, string?, CancellationToken, Task<string>>> factory)
    {
        services.AddKeyedSingleton<Func<Guid, string, string?, CancellationToken, Task<string>>>(CommandsPrefix + source, (provider, _) => factory(provider));
        return services;
    }
    public static IServiceCollection RegisterNotificationAccess(this IServiceCollection services, string source,
        Func<IServiceProvider, Func<Guid, string, CancellationToken, Task<bool>>> factory)
    {
        services.AddKeyedSingleton<Func<Guid, string, CancellationToken, Task<bool>>>(AccessPrefix + source, (provider, _) => factory(provider));
        return services;
    }
    public static IServiceCollection RegisterNotificationTypes(this IServiceCollection services, NotificationRegistration registration)
    {
        var json = Contract.Serialize(registration);
        services.TryAddSingleton<NotificationsClient>();
        services.AddKeyedSingleton<Func<string>>(RegistrationPrefix + registration.Source, (_, _) => () => json);
        services.AddSingleton(Tuple.Create(RegistrationPrefix + registration.Source, (Func<string>)(() => json)));
        return services;
    }
}

public sealed class NotificationsClient(IServiceProvider services)
{
    private readonly AccountHubClient client = new(services);
    public Task<JsonElement?> RegisterAsync(NotificationRegistration registration, CancellationToken ct = default)
    {
        if (services.GetKeyedService<Func<Guid, string, CancellationToken, Task<bool>>>(NotificationIntegration.AccessPrefix + registration.Source) is not null)
            registration = registration with { RequireAccess = true };
        return client.CallAsync("notifications.register", registration, ct);
    }
    public Task<JsonElement?> PublishAsync(NotificationEvent entry, CancellationToken ct = default) => client.CallAsync("notifications.publish", entry, ct);
    public Task<JsonElement?> AlertAdministratorsAsync(string source, string type, string title, string body, string? link = null,
        NotificationAction[]? actions = null, string? dedupeKey = null, CancellationToken ct = default) =>
        PublishAsync(new(1, Guid.NewGuid(), source, type, "warning", title, body, [], true, DateTimeOffset.UtcNow,
            Link: link, DedupeKey: dedupeKey, Actions: actions), ct);
    public Task<bool> ReadyAsync(CancellationToken ct = default) => client.ReadyAsync(ct);
    public async Task<bool> ReadyForRegisteredTypesAsync(CancellationToken ct = default)
    {
        if (services.GetKeyedService<Func<bool>>("Notifications.Owner.1")?.Invoke() != true || !await ReadyAsync(ct)) return false;
        var health = await client.CallAsync("health", new { }, ct);
        return health is { } envelope && envelope.TryGetProperty("data", out var data) && data.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array &&
            new[] { "registered-types", "scoped-access", "commands" }.All(required => features.EnumerateArray().Any(feature => feature.ValueKind == JsonValueKind.String && feature.GetString() == required));
    }
    public async Task PublishRequiredAsync(NotificationEvent entry, CancellationToken ct = default)
    {
        var response = await PublishAsync(entry, ct);
        if (response is not { } json || !json.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            throw new InvalidOperationException("Notification publication was not acknowledged");
    }
}
