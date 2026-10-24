using System.Text;
using System.Text.Json;
using AccountHub.Api.Contracts;

namespace AccountHub.Core;

public sealed class ProducerDispatcher(HubStore store)
{
    public const string HostVersion = "0.1.0";
    public static readonly string[] Operations = ["hello", "health", "audit.publish"];

    public async Task<string> DispatchAsync(string json, CancellationToken ct)
    {
        ApiResponse response;
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > Wire.MaxBatchBytes)
                throw new HubException("invalid_request", "Request exceeds 256 KB");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            _ = CanonicalJson.Normalize(document.RootElement); // Also rejects duplicate property names.
            var request = document.RootElement.Deserialize<ApiRequest>(Wire.Options);
            if (request is null || request.ApiVersion != "1" || request.RequestId == Guid.Empty
                || request.Payload.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(request.Operation))
                throw new HubException("invalid_request", "Invalid v1 request envelope");
            response = request.Operation switch
            {
                "hello" => ApiResponse.Success(new { hostVersion = HostVersion, apiVersion = "1", operations = Operations }),
                "health" => ApiResponse.Success(new
                {
                    ready = store.Ready, hostVersion = HostVersion, schemaVersion = store.Ready ? 1 : 0,
                    features = new { audit = store.Ready, notifications = false, bell = false }
                }),
                "audit.publish" => await PublishAuditAsync(request.Payload, ct),
                // Frozen contract, deliberately unavailable until P4 provides durable fanout.
                "notifications.publish" or "notifications.publishBatch" =>
                    ApiResponse.Failure("unavailable", "Notification host is not enabled in this foundation build", true),
                _ => ApiResponse.Failure("unsupported_operation", "Operation is not supported")
            };
        }
        catch (HubException ex) { response = ApiResponse.Failure(ex.Code, ex.Message, ex.Retryable); }
        catch (JsonException) { response = ApiResponse.Failure("invalid_request", "Invalid JSON request"); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { response = ApiResponse.Failure("internal", "Account Hub could not process the request", true); }
        return JsonSerializer.Serialize(response, Wire.Options);
    }

    private async Task<ApiResponse> PublishAuditAsync(JsonElement payload, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > Wire.MaxEventBytes)
            throw new HubException("invalid_request", "Audit event exceeds 64 KB");
        var value = payload.Deserialize<AuditEvent>(Wire.Options)
            ?? throw new HubException("invalid_request", "Audit event is required");
        // Tolerate additive fields without retaining them; bind idempotency to the entire input.
        var accepted = await store.AppendProducerAuditAsync(value, CanonicalJson.Hash(CanonicalJson.Normalize(payload)), ct);
        return ApiResponse.Success(new { status = accepted.Status, auditId = accepted.AuditId });
    }
}
