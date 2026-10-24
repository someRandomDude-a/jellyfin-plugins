using System.Security.Cryptography;
using System.Text;
using AccountHub.Core;
using AccountHub.Security;
using AccountHub.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AccountHub;

public sealed class SecurityIngressBudget(IngressLimiter limiter) : ISecurityIngressBudget
{
    public bool TryAcquire(string sourceAddress) => limiter.Allow("security-global", 120) && limiter.Allow("security-ip:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sourceAddress))), 30);
}

public sealed class SecurityReceiptAdapter(NotificationStore store, IngressLimiter limiter) : ISecurityReceiptStore
{
    public Task<ReceiptResult> AcceptAsync(SecurityReceipt receipt, CancellationToken ct)
    {
        var id = new Guid(Convert.FromHexString(receipt.BodyHash).AsSpan(0, 16));
        // The receipt ledger owns independent audit/fanout acknowledgments. No username or IP hash
        // enters generic delivery content; parsed raw prose has already been discarded.
        var value = receipt.Event;
        var audit = new AccountHub.Api.AuditEvent(1, id, "jellyfin-security", value?.Type ?? "security.webhook.quarantined", receipt.CompatibilityReason is null ? "success" : "denied", value?.OccurredAt.ToUniversalTime() ?? receipt.ReceivedAt);
        var alert = receipt.DeliverAlerts && value is not null && (!value.IsNoisy || limiter.Allow("security-noise:" + value.Type, 1))
            ? new AccountHub.Api.NotificationEvent(1, id, "jellyfin-security", value.Type, value.Severity, value.Title, value.Body, [], true, value.OccurredAt.ToUniversalTime()) : null;
        var accepted = store.Receipt(receipt.BodyHash, alert, audit, receipt.CompatibilityReason is not null);
        return Task.FromResult(new ReceiptResult(accepted ? receipt.CompatibilityReason is null ? "accepted" : "quarantined" : "duplicate"));
    }
}

[ApiController, AllowAnonymous, Route("AccountHub/api/v1/security/jellyfin-security"), RequestSizeLimit(65536)]
public sealed class SecurityReceiver(HubStore store, HubHost host, SecurityWebhook receiver) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        byte[]? signing = null, privacy = null, root = null;
        try
        {
            if (!string.Equals(Request.ContentType?.Split(';')[0].Trim(), "application/json", StringComparison.OrdinalIgnoreCase) ||
                Request.Headers["X-2FA-Timestamp"].Count != 1 || Request.Headers["X-2FA-Signature"].Count != 1 ||
                Request.Headers.ContainsKey("Authorization") || Request.Headers.ContainsKey("X-Emby-Token") || Request.Query.Count != 0) throw new SecurityFailure("invalid_request", 400);
            var secret = await store.ReadSecretAsync("security.webhookSecret", ct);
            if (string.IsNullOrEmpty(secret)) throw new SecurityFailure("unauthorized", 401);
            signing = Encoding.UTF8.GetBytes(secret);
            root = Convert.FromBase64String((await store.ReadSecretAsync("internal.commandFingerprintKey", ct))!);
            privacy = HMACSHA256.HashData(root, "AccountHub.Security.IP.v1"u8);
            var settings = await host.ReadSettingsAsync(ct);
            var result = await receiver.ReceiveAsync(Request.Body, Request.Headers["X-2FA-Timestamp"], Request.Headers["X-2FA-Signature"], HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown", signing, privacy, settings.Settings.AlertMode, ct);
            return new JsonResult(new { apiVersion = "1", ok = true, data = result }, SecurityJson.Options) { StatusCode = 202 };
        }
        catch (SecurityFailure ex) { return new JsonResult(new { apiVersion = "1", ok = false, error = new { code = ex.Code, retryable = ex.Retryable } }, SecurityJson.Options) { StatusCode = ex.Status }; }
        catch (HubException) { return StatusCode(503, new { apiVersion = "1", ok = false, error = new { code = "unavailable", retryable = true } }); }
        finally { if (signing is not null) CryptographicOperations.ZeroMemory(signing); if (privacy is not null) CryptographicOperations.ZeroMemory(privacy); if (root is not null) CryptographicOperations.ZeroMemory(root); }
    }
}
