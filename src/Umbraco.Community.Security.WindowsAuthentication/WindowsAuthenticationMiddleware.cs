using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Umbraco.Community.Security.WindowsAuthentication;

/// <summary>
/// Runs <see cref="WindowsAuthenticationHeaders"/> before routing and authentication, so OpenIddict and any other bearer consumer
/// see the request the backoffice would have sent without the package. Requests without the X-Umb-Authorization header pass straight through.
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

        return next(context);
    }
}
