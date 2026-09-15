using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Server;

/// <summary>
/// Umbraco answers a failed backoffice sign-in with 401, and IIS adds its Windows challenge to every 401, so the browser would prompt for
/// Windows credentials instead of showing Umbraco's message. Only that response, and only when the host did Windows authentication, changes.
/// </summary>
[TestFixture]
public class WindowsAuthenticationSignInTests
{
    private const string LoginPath = WindowsAuthenticationDefaults.BackOfficeLoginPath;
    private const string StatusHeader = WindowsAuthenticationDefaults.StatusHeaderName;

    [TestCase(401, 400, "401")]
    [TestCase(200, 200, null)]
    [TestCase(400, 400, null)]
    [TestCase(402, 402, null)]
    [TestCase(403, 403, null)]
    [TestCase(500, 500, null)]
    public async Task Sign_in_behind_windows_authentication_sends_only_a_401_as_400_with_the_status_marker(int status, int expectedStatus, string? expectedMarker)
    {
        HttpContext context = SignInContext("POST", LoginPath);
        SetAuthType(context, "NTLM");
        List<string> before = WindowsAuthenticationHeadersTests.Snapshot(context);

        bool hidden = WindowsAuthenticationHeaders.HideFailedSignInFromHost(context);
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await ResponseFeature(context).StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hidden, Is.True);
            Assert.That(context.Response.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(context.Response.Headers.TryGetValue(StatusHeader, out StringValues marker) ? marker.ToString() : null, Is.EqualTo(expectedMarker));
            Assert.That(context.Response.ContentType, Is.EqualTo("application/problem+json"));
            Assert.That(WindowsAuthenticationHeadersTests.Snapshot(context), Is.EqualTo(before), "The request itself is not changed");
            Assert.That(context.Items, Is.Empty);
        }
    }

    [TestCase("Negotiate")]
    [TestCase("NTLM")]
    [TestCase("kerberos")]
    public async Task Sign_in_authenticated_by_a_windows_identity_is_hidden_too(string authenticationType)
    {
        // IIS in-process with AutomaticAuthentication on puts the Windows identity on HttpContext.User.
        HttpContext context = SignInContext("POST", LoginPath);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"DOMAIN\user")], authenticationType));

        bool hidden = WindowsAuthenticationHeaders.HideFailedSignInFromHost(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ResponseFeature(context).StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hidden, Is.True);
            Assert.That(context.Response.StatusCode, Is.EqualTo(400));
        }
    }

    [TestCase("POST")]
    [TestCase("post")]
    public void Matches_the_sign_in_request_case_insensitively(string method)
    {
        HttpContext context = SignInContext(method, LoginPath.ToUpperInvariant());
        SetAuthType(context, "Negotiate");

        bool hidden = WindowsAuthenticationHeaders.HideFailedSignInFromHost(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hidden, Is.True);
            Assert.That(ResponseFeature(context).StartingCallbacks, Is.EqualTo(1));
        }
    }

    [TestCase("anonymous")]
    [TestCase("unauthenticated identity")]
    [TestCase("non-windows identity")]
    [TestCase("empty AUTH_TYPE")]
    public async Task Leaves_a_sign_in_alone_when_the_host_did_not_authenticate_it(string host)
    {
        // e.g. Kestrel: nothing adds a Windows challenge, so Umbraco's 401 is exactly what the sign-in page should get.
        HttpContext context = SignInContext("POST", LoginPath);
        switch (host)
        {
            case "unauthenticated identity":
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
                break;
            case "non-windows identity":
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "member")], "Identity.Application"));
                break;
            case "empty AUTH_TYPE":
                SetAuthType(context, string.Empty);
                break;
        }

        await AssertLeftAloneAsync(context);
    }

    [TestCase("GET", LoginPath)]
    [TestCase("PUT", LoginPath)]
    [TestCase("POST", LoginPath + "/")]
    [TestCase("POST", LoginPath + "-extra")]
    [TestCase("POST", "/umbraco/management/api/v1/security/back-office/token")]
    [TestCase("POST", "/umbraco/management/api/v1/security/back-office/verify-2fa")]
    [TestCase("POST", "/umbraco/management/api/v1/user/current")]
    [TestCase("POST", "/umbraco/login")]
    [TestCase("POST", "/member-login/")]
    [TestCase("POST", "/api/test-auth/jwt")]
    [TestCase("GET", "/umbraco/delivery/api/v2/content")]
    public async Task Leaves_every_other_request_alone_even_behind_windows_authentication(string method, string path)
    {
        HttpContext context = SignInContext(method, path);
        SetAuthType(context, "NTLM");

        await AssertLeftAloneAsync(context);
    }

    internal static void SetAuthType(HttpContext context, string authType)
        => context.Features.Set<IServerVariablesFeature>(new AuthTypeServerVariables(authType));

    private static async Task AssertLeftAloneAsync(HttpContext context)
    {
        List<string> before = WindowsAuthenticationHeadersTests.Snapshot(context);

        bool hidden = WindowsAuthenticationHeaders.HideFailedSignInFromHost(context);
        int callbacks = ResponseFeature(context).StartingCallbacks;
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ResponseFeature(context).StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hidden, Is.False);
            Assert.That(callbacks, Is.Zero, "The response must not be touched");
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(context.Response.Headers.ContainsKey(StatusHeader), Is.False);
            Assert.That(WindowsAuthenticationHeadersTests.Snapshot(context), Is.EqualTo(before));
            Assert.That(context.Items, Is.Empty);
        }
    }

    private static HttpContext SignInContext(string method, string path)
    {
        HttpContext context = WindowsAuthenticationHeadersTests.CreateContext(("Content-Type", "application/json"));
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    private static WindowsAuthenticationHeadersTests.StartingResponseFeature ResponseFeature(HttpContext context)
        => (WindowsAuthenticationHeadersTests.StartingResponseFeature)context.Features.Get<IHttpResponseFeature>()!;

    private sealed class AuthTypeServerVariables(string authType) : IServerVariablesFeature
    {
        public string? this[string variableName]
        {
            get => string.Equals(variableName, "AUTH_TYPE", StringComparison.OrdinalIgnoreCase) ? authType : null;
            set => throw new NotSupportedException();
        }
    }
}
