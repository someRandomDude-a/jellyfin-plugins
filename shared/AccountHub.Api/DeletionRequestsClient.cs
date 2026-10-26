using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AccountHub.Api;

/// <summary>Rules submit requests; the owning queue alone resolves targets and executes deletion.</summary>
public sealed record AutomaticRemovalSubmission(Guid CommandId, Guid ActorId, Guid ItemId, string RuleId)
{
    public bool PreviewOnly { get; init; }
}
public sealed class DeletionRequestsClient(IServiceProvider services)
{
    public const string CollectionsServiceKey = "DeletionQueue.Collections.1";
    public bool CollectionMaintenanceAvailable => services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(CollectionsServiceKey) is not null;
    public async Task MaintainCollectionsAsync(CancellationToken ct) {
        var handler = services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(CollectionsServiceKey)
            ?? throw new InvalidOperationException("Deletion Queue collection maintenance is unavailable");
        _ = await handler("{}", ct);
    }
    public const string ServiceKey = "DeletionQueue.Automation.1";
    public bool Available => services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(ServiceKey) is not null;
    public async Task<JsonElement> SubmitAsync(AutomaticRemovalSubmission submission, CancellationToken ct)
    {
        var handler = services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(ServiceKey)
            ?? throw new InvalidOperationException("Deletion Queue is unavailable");
        using var document = JsonDocument.Parse(await handler(Contract.Serialize(submission), ct));
        return document.RootElement.Clone();
    }
}

public sealed record AutomaticRuleCheck(Guid ActorId, Guid ItemId, string RuleId);
/// <summary>Live fail-closed rule verification; absence never authorizes queue progression.</summary>
public sealed class AutomaticRuleValidationClient(IServiceProvider services)
{
    public const string ServiceKey = "AutomaticDeletionRequests.Validate.1";
    public async Task<bool> MatchesAsync(AutomaticRuleCheck request, CancellationToken ct) {
        var handler = services.GetKeyedService<Func<string, CancellationToken, Task<string>>>(ServiceKey);
        if (handler is null) return false;
        try {
            using var response = JsonDocument.Parse(await handler(Contract.Serialize(request), ct));
            return response.RootElement.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.True;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }
}
