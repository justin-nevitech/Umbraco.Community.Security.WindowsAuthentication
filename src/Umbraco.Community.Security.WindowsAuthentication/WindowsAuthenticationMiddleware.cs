using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Umbraco.Community.Security.WindowsAuthentication;

/// <summary>
/// Runs <see cref="WindowsAuthenticationHeaders"/> before routing and authentication, so OpenIddict and any other bearer consumer
/// see the request the backoffice would have sent without the package, and a failed backoffice sign-in behind Windows authentication
/// shows Umbraco's message instead of a Windows credentials prompt. Every other request passes straight through.
/// </summary>
public sealed class WindowsAuthenticationMiddleware(RequestDelegate next, ILogger<WindowsAuthenticationMiddleware> logger)
{
    public Task InvokeAsync(HttpContext context)
    {
        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        if (result is not WindowsAuthenticationHeadersResult.NoBackOfficeHeader)
        {
            logger.LogDebug("{HeaderName} on {Path}: {Result}", WindowsAuthenticationDefaults.HeaderName, context.Request.Path, result);
        }

        if (WindowsAuthenticationHeaders.HideFailedSignInFromHost(context))
        {
            logger.LogDebug("Backoffice sign-in on {Path}: a 401 response will be sent as 400 so IIS does not add its Windows challenge", context.Request.Path);
        }

        return next(context);
    }
}
