using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Umbraco.Community.Security.WindowsAuthentication;

public enum WindowsAuthenticationHeadersResult
{
    /// <summary>The request has no X-Umb-Authorization header and was not changed.</summary>
    NoBackOfficeHeader,

    /// <summary>
    /// The bearer value was moved from the X-Umb-Authorization header into the <c>Authorization</c> header. If the response is a 401 it is
    /// sent as 403 with <see cref="WindowsAuthenticationDefaults.StatusHeaderName"/>.
    /// </summary>
    Restored,

    /// <summary>The X-Umb-Authorization header is not a single Bearer value, so the request was not changed.</summary>
    InvalidBackOfficeHeader,

    /// <summary>The request carries an <c>Authorization</c> header the application may still need, so the request was not changed.</summary>
    AuthorizationHeaderInUse,
}

/// <summary>
/// Restores the <c>Authorization</c> header that the backoffice client script moved into <see cref="WindowsAuthenticationDefaults.HeaderName"/>,
/// and keeps IIS from turning backoffice 401 responses into Windows credentials prompts.
/// </summary>
/// <remarks>
/// Only the backoffice client script sends the X-Umb-Authorization header, so every other request (front-end pages, members, the Delivery API,
/// custom authentication schemes) is left untouched. A request is only changed when:
/// <list type="bullet">
/// <item>it carries exactly one X-Umb-Authorization header holding a Bearer value, and</item>
/// <item>it has no <c>Authorization</c> header, or has a Negotiate/NTLM one the host has already used to authenticate the request
/// (IIS and HTTP.sys Windows authentication run before the application).</item>
/// </list>
/// When it is changed, the application sees the <c>Authorization</c> header the backoffice would have sent without the package,
/// and a 401 response is sent as 403 with <see cref="WindowsAuthenticationDefaults.StatusHeaderName"/>, which the client script
/// turns back into a 401. Responses to every other request are left alone.
/// <para>
/// The Umbraco sign-in page doesn't load the client script and sends no bearer token, so a failed sign-in is handled separately by
/// <see cref="HideFailedSignInFromHost"/>.
/// </para>
/// </remarks>
public static class WindowsAuthenticationHeaders
{
    private const string BearerPrefix = "Bearer ";

    private static readonly string[] WindowsAuthenticationTypes = ["Negotiate", "NTLM", "Kerberos"];

    public static WindowsAuthenticationHeadersResult Apply(HttpContext context)
    {
        IHeaderDictionary headers = context.Request.Headers;

        if (headers.TryGetValue(WindowsAuthenticationDefaults.HeaderName, out StringValues headerValue) is false)
        {
            return WindowsAuthenticationHeadersResult.NoBackOfficeHeader;
        }

        if (headerValue.Count != 1 || IsBearer(headerValue[0]) is false)
        {
            return WindowsAuthenticationHeadersResult.InvalidBackOfficeHeader;
        }

        if (StringValues.IsNullOrEmpty(headers.Authorization) is false && IsConsumedByHost(context) is false)
        {
            return WindowsAuthenticationHeadersResult.AuthorizationHeaderInUse;
        }

        headers.Authorization = headerValue[0];
        headers.Remove(WindowsAuthenticationDefaults.HeaderName);
        context.Items[WindowsAuthenticationDefaults.AppliedItemKey] = true;
        context.Response.OnStarting(HideUnauthorizedFromHost, context);

        return WindowsAuthenticationHeadersResult.Restored;
    }

    /// <summary>
    /// For a sign-in posted to <see cref="WindowsAuthenticationDefaults.BackOfficeLoginPath"/> that the host has authenticated with Windows
    /// authentication, sends a 401 response as 400 with <see cref="WindowsAuthenticationDefaults.StatusHeaderName"/>.
    /// </summary>
    /// <returns><see langword="true"/> when the response will be checked; every other request is left untouched.</returns>
    /// <remarks>
    /// Umbraco answers a failed sign-in, including one for a locked-out user, with 401. Behind IIS Windows authentication, IIS adds its
    /// Negotiate/NTLM challenge to that 401, so the browser shows a Windows credentials prompt instead of Umbraco's "couldn't log you in"
    /// message. The sign-in page shows the same message for 400 as for 401, and a 400 carries no challenge. A request the host did not
    /// authenticate with Windows authentication (Kestrel, for example) gets no challenge, so its response is left alone.
    /// </remarks>
    public static bool HideFailedSignInFromHost(HttpContext context)
    {
        HttpRequest request = context.Request;
        if (HttpMethods.IsPost(request.Method) is false
            || request.Path.Equals(WindowsAuthenticationDefaults.BackOfficeLoginPath, StringComparison.OrdinalIgnoreCase) is false
            || IsAuthenticatedByHost(context) is false)
        {
            return false;
        }

        context.Response.OnStarting(SendUnauthorizedSignInAsBadRequest, context);
        return true;
    }

    // IIS adds WWW-Authenticate: Negotiate/NTLM to every 401 on its way out. The browser answers that challenge, still gets a 401,
    // and then prompts for Windows credentials, so an expired backoffice session would show a Windows login dialog instead of
    // Umbraco's own re-authentication. A 403 carries no challenge; the client script turns it back into the original 401.
    private static Task HideUnauthorizedFromHost(object state)
    {
        HttpResponse response = ((HttpContext)state).Response;
        if (response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            response.StatusCode = StatusCodes.Status403Forbidden;
            response.Headers[WindowsAuthenticationDefaults.StatusHeaderName] = "401";
        }

        return Task.CompletedTask;
    }

    // The sign-in page has no client script to restore a hidden 401, so the status it is sent as has to mean the same thing to the page:
    // it shows "couldn't log you in" for both 400 and 401, whereas 403 would read as "locked out".
    private static Task SendUnauthorizedSignInAsBadRequest(object state)
    {
        HttpResponse response = ((HttpContext)state).Response;
        if (response.StatusCode == StatusCodes.Status401Unauthorized)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            response.Headers[WindowsAuthenticationDefaults.StatusHeaderName] = "401";
        }

        return Task.CompletedTask;
    }

    // The browser only adds an Authorization header of its own when the server asks for HTTP authentication. Negotiate/NTLM
    // is safe to replace once IIS or HTTP.sys has authenticated the request with it; anything else may still be read by an
    // authentication handler in the application.
    private static bool IsConsumedByHost(HttpContext context)
    {
        StringValues authorization = context.Request.Headers.Authorization;
        return authorization.Count == 1 && IsWindowsScheme(authorization.ToString()) && IsAuthenticatedByHost(context);
    }

    // IIS in-process puts the Windows identity on HttpContext.User (unless AutomaticAuthentication is off) and always reports AUTH_TYPE.
    private static bool IsAuthenticatedByHost(HttpContext context)
        => context.User.Identities.Any(identity =>
               identity.IsAuthenticated && WindowsAuthenticationTypes.Contains(identity.AuthenticationType, StringComparer.OrdinalIgnoreCase))
           || string.IsNullOrEmpty(context.Features.Get<IServerVariablesFeature>()?["AUTH_TYPE"]) is false;

    private static bool IsBearer(string? value)
        => value is not null
           && value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
           && value.AsSpan(BearerPrefix.Length).IsWhiteSpace() is false;

    private static bool IsWindowsScheme(string value)
        => value.StartsWith("Negotiate ", StringComparison.OrdinalIgnoreCase) || value.StartsWith("NTLM ", StringComparison.OrdinalIgnoreCase);
}
