using AccountHub.Api;
using Xunit;

namespace AccountHub.Notifications.Tests;

public sealed class RegistryTests
{
    [Fact] public async Task FailedCommandProvidersLeaveNotificationsReadableAndActionsFailClosed()
    {
        var f = new Fixture(); var registration = Registration();
        f.Service.Register(registration with { Types = [registration.Types[0] with { Commands = ["allow"] }] });
        f.Host.Commands = (_, _, _) => throw new IOException("private provider failure");
        var entry = f.Event() with { Actions = [new("Review request", "#/home")] };
        var shown = await f.Service.PresentAsync(f.Host.Alice, entry, f.Service.Catalog(), default);
        Assert.Empty(shown.Commands!); Assert.Single(shown.Actions!); Assert.Equal(entry.Body, shown.Body);
        var error = await Assert.ThrowsAsync<NotificationException>(() => f.Service.InvokeCommandAsync(f.Host.Alice, entry, new("allow", Guid.NewGuid(), 1, "snapshot"), default));
        Assert.Equal(409, error.Status);
    }
    [Fact] public async Task AdministratorTemplatesAndToastDurationProjectWithoutRewritingEvents()
    {
        var f = new Fixture(); f.Service.Register(Registration());
        var entry = f.Event() with { Metadata = new() { ["days"] = "3" } }; await f.Publish(entry);
        f.Service.SaveLayout(new("deletion-queue", "proposal.updated", 1, new() { MessageTemplate = "Played {media}: delayed {days} days.", ToastSeconds = 5 }));
        var shown = await f.Service.PresentAsync(f.Host.Alice, entry, f.Service.Catalog(), default);
        Assert.Equal($"Played {entry.Title}: delayed 3 days.", shown.Body); Assert.Equal(5, shown.Layout!.ToastSeconds);
        Assert.Equal(entry.Body, f.Store.Inbox(f.Host.Alice, 0, 25, null, false).Single().Event.Body);
        Assert.Throws<NotificationException>(() => f.Service.SaveLayout(new("deletion-queue", "proposal.updated", 2, new() { ToastSeconds = 11 })));
    }
    [Fact] public async Task RequiredPermissionProvidersFailRetryablyWithoutAcknowledgingLostFanout()
    {
        var f = new Fixture(); f.Service.Register(Registration() with { RequireAccess = true });
        f.Service.Register(Registration()); Assert.True(f.Service.Catalog().Single().RequireAccess);
        var entry = f.Event(); f.Host.MissingAccessProviders.Add(entry.Source);
        var failed = await f.Dispatch("notifications.publish", entry); Fixture.Ok(failed, false); Assert.Contains("\"retryable\":true", failed);
        Assert.Empty(f.Store.Inbox(f.Host.Alice, 0, 25, null, false));
        f.Host.MissingAccessProviders.Clear(); await f.Publish(entry);
        Assert.Equal("required", f.Store.Inbox(f.Host.Alice, 0, 25, null, false).Single().Event.Metadata!["notificationAccess"]);
        f.Host.MissingAccessProviders.Add(entry.Source);
        Assert.Equal("duplicate", (await f.Publish(entry)).Status);
    }
    [Fact] public async Task AdditionalPluginsCanRegisterWithoutAHardCodedSourceAndCannotEmitUndeclaredTypes()
    {
        var f = new Fixture();
        f.Host.Registrations.Add(Registration() with { Source = "example-plugin" });
        Fixture.Ok(await f.Dispatch("notifications.publish", f.Event() with { Source = "example-plugin" }));
        Assert.Equal("example-plugin", f.Store.Inbox(f.Host.Alice, 0, 25, null, false).Single().Event.Source);
        Fixture.Ok(await f.Dispatch("notifications.publish", f.Event() with { Source = "example-plugin", Type = "unregistered.event" }), false);
    }
    private static NotificationRegistration Registration(string recipients = "explicit-users") => new("deletion-queue", "1", [
        new("proposal.updated", "Removal requests", "Request status changes", recipients, new("compact", 100), [new("Review request")])
    ]);
    [Fact] public async Task RegistrationControlsRecipientsLayoutAndAllowedActions()
    {
        var f = new Fixture(); f.Service.Register(Registration());
        var entry = f.Event() with { Actions = [new("Review request", "#/home", "normal")] };
        await f.Publish(entry);
        var accepted = f.Store.Inbox(f.Host.Alice, 0, 25, null, false).Single().Event;
        Assert.Equal(100, accepted.Layout!.SummaryLength); Assert.Equal("compact", accepted.Actions!.Single().Size);
        Fixture.Ok(await f.Dispatch("notifications.publish", f.Event() with { Actions = [new("Undeclared", "#/home")] }), false);
        Fixture.Ok(await f.Dispatch("notifications.publish", f.Event(admin: true)), false);
    }
    [Fact] public async Task AdministratorDefinitionsResolveOnlyCurrentAdministrators()
    {
        var f = new Fixture(); f.Service.Register(Registration("administrators"));
        Fixture.Ok(await f.Dispatch("notifications.publish", f.Event()), false);
        await f.Publish(f.Event(admin: true));
        Assert.Empty(f.Store.Inbox(f.Host.Alice, 0, 25, null, false));
        Assert.Single(f.Store.Inbox(f.Host.Bob, 0, 25, null, false));
    }
    [Fact] public void LayoutOverridesSurviveReregistrationAndRestartAndRejectStaleRevisions()
    {
        var f = new Fixture(); var registration = Registration(); f.Service.Register(registration);
        var catalog = f.Service.Catalog().Single(); Assert.Equal(1, catalog.Revision);
        f.Service.SaveLayout(new("deletion-queue", "proposal.updated", catalog.Revision, new("standard", 220, false)));
        var stale = Assert.Throws<NotificationException>(() => f.Service.SaveLayout(new("deletion-queue", "proposal.updated", catalog.Revision, new())));
        Assert.Equal("revision_changed", stale.Code);
        f.Service.Register(registration);
        var reopened = new NotificationStore(f.Options, f.Protection, f.Clock);
        var current = new NotificationService(reopened, f.Host, f.Options).Catalog().Single();
        Assert.Equal(2, current.Revision); Assert.Equal(new("standard", 220, false), current.Types.Single().Layout);
        f.Service.Register(registration with { Version = "2", Types = [registration.Types.Single() with { Name = "Updated description" }] });
        Assert.Equal(220, f.Service.Catalog().Single().Types.Single().Layout.SummaryLength);
    }
    [Fact] public async Task HttpProducerCredentialsCannotRegisterOrRedefineTypes()
    {
        var f = new Fixture();
        Fixture.Ok(await f.Dispatch("notifications.register", Registration(), new("deletion-queue", ["notifications:write", "notifications:admin"])), false);
        Assert.Empty(f.Service.Catalog());
        Fixture.Ok(await f.Dispatch("notifications.register", Registration()));
        Assert.Single(f.Service.Catalog());
    }
    [Fact] public void RemovedDefinitionsDoNotLeaveOrphanLayoutOverrides()
    {
        var f = new Fixture(); var registration = Registration(); f.Service.Register(registration);
        f.Service.SaveLayout(new("deletion-queue", "proposal.updated", 1, new("standard", 220)));
        f.Service.Register(registration with { Types = [registration.Types.Single() with { Type = "proposal.created" }] });
        f.Service.Register(registration);
        Assert.Equal(100, f.Service.Catalog().Single().Types.Single().Layout.SummaryLength);
    }
}
