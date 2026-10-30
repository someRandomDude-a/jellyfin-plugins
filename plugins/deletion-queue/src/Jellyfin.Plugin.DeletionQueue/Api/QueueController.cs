using DeletionQueue.Core;
using DeletionQueue.Infrastructure;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.DeletionQueue.Api;

[ApiController]
[Authorize]
[Route("DeletionQueue")]
[TypeFilter(typeof(QueueApiFilter))]
[RequestSizeLimit(32768)]
public sealed class QueueController(QueueRuntime runtime, LiveIdentity identity, TargetResolver targets,
    IUserManager users, IServerApplicationHost host, TimeProvider clock,
    EnhancedConnection enhanced) : ControllerBase
{
    [AllowAnonymous, HttpGet("assets/{name}")]
    public IActionResult Asset(string name) => name switch
    {
        "admin.css" => Resource("admin.css", "text/css"),
        "integrated.js" => Resource("integrated.js", "application/javascript"),
        "timeline.js" => Resource("timeline.js", "application/javascript"),
        "timeline.css" => Resource("timeline.css", "text/css"),
        "integrated.css" => Resource("integrated.css", "text/css"),
        _ => NotFound()
    };

    [HttpGet("proposals")]
    public IActionResult List([FromQuery] int skip = 0, [FromQuery] int take = 50, [FromQuery] string view = "all",[FromQuery]string scope="visible")
    {
        var caller = identity.Caller(User);
        if(scope is not ("visible" or "all-users"))throw new QueueException("InvalidPagination");
        if(scope=="all-users")caller=identity.Caller(User,administrator:true);
        if (skip < 0 || take is < 1 or > 100) throw new QueueException("InvalidPagination");
        var state = runtime.Read();
        if (view is not ("all" or "active" or "history")) throw new QueueException("InvalidPagination");
        foreach (var proposal in state.Proposals) QueueRules.Expire(state, proposal, clock.GetUtcNow());
        return Ok(state.Proposals.Where(p => targets.Accessible(caller.Id, p))
            .Where(p => view == "all" || (view == "history" ? p.Terminal : !p.Terminal))
            .OrderByDescending(p => view=="history"?p.ClosedAt??p.CreatedAt:p.CreatedAt).Skip(skip).Take(take)
            .Select(p => {var projected=QueueRules.Project(state,p,caller.Id,clock.GetUtcNow());return scope=="all-users"?projected with{RequesterLabel=p.RequiresAutomaticRuleValidation&&p.RequesterId is null?"Automation · requester unknown":users.GetUserById(p.RequesterId??p.OwnerId)?.Username??"Requester unavailable"}:projected;}).ToArray());
    }

    [HttpGet("proposals/{id:guid}")]
    public IActionResult Detail(Guid id)
    {
        var caller = identity.Caller(User);
        var state = runtime.Read();
        var p = Visible(state, id, caller.Id);
        return Ok(QueueRules.Project(state, p, caller.Id, clock.GetUtcNow()));
    }

    [HttpGet("admin/approvals")]
    public IActionResult Approvals([FromQuery]int skip=0,[FromQuery]int take=50){
        var caller=identity.Caller(User,administrator:true);if(skip<0||take is <1 or >100)throw new QueueException("InvalidPagination");var state=runtime.Read();var now=clock.GetUtcNow();
        foreach(var p in state.Proposals)QueueRules.Expire(state,p,now);
        return Ok(state.Proposals.Where(p=>!p.Terminal&&p.Claim is null&&state.Policy.RequireAdministratorApproval&&p.Reason=="AdministratorApprovalRequired"&&!QueueRules.AdministratorApprovalCurrent(state,p)&&targets.Accessible(caller.Id,p)).OrderBy(p=>p.ApprovalDeadline).ThenBy(p=>p.CreatedAt).Skip(skip).Take(take)
            .Select(p=>QueueRules.Project(state,p,caller.Id,now) with{CanApprove=true,CanConsent=false,CanUndo=false,AdministratorApprovalSnapshot=QueueRules.AdministratorSnapshot(state,p),RequesterLabel=p.RequiresAutomaticRuleValidation&&p.RequesterId is null?"Automation · requester unknown":users.GetUserById(p.RequesterId??p.OwnerId)?.Username??"Requester unavailable"}).ToArray());
    }

    [HttpPost("targets/preview")]
    public async Task<IActionResult> Preview(TargetSelection selection, CancellationToken ct)
    {
        var caller = identity.Caller(User);
        var target = await targets.ResolveAsync(caller, selection, ct);
        var state = runtime.Read();
        QueueRules.RequireProposalAuthority(state, identity.Caller(User), target, targets.Accessible(caller.Id, target));
        return Ok(new { target.Title, target.Fingerprint, Snapshot = QueueRules.Snapshot(state, target, caller.Id),
            target.Kind, target.Is4K, Selection = selection, target.RequestOnly, ExecutionAvailable = true });
    }

    [HttpGet("proposals/{id:guid}/status")]
    public async Task<IActionResult> RefreshStatus(Guid id, [FromServices] SeerrExecutionGateway gateway)
    {
        var caller = identity.Caller(User);
        var p = Visible(runtime.Read(), id, caller.Id);
        if (!p.Terminal && p.Claim is null) {
            try {
                using var verificationBudget = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
                verificationBudget.CancelAfter(TimeSpan.FromSeconds(45));
                var check = await gateway.PreflightAsync(p, verificationBudget.Token);
                runtime.Write(Guid.NewGuid(), "status-check", "EligibilityEvaluated", state => {
                    var current = Visible(state, id, identity.Caller(User).Id);
                    if (current.Revision != check.ProposalRevision) throw new QueueException("RevisionChanged");
                    var before = (current.Status, current.Reason, current.NotBefore, current.EligibleSince, current.DelayFloorHours);
                    QueueRules.Evaluate(state, current, check.Evidence, clock.GetUtcNow());
                    if (before != (current.Status, current.Reason, current.NotBefore, current.EligibleSince, current.DelayFloorHours)) current.Revision++;
                    return true;
                });
            } catch (QueueException e) when (e.Code == "RevisionChanged") { /* A concurrent domain change wins. */ }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
            catch (Exception error) {
                if (!runtime.Available) throw;
                runtime.Write(Guid.NewGuid(), "status-check", "VerificationUnavailable", state => {
                    var current = Visible(state, id, identity.Caller(User).Id);
                    if (current.Revision == p.Revision && !current.Terminal && current.Claim is null) {
                        QueueRules.VerificationUnavailable(state, current, clock.GetUtcNow(), (error as QueueException)?.Code); current.Revision++;
                    }
                    return true;
                });
            }
        }
        return Detail(id);
    }

    [HttpPost("targets/items/{id:guid}/preview")]
    public async Task<IActionResult> PreviewItem(Guid id, CancellationToken ct)
    {
        var caller = identity.Caller(User);
        var selection = new TargetSelection(id, null, false);
        VerifiedTarget target;
        try { target = await targets.ResolveAsync(caller, selection, ct); }
        catch (QueueException e) when (e.Code is "TargetEditionUnverified" or "SeerrTargetNotManaged")
        { selection = selection with { Is4K = true }; target = await targets.ResolveAsync(caller, selection, ct); }
        QueueRules.RequireProposalAuthority(runtime.Read(), identity.Caller(User), target, targets.Accessible(caller.Id, id));
        var state = runtime.Read();
        return Ok(new { target.Title, target.Fingerprint, Snapshot = QueueRules.Snapshot(state, target, caller.Id),
            target.Kind, target.Is4K, Selection = selection, ExecutionAvailable = true });
    }

    [HttpGet("targets/items/{id:guid}/capabilities")]
    public IActionResult Capabilities(Guid id)
    {
        var caller = identity.Caller(User);
        return Ok(new { CanPropose = targets.Accessible(caller.Id, id) && QueueRules.Authority(runtime.Read(), caller.Id) != ProposalAuthority.Denied });
    }

    [HttpGet("targets/items/{id:guid}/removal")]
    public async Task<IActionResult> ItemRemoval(Guid id, [FromServices] SeerrExecutionGateway? gateway = null)
    {
        var caller = identity.Caller(User);
        var selectedId=id;id=targets.ContextItem(caller.Id,id);
        if (!targets.Accessible(caller.Id, id)) throw new QueueException("TargetUnavailable");
        var state = runtime.Read(); var now = clock.GetUtcNow();
        var matching = state.Proposals.Where(p => p.Target.JellyfinItemId == id && targets.Accessible(caller.Id, p)).ToArray();
        if (gateway is not null) {
            foreach (var p in matching.Where(p => !p.Terminal && p.Claim is null)) await RefreshStatus(p.Id, gateway);
            state = runtime.Read();
            matching = state.Proposals.Where(p => p.Target.JellyfinItemId == id && targets.Accessible(caller.Id, p)).ToArray();
        }
        foreach (var p in matching) QueueRules.Expire(state, p, now);
        var authority = QueueRules.Authority(state, caller.Id);
        var canPropose = authority != ProposalAuthority.Denied;
        string? reason = canPropose ? null : "ProposalNotPermitted";
        if (canPropose && !matching.Any(p => !p.Terminal)) {
            try {
                VerifiedTarget target;
                try { target = await targets.ResolveAsync(caller, new(id, null, false), HttpContext.RequestAborted); }
                catch (QueueException e) when (e.Code is "TargetEditionUnverified" or "SeerrTargetNotManaged") {
                    target = await targets.ResolveAsync(caller, new(id, null, true), HttpContext.RequestAborted);
                }
                canPropose = authority == ProposalAuthority.AnyAccessibleItem || target.OwnRequestVerified;
                if (!canPropose) reason = "NotYourRequest";
            } catch (QueueException e) { canPropose = false; reason = e.Code; }
        }
        return Ok(new {
            CanPropose = canPropose, Reason = reason, TargetItemId=id, WholeSeries=selectedId!=id,
            Proposals = matching.Where(p => !p.Terminal).OrderByDescending(p => p.CreatedAt)
                .Select(p => QueueRules.Project(state, p, caller.Id, now)).ToArray()
        });
    }

    [HttpPost("proposals")]
    public async Task<IActionResult> Propose(ProposeBody body, CancellationToken ct)
    {
        var caller = identity.Caller(User);
        if (body.CommandId == Guid.Empty) throw new QueueException("CommandIdRequired");
        var target = await targets.ResolveAsync(caller, body.Selection, ct);
        if (body.Fingerprint != target.Fingerprint) throw new QueueException("TargetChanged");
        var id = runtime.Write(body.CommandId, caller.Id.ToString(), "ProposalCreatedOrJoined", state =>
        {
            if (state.Seerr.Revision != target.ConfigurationRevision) throw new QueueException("RevisionChanged");
            var proposal = QueueRules.Propose(state, identity.Caller(User), target,
                targets.Accessible(caller.Id, target), body.Snapshot, clock.GetUtcNow());
            return proposal.Id;
        });
        return Detail(id);
    }

    [HttpPost("proposals/{id:guid}/consent")]
    public IActionResult Consent(Guid id, ConsentBody body)
    {
        if (!Enum.IsDefined(body.Answer)) throw new QueueException("InvalidAnswer");
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "ConsentChanged", state =>
        {
            var p = Visible(state, id, caller.Id);
            QueueRules.Consent(state, id, identity.Caller(User), body.Revision, body.Snapshot,
                body.Answer, targets.Accessible(caller.Id, p.Target), clock.GetUtcNow());
            return true;
        });
        return Detail(id);
    }

    [HttpPost("proposals/{id:guid}/undo")]
    public IActionResult Undo(Guid id, RevisionBody body)
    {
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "OwnerUndo", state =>
        { _ = Visible(state, id, caller.Id); _ = identity.Caller(User); QueueRules.Undo(state, id, caller.Id, body.Revision, clock.GetUtcNow()); return true; });
        return NoContent();
    }

    [HttpGet("me/preferences")]
    public IActionResult Preferences()
    {
        var caller = identity.Caller(User);
        return Ok(runtime.Read().Preferences.GetValueOrDefault(caller.Id) ?? new Preferences());
    }

    [HttpPatch("me/preferences")]
    public IActionResult Preferences(PreferencesBody body)
    {
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "PreferencesChanged", state =>
        { _ = identity.Caller(User); QueueRules.UpdatePreferences(state, caller.Id, body.Revision, body.DelayHours, body.Notifications);
            QueueRules.UpdatePersonalTimelines(state, caller.Id, body.ActivityDays ?? state.Preferences[caller.Id].ActivityDays, body.AdministratorApprovalDays ?? state.Preferences[caller.Id].AdministratorApprovalDays,body.RecentHistoryDays); return true; });
        return Preferences();
    }

    [HttpPost("proposals/{id:guid}/subscription"), HttpDelete("proposals/{id:guid}/subscription")]
    public IActionResult Subscribe(Guid id, RevisionBody body)
    {
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "SubscriptionChanged", state =>
        {
            _ = Visible(state, id, caller.Id);
            _ = identity.Caller(User);
            QueueRules.Subscription(state, id, caller.Id, body.Revision, Request.Method == "POST");
            return true;
        });
        return NoContent();
    }

    [HttpGet("me/notices")]
    public IActionResult Notices()
    {
        var caller = identity.Caller(User);
        var state = runtime.Read();
        if (state.NoticeDeliveryIncomplete) Response.Headers["X-DeletionQueue-Notice-Delivery"] = "incomplete";
        var accessible = state.Proposals.Where(p => targets.Accessible(caller.Id, p)).Select(p => p.Id).ToHashSet();
        return Ok(state.Notices.Where(n => n.UserId == caller.Id && n.Delivery == "local" && accessible.Contains(n.ProposalId))
             .OrderBy(n => n.ReadAt.HasValue).ThenByDescending(n => state.Notices.IndexOf(n)).Take(100).Select(n => new { n.Id, n.ProposalId, n.Text, IsRead = n.ReadAt.HasValue }).ToArray());
    }

    [HttpGet("me/integration")]
    public IActionResult IntegrationState([FromServices] HubAvailability hub)
    {
        var caller = identity.Caller(User);
        var state = runtime.Read();
        return Ok(new { ApiVersion = "1", HubAvailable = hub.Ready,
            IsAdministrator=caller.Administrator,RecentHistoryDays=state.Preferences.GetValueOrDefault(caller.Id)?.RecentHistoryDays is >0 and var days?days:state.Policy.RecentHistoryDays,
            DeviceBell = state.Hub.DeviceBell, EnhancedRequestsEnabled = enhanced.RequestsPageEnabled });
    }

    [HttpPost("me/notices/{id:guid}/read")]
    public IActionResult ReadNotice(Guid id, NoticeReadBody body)
    {
        var caller = identity.Caller(User);
        runtime.Write(body.CommandId, caller.Id.ToString(), "NoticeMarkedRead", state =>
        {
            _ = identity.Caller(User);
            var notice = state.Notices.SingleOrDefault(n => n.Id == id && n.UserId == caller.Id) ?? throw new QueueException("NotFound");
            _ = Visible(state, notice.ProposalId, caller.Id);
            NoticeRules.MarkRead(state, id, caller.Id, clock.GetUtcNow());
            return true;
        });
        return NoContent();
    }

    [HttpGet("admin/status")]
    public async Task<IActionResult> AdminStatus(CancellationToken ct)
    {
        var caller = identity.Caller(User, administrator: true);
        if (!runtime.Available) return Ok(new { StorageAvailable = false, ExecutionAvailable = false, Mode = QueueRuntime.ReleaseMode });
        var state = runtime.Read();
        var notificationsReady = await new AccountHub.Api.NotificationsClient(HttpContext.RequestServices).ReadyForRegisteredTypesAsync(ct);
        return Ok(new
        {
            StorageAvailable = true, ExecutionAvailable = true, Mode = QueueRuntime.ReleaseMode, NotificationsReady = notificationsReady,
            state.Policy, state.CoverageSince, state.LastHeartbeat, state.CoverageHealthy, state.ClockHealthy,
             state.CoverageProblem, CollectorHealthy = runtime.CollectorHealthy,CollectorRunning=runtime.CollectorRunning, LocalIntegrityVerified = true, IndependentRollbackProtection = false,
            ContinuousWindowCollected = QueueRules.CoverageComplete(state, clock.GetUtcNow()),
            PerProposalInventoryChecksEnabled = true, SeerrDelegationTrusted = true,
            state.NoticeDeliveryIncomplete,
             Integration = new { Pending = state.Outbox.Count(e => e.Status is "pending" or "delivering"),
                DeadLetters = state.Outbox.Count(e => e.Status == "dead_letter"), LocalFallbacks = state.Outbox.Count(e => e.Status == "local_fallback"),
                 state.HubDeliveryMetadataRolledOff },
             ReevaluationPending=runtime.ReevaluationPending,
             Seerr = new { state.Seerr.BaseUrl, state.Seerr.Revision, KeyConfigured = state.Seerr.ApiKey.Length > 0, JellyfinServerId = host.SystemId },
             Enhanced = new { ConnectionAvailable = enhanced.Discover() is not null },
            Accounts = users.GetUsers().OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase).Take(500).Select(u => new { u.Id, u.Username, EffectiveAuthority = QueueRules.Authority(state, u.Id), Account = identity.Account(u.Id),
                 PreferencesRevision = state.Preferences.GetValueOrDefault(u.Id)?.Revision ?? 0 }).ToArray(),
             Proposals = state.Proposals.TakeLast(200).ToArray(), ActivityEvidence = state.Activity.TakeLast(500).ToArray(),
             ProposalProgress = state.Proposals.TakeLast(200).ToDictionary(p=>p.Id,p=>QueueRules.Project(state,p,caller.Id,clock.GetUtcNow())),
            AdministratorApprovalSnapshots = state.Proposals.TakeLast(200).ToDictionary(p => p.Id, p => QueueRules.AdministratorSnapshot(state, p)),
            MonitoringLimits = new { Accounts = 500, Proposals = 200, Activity = 500 },
            Journal = runtime.History(),
            HistoryAuthentication = "All events since first initialization are authenticated; no imported legacy history."
        });
    }

    [HttpPatch("admin/policy")]
    public IActionResult Policy(PolicyBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        runtime.Write(body.CommandId, caller.Id.ToString(), "AdministratorPolicyChanged", state =>
        { _ = identity.Caller(User, administrator: true); QueueRules.UpdatePolicy(state, body.Policy, body.Revision); return true; });
        return NoContent();
    }

    [HttpPatch("admin/seerr")]
    public IActionResult Integration(IntegrationBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        if (!Uri.TryCreate(body.BaseUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https") ||
            url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0 || body.BaseUrl.Length > 2048 || body.ApiKey?.Length > 512)
            throw new QueueException("InvalidSeerrSettings");
        if (body.ApiKey?.Any(c => c < 33 || c > 126) == true) throw new QueueException("InvalidSeerrSettings");
        runtime.Write(body.CommandId, caller.Id.ToString(), "SeerrConfigurationChanged", state =>
        {
            _ = identity.Caller(User, administrator: true);
            if (body.Revision != state.Seerr.Revision) throw new QueueException("RevisionChanged");
            state.Seerr.BaseUrl = url.AbsoluteUri.TrimEnd('/');
            if (body.ApiKey is not null) state.Seerr.ApiKey = body.ApiKey;
            state.Seerr.JellyfinServerId = host.SystemId;
            state.Seerr.Revision++;
            QueueRules.BreakCoverage(state, "IntegrationChanged", clock.GetUtcNow());
            return true;
        });
        return NoContent();
    }

    [HttpPost("admin/seerr/enhanced")]
    public IActionResult ImportEnhanced(RevisionBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        var connection = enhanced.Discover() ?? throw new QueueException("EnhancedConnectionUnavailable");
        runtime.Write(body.CommandId, caller.Id.ToString(), "EnhancedSeerrConnectionImported", state =>
        {
            _ = identity.Caller(User, administrator: true);
            if (state.Seerr.Revision != body.Revision) throw new QueueException("RevisionChanged");
            state.Seerr.BaseUrl = connection.Url;
            state.Seerr.ApiKey = connection.Key;
            state.Seerr.JellyfinServerId = host.SystemId;
            state.Seerr.Revision++;
            QueueRules.BreakCoverage(state, "IntegrationChanged", clock.GetUtcNow());
            return true;
        });
        return NoContent();
    }

    [HttpPost("admin/seerr/test")]
    public async Task<IActionResult> TestSeerr([FromServices] ISeerrClientFactory clients, CancellationToken ct)
    {
        _ = identity.Caller(User, administrator: true); Response.Headers.CacheControl = "no-store";
        var settings = runtime.Read().Seerr;
        if (settings.BaseUrl.Length == 0 || settings.ApiKey.Length == 0) return Ok(new { working = false, message = "Save a Seerr URL and API key first.", version = (string?)null });
        try {
            using var client = clients.Create(settings);
            var connection = await client.JellyfinSettingsAsync(ct);
            var matches = connection.TryGetProperty("serverId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String && string.Equals(id.GetString(), host.SystemId, StringComparison.OrdinalIgnoreCase);
            string? version = null;
            if (matches) try { var status = await client.StatusAsync(ct); if (status.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String && v.GetString() is { Length: > 0 and <= 80 } text && System.Text.RegularExpressions.Regex.IsMatch(text, @"^v?\d+\.\d+(?:\.\d+)?(?:[-+][A-Za-z0-9.-]+)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)) version = text; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* Version discovery is optional; authenticated identity verification is not. */ }
            _ = identity.Caller(User, administrator: true);
            if (runtime.Read().Seerr.Revision != settings.Revision) throw new QueueException("RevisionChanged");
            return Ok(new { working = matches, version, message = matches ? "Authenticated Seerr connection and Jellyfin server identity verified. No media or requests changed." : "Seerr authenticated, but its Jellyfin server identity does not match this server." });
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (QueueException) { throw; }
        catch { return Ok(new { working = false, version = (string?)null, message = "Connection could not be verified. Check the saved URL/key, Seerr permissions and outbound network access. No changes were made." }); }
    }

    [HttpPost("admin/users/{id:guid}/reset")]
    public IActionResult ResetUser(Guid id, UserResetBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        runtime.Write(body.CommandId, caller.Id.ToString(), "UserPolicyAndPreferencesReset", state =>
        {
            _ = identity.Caller(User, administrator: true);
            if (users.GetUserById(id) is null) throw new QueueException("NotFound");
            QueueRules.ResetUser(state, id, body.Revision, body.PreferencesRevision);
            return true;
        });
        return NoContent();
    }
    [HttpPost("admin/users/reset-permissions")]
    public IActionResult ResetAllUserPermissions(RevisionBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        var count = runtime.Write(body.CommandId, caller.Id.ToString(), "AllUserPermissionsReset", state => {
            _ = identity.Caller(User, administrator: true);
            return QueueRules.ResetAllUserPermissions(state, body.Revision);
        });
        return Ok(new { ResetCount = count });
    }

    [HttpPost("admin/proposals/{id:guid}/reconcile")]
    public async Task<IActionResult> Reconcile(Guid id, RevisionBody body, [FromServices] SeerrExecutionGateway gateway, CancellationToken ct)
    {
        var caller = identity.Caller(User, administrator: true);
        runtime.Write(body.CommandId, caller.Id.ToString(), "ReadOnlyReconciliationRequested", state =>
        {
            _ = identity.Caller(User, administrator: true);
            var p = QueueRules.Find(state, id);
            if (p.Revision != body.Revision || p.Status != ProposalStatus.Uncertain) throw new QueueException("RevisionChanged");
            return true;
        });
        await new ExecutionCoordinator(runtime, gateway, clock).ReconcileAsync(id, ct);
        return NoContent();
    }

    [HttpPost("admin/proposals/{id:guid}/approval")]
    public IActionResult AdministratorDecision(Guid id, AdministratorApprovalBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        runtime.Write(body.CommandId, caller.Id.ToString(), "AdministratorApprovalChanged", state =>
        {
            var p = Visible(state, id, caller.Id);
            QueueRules.AdministratorDecision(state, id, identity.Caller(User, administrator: true), body.Revision,
                body.Snapshot, body.Approved, targets.Accessible(caller.Id, p.Target), clock.GetUtcNow());
            return true;
        });
        return NoContent();
    }

    private Proposal Visible(QueueState state, Guid id, Guid caller)
    {
        var p = QueueRules.Find(state, id);
        if (!targets.Accessible(caller, p)) throw new QueueException("NotFound");
        return p;
    }
    [HttpPost("admin/proposals/{id:guid}/deny")]
    public IActionResult Deny(Guid id, AdministratorApprovalBody body)
    {
        var caller = identity.Caller(User, administrator: true);
        runtime.Write(body.CommandId, caller.Id.ToString(), "AdministratorRequestDenied", state => {
            var p = QueueRules.Find(state, id);
            QueueRules.Deny(state, id, identity.Caller(User, administrator: true), body.Revision, body.Snapshot,
                targets.Accessible(caller.Id, p.Target) || p.Target.RequestId > 0, clock.GetUtcNow());
            return true;
        });
        return NoContent();
    }

    private IActionResult Resource(string name, string contentType)
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(typeof(Plugin).Namespace + ".Web." + name);
        return stream is null ? NotFound() : File(stream, contentType);
    }
}
