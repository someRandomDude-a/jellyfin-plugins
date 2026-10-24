using Jellyfin.Plugin.UserManager.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.UserManager.Api;

[Authorize(Roles = "Administrator")]
[Route("UserManager/api/admin/presets")]
public sealed class PresetsController : ApiBaseController
{
    private readonly PresetService _presets;
    private readonly AuditService _audit;

    public PresetsController(PresetService presets, AuditService audit)
    {
        _presets = presets;
        _audit = audit;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PresetDto>>> GetAll(CancellationToken cancellationToken)
    {
        var presets = await _presets.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return Ok(presets.Select(ApiMapping.ToDto).ToList());
    }

    [HttpGet("permissions")]
    public ActionResult GetPermissionCatalog() => Ok(SeerrPermissions.Catalog);

    [HttpPost]
    public async Task<ActionResult<PresetDto>> Create([FromBody] PresetInput input, CancellationToken cancellationToken)
    {
        var preset = await _presets.CreateAsync(
            input.Name,
            input.Description,
            input.JellyfinPolicy?.GetRawText(),
            input.SeerrPermissions,
            input.IsDefault,
            cancellationToken).ConfigureAwait(false);

        var actor = Actor;
        await _audit.LogAsync(
            AuditService.ActionPresetSaved,
            actor.Id,
            actor.Name,
            null,
            null,
            $"Preset '{preset.Name}' created.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        return Ok(ApiMapping.ToDto(preset));
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PresetDto>> Update(string id, [FromBody] PresetInput input, CancellationToken cancellationToken)
    {
        var preset = await _presets.UpdateAsync(
            id,
            input.Name,
            input.Description,
            input.JellyfinPolicy?.GetRawText(),
            input.SeerrPermissions,
            input.IsDefault,
            cancellationToken).ConfigureAwait(false);

        var actor = Actor;
        await _audit.LogAsync(
            AuditService.ActionPresetSaved,
            actor.Id,
            actor.Name,
            null,
            null,
            $"Preset '{preset.Name}' updated.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        return Ok(ApiMapping.ToDto(preset));
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(string id, CancellationToken cancellationToken)
    {
        await _presets.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        var actor = Actor;
        await _audit.LogAsync(
            AuditService.ActionPresetDeleted,
            actor.Id,
            actor.Name,
            null,
            null,
            $"Preset {id} deleted.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("import/{userId:guid}")]
    public async Task<ActionResult<PresetDto>> Import(
        Guid userId,
        [FromBody] ImportPresetInput input,
        CancellationToken cancellationToken)
    {
        var preset = await _presets.ImportFromUserAsync(userId, input.Name ?? string.Empty, input.Description, cancellationToken)
            .ConfigureAwait(false);

        var actor = Actor;
        await _audit.LogAsync(
            AuditService.ActionPresetSaved,
            actor.Id,
            actor.Name,
            userId.ToString("N"),
            null,
            $"Preset '{preset.Name}' imported from user.",
            ClientIp,
            cancellationToken).ConfigureAwait(false);

        return Ok(ApiMapping.ToDto(preset));
    }
}
