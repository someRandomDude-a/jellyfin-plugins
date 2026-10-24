using Jellyfin.Plugin.UserManager.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.UserManager.Api;

/// <summary>
/// Maps service exceptions to JSON errors without leaking internals.
/// </summary>
public sealed class ApiExceptionFilter : IAsyncExceptionFilter
{
    private readonly ILogger<ApiExceptionFilter> _logger;

    public ApiExceptionFilter(ILogger<ApiExceptionFilter> logger)
    {
        _logger = logger;
    }

    public Task OnExceptionAsync(ExceptionContext context)
    {
        var exception = context.Exception;
        var (status, message) = exception switch
        {
            UserManagerException known => (known.StatusCode, known.Message),
            SeerrException seerr => (502, seerr.Message),
            ArgumentException argument => (400, argument.Message),
            UnauthorizedAccessException => (403, "You do not have permission to perform this action."),
            _ => (500, "An unexpected error occurred.")
        };

        if (status >= 500)
        {
            _logger.LogError(exception, "User Manager API error");
        }
        else
        {
            _logger.LogInformation("User Manager API rejected a request: {Message}", exception.Message);
        }

        context.Result = new ObjectResult(new { error = message }) { StatusCode = status };
        context.ExceptionHandled = true;
        return Task.CompletedTask;
    }
}

[ApiController]
[TypeFilter(typeof(ApiExceptionFilter))]
public abstract class ApiBaseController : ControllerBase
{
    protected Guid? CurrentUserId => JellyfinUserService.GetUserId(User);

    protected Guid RequireUserId() =>
        CurrentUserId ?? throw new UserManagerException(401, "Sign in to continue.");

    protected bool IsAdministrator => User.IsInRole("Administrator");

    protected string ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    protected (string Id, string Name) Actor => AuditService.Actor(User);

    protected async Task<string> GetBaseUrlAsync(PluginSettingsService settings, CancellationToken cancellationToken)
    {
        var current = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(current.PublicBaseUrl))
        {
            return current.PublicBaseUrl.TrimEnd('/');
        }

        return $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
    }
}
