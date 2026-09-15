namespace Umbraco.Community.Security.WindowsAuthentication;

public static class WindowsAuthenticationDefaults
{
    /// <summary>
    /// The header the backoffice client moves its bearer token into. Must match <c>BACKOFFICE_HEADER</c> in the client script.
    /// </summary>
    public const string HeaderName = "X-Umb-Authorization";

    /// <summary>
    /// Added to a response to a relayed request whose real status was 401. That response is sent as 403 instead, because IIS adds
    /// its Windows authentication challenge to every 401 and the browser would then prompt for Windows credentials. Must match
    /// <c>STATUS_HEADER</c> in the client script, which turns the response back into a 401.
    /// </summary>
    public const string StatusHeaderName = "X-Umb-Authorization-Status";

    /// <summary>
    /// Set in <see cref="Microsoft.AspNetCore.Http.HttpContext.Items"/> when the middleware restored the Authorization header.
    /// </summary>
    public const string AppliedItemKey = "Umbraco.Community.Security.WindowsAuthentication.Applied";

    /// <summary>
    /// The Management API endpoint the Umbraco sign-in page posts credentials to. The sign-in page requests this exact path.
    /// </summary>
    public const string BackOfficeLoginPath = "/umbraco/management/api/v1/security/back-office/login";

    public const string PipelineFilterName = "Umbraco.Community.Security.WindowsAuthentication";
}
