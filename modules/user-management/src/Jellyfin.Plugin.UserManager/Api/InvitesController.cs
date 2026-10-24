using Jellyfin.Plugin.UserManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserManager.Api;

[Authorize(Roles = "Administrator")]
[Route("UserManager/api/admin/invites")]
public sealed class InvitesController : ApiBaseController
{
    private readonly InviteService _invites;
    private readonly PluginSettingsService _settings;

    public InvitesController(InviteService invites, PluginSettingsService settings)
    {
        _invites = invites;
        _settings = settings;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<InviteDto>>> GetAll(CancellationToken cancellationToken)
    {
        var invites = await _invites.ListAsync(cancellationToken).ConfigureAwait(false);
        return Ok(invites.Select(ApiMapping.ToDto).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<InviteCreatedDto>> Create([FromBody] CreateInviteInput input, CancellationToken cancellationToken)
    {
        var actor = Actor;
        var baseUrl = await GetBaseUrlAsync(_settings, cancellationToken).ConfigureAwait(false);
        var (invite, url) = await _invites.CreateAsync(
            actor.Id,
            actor.Name,
            input.PresetId,
            input.MaxUses,
            input.ExpiresInDays,
            input.Note,
            baseUrl,
            cancellationToken).ConfigureAwait(false);

        return Ok(new InviteCreatedDto { Invite = ApiMapping.ToDto(invite), Url = url });
    }

    [HttpPost("{id}/revoke")]
    public async Task<ActionResult> Revoke(string id, CancellationToken cancellationToken)
    {
        var actor = Actor;
        await _invites.RevokeAsync(id, actor.Id, actor.Name, ClientIp, cancellationToken).ConfigureAwait(false);
        return Ok(new { message = "Invite revoked." });
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        var actor = Actor;
        await _invites.DeleteAsync(id, actor.Id, actor.Name, ClientIp, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
