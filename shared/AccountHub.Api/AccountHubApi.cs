using System.Text;
using System.Text.Json;
using AccountHub.Api.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace AccountHub.Api;

/// <summary>Resolve on every call. Never hold a host delegate across plugin lifecycle changes.</summary>
public sealed class AccountHubApi(IServiceProvider services)
{
    private readonly AccountHubClient client = new(services);
    public async Task<ApiResponse> CallAsync<T>(string operation, T payload, CancellationToken cancellationToken = default)
    {
        var request = JsonSerializer.Serialize(new ApiRequest("1", operation, Guid.NewGuid(),
            JsonSerializer.SerializeToElement(payload, Wire.Options)), Wire.Options);
        if (Encoding.UTF8.GetByteCount(request) > Wire.MaxBatchBytes)
            return ApiResponse.Failure("invalid_request", "Request exceeds the transport limit");

        try
        {
            var result = await client.CallAsync(operation, payload!, cancellationToken).ConfigureAwait(false);
            if (result is null) return ApiResponse.Failure("unavailable", "Account Hub call failed", true);
            var json = result.Value.GetRawText();
            if (Encoding.UTF8.GetByteCount(json) > Wire.MaxBatchBytes)
                return ApiResponse.Failure("unavailable", "Invalid host response", true);
            var response = JsonSerializer.Deserialize<ApiResponse>(json, Wire.Options);
            return response is { ApiVersion: "1" } && (response.Ok ? response.Data.HasValue : response.Error is not null)
                ? response : ApiResponse.Failure("unavailable", "Incompatible host response", true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            // A producer's committed primary action must survive host failure. Never log request data.
            return ApiResponse.Failure("unavailable", "Account Hub call failed", true);
        }
    }
}
