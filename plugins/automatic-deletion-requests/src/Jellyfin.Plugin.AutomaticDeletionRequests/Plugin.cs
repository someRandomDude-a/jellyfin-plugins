using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AutomaticDeletionRequests;

public sealed class Plugin : BasePlugin, IHasWebPages
{
    public Plugin() {
        var assembly = GetType().Assembly;
        SetAttributes(assembly.Location, Path.GetDirectoryName(assembly.Location)!, assembly.GetName().Version!);
    }
    public override Guid Id => new("a6429fb2-c67c-459d-b6a7-579c4a05dace");
    public override string Name => "Automatic Deletion Requests";
    public override string Description => "Administrator rules that submit requests to Deletion Queue; never a deletion executor.";
    public IEnumerable<PluginPageInfo> GetPages() {
        yield return new() { Name = "AutomaticDeletionRequests", DisplayName = Name, EnableInMainMenu = true, MenuIcon = "rule", EmbeddedResourcePath = GetType().Namespace + ".Web.admin.html" };
        yield return new() { Name = "AutomaticDeletionRequests.js", EmbeddedResourcePath = GetType().Namespace + ".Web.admin.js" };
        yield return new() { Name = "AutomaticDeletionRequestsCircuit.js", EmbeddedResourcePath = GetType().Namespace + ".Web.circuit.js" };
        yield return new() { Name = "AutomaticDeletionRequestsRouting.js", EmbeddedResourcePath = GetType().Namespace + ".Web.routing.js" };
        yield return new() { Name = "AutomaticDeletionRequestsAdminUI.js", EmbeddedResourcePath = "Suite.Web.admin-ui.js" };
    }
}
public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host) {
        services.AddSingleton<RuleStore>(); services.AddSingleton<AccountHub.Api.DeletionRequestsClient>();
        services.AddSingleton<RuleRunner>();
        services.AddKeyedSingleton<Func<string, CancellationToken, Task<string>>>(AccountHub.Api.AutomaticRuleValidationClient.ServiceKey,
            (sp, _) => sp.GetRequiredService<RuleRunner>().ValidateAsync);
    }
}
