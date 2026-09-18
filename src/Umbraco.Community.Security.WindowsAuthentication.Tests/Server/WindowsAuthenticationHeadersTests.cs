using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Server;

[TestFixture]
public class WindowsAuthenticationHeadersTests
{
    private const string BackOfficeHeader = WindowsAuthenticationDefaults.HeaderName;
    private const string StatusHeader = WindowsAuthenticationDefaults.StatusHeaderName;

    [TestCase("Bearer abc")]
    [TestCase("bearer abc")]
    [TestCase("BEARER abc")]
    [TestCase("Bearer [redacted]")]
    [TestCase("Bearer eyJhbGciOiJIUzI1NiJ9.e30.sig")]
    public void Restores_bearer_value_when_request_has_no_authorization_header(string value)
    {
        HttpContext context = CreateContext((BackOfficeHeader, value), ("Accept", "application/json"));

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(WindowsAuthenticationHeadersResult.Restored));
            Assert.That(context.Request.Headers.Authorization.ToString(), Is.EqualTo(value));
            Assert.That(context.Request.Headers.ContainsKey(BackOfficeHeader), Is.False);
            Assert.That(context.Request.Headers.Accept.ToString(), Is.EqualTo("application/json"));
            Assert.That(context.Items[WindowsAuthenticationDefaults.AppliedItemKey], Is.EqualTo(true));
        }
    }

    [Test]
    public void Restore_changes_nothing_but_the_two_headers_involved()
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"), ("Cookie", "a=b"), ("Api-Key", "key"), ("X-Custom", "1"));
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeaders.Apply(context);

        List<string> expected = before.Where(h => h.StartsWith(BackOfficeHeader + ":") is false).Append("Authorization: Bearer abc").Order().ToList();
        Assert.That(Snapshot(context), Is.EqualTo(expected));
    }

    [TestCase(401, 403, "401")]
    [TestCase(200, 200, null)]
    [TestCase(204, 204, null)]
    [TestCase(302, 302, null)]
    [TestCase(403, 403, null)]
    [TestCase(404, 404, null)]
    [TestCase(500, 500, null)]
    public async Task Restored_request_sends_only_a_401_as_403_with_the_status_marker(int status, int expectedStatus, string? expectedMarker)
    {
        // IIS adds its Windows challenge to every 401, which would make the browser prompt for Windows credentials.
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"));
        WindowsAuthenticationHeaders.Apply(context);
        context.Response.StatusCode = status;
        context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";

        await ResponseFeature(context).StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(context.Response.Headers.TryGetValue(StatusHeader, out StringValues marker) ? marker.ToString() : null, Is.EqualTo(expectedMarker));
            Assert.That(context.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Bearer error=\"invalid_token\""));
        }
    }

    [Test]
    public async Task A_401_to_a_request_that_was_not_changed_stays_a_401()
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Basic abc"));
        WindowsAuthenticationHeaders.Apply(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;

        await ResponseFeature(context).StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Response.StatusCode, Is.EqualTo(401));
            Assert.That(context.Response.Headers.ContainsKey(StatusHeader), Is.False);
        }
    }

    public static IEnumerable<TestCaseData> RequestsWithoutBackOfficeHeader()
    {
        yield return new TestCaseData((object)Array.Empty<(string, string)>()).SetName("No headers");
        yield return new TestCaseData((object)new[] { ("Authorization", "Bearer front-end-jwt") }).SetName("Bearer token");
        yield return new TestCaseData((object)new[] { ("Authorization", "Basic dXNlcjpwYXNz") }).SetName("Basic credentials");
        yield return new TestCaseData((object)new[] { ("Authorization", "Negotiate abc") }).SetName("Negotiate token");
        yield return new TestCaseData((object)new[] { ("Api-Key", "delivery-api-key"), ("Preview", "true") }).SetName("Delivery API key");
        yield return new TestCaseData((object)new[] { ("Cookie", ".AspNetCore.Identity.Application=abc") }).SetName("Member cookie");
        yield return new TestCaseData((object)new[] { ("X-Umb-Authorization-Other", "Bearer abc") }).SetName("Similarly named header");
    }

    [TestCaseSource(nameof(RequestsWithoutBackOfficeHeader))]
    public void Leaves_requests_without_the_backoffice_header_untouched((string Name, string Value)[] headers)
    {
        HttpContext context = CreateContext(headers);
        context.User = WindowsUser("NTLM");
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.NoBackOfficeHeader);
    }

    [TestCase("")]
    [TestCase("Bearer")]
    [TestCase("Bearer ")]
    [TestCase("Bearer    ")]
    [TestCase("Basic dXNlcjpwYXNz")]
    [TestCase("Negotiate abc")]
    [TestCase("Token abc")]
    [TestCase("abc")]
    public void Leaves_request_untouched_when_backoffice_header_is_not_a_bearer_value(string value)
    {
        HttpContext context = CreateContext((BackOfficeHeader, value));
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.InvalidBackOfficeHeader);
    }

    [Test]
    public void Leaves_request_untouched_when_backoffice_header_holds_a_null_value()
    {
        HttpContext context = CreateContext();
        context.Request.Headers[BackOfficeHeader] = new StringValues([null]);
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.InvalidBackOfficeHeader);
    }

    [Test]
    public void Leaves_request_untouched_when_backoffice_header_has_multiple_values()
    {
        HttpContext context = CreateContext();
        context.Request.Headers[BackOfficeHeader] = new StringValues(["Bearer a", "Bearer b"]);
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.InvalidBackOfficeHeader);
    }

    [TestCase("Bearer other")]
    [TestCase("Basic dXNlcjpwYXNz")]
    [TestCase("ApiKey abc")]
    public void Never_replaces_a_non_windows_authorization_header_even_for_a_windows_authenticated_request(string authorization)
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"), ("Authorization", authorization));
        context.User = WindowsUser("Negotiate");
        SetServerVariable(context, "AUTH_TYPE", "Negotiate");
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.AuthorizationHeaderInUse);
    }

    [TestCase("Negotiate abc")]
    [TestCase("NTLM abc")]
    public void Keeps_a_windows_authorization_header_the_host_has_not_processed(string authorization)
    {
        // e.g. Kestrel with the ASP.NET Core Negotiate handler, which reads the header later in the pipeline.
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"), ("Authorization", authorization));
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.AuthorizationHeaderInUse);
    }

    [Test]
    public void Keeps_a_windows_authorization_header_when_the_request_was_authenticated_by_something_else()
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"), ("Authorization", "Negotiate abc"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "api-client")], "ApiKey"));
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.AuthorizationHeaderInUse);
    }

    [Test]
    public void Keeps_a_windows_authorization_header_when_the_server_variable_is_empty()
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"), ("Authorization", "Negotiate abc"));
        SetServerVariable(context, "AUTH_TYPE", string.Empty);
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.AuthorizationHeaderInUse);
    }

    [Test]
    public void Keeps_multiple_authorization_values()
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"));
        context.Request.Headers.Authorization = new StringValues(["Negotiate a", "NTLM b"]);
        context.User = WindowsUser("NTLM");
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.AuthorizationHeaderInUse);
    }

    [TestCase("Negotiate abc", "Negotiate")]
    [TestCase("Negotiate abc", "Kerberos")]
    [TestCase("NTLM abc", "NTLM")]
    [TestCase("ntlm abc", "ntlm")]
    public void Replaces_a_windows_authorization_header_the_host_has_already_authenticated(string authorization, string authenticationType)
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"), ("Authorization", authorization));
        context.User = WindowsUser(authenticationType);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertRestored(context, result);
    }

    [TestCase("Negotiate")]
    [TestCase("NTLM")]
    public void Replaces_a_windows_authorization_header_when_iis_reports_the_authentication_type(string authType)
    {
        // IIS in-process with AutomaticAuthentication disabled leaves HttpContext.User anonymous but still exposes AUTH_TYPE.
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"), ("Authorization", "Negotiate abc"));
        SetServerVariable(context, "AUTH_TYPE", authType);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertRestored(context, result);
    }

    [Test]
    public void Unauthenticated_windows_identity_does_not_count_as_host_authentication()
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"), ("Authorization", "NTLM abc"));
        context.User = new ClaimsPrincipal(new ClaimsIdentity()); // IsAuthenticated is false without an authentication type
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.AuthorizationHeaderInUse);
    }

    [Test]
    public void Restores_when_authorization_header_is_present_but_empty()
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"));
        context.Request.Headers.Authorization = StringValues.Empty;

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertRestored(context, result);
    }

    [TestCase("/")]
    [TestCase("/api/test-auth/jwt")]
    [TestCase("/media/image.jpg")]
    [TestCase("/umbracox/api")]
    [TestCase("/umbraco-extra/api")]
    [TestCase("/app/umbraco/management/api")]
    public void Leaves_a_request_outside_the_backoffice_path_untouched(string path)
    {
        // Only Umbraco's own endpoints under /umbraco are relayed, so a same-origin path served by anything else keeps its headers.
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"));
        context.Request.Path = path;
        List<string> before = Snapshot(context);

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertUntouched(context, before, result, WindowsAuthenticationHeadersResult.OutsideBackOfficePath);
    }

    [TestCase("/umbraco")]
    [TestCase("/umbraco/serverEventHub")]
    [TestCase("/umbraco/preview")]
    [TestCase("/umbraco/ailoganalyser/api/v1.0/analyse")]
    [TestCase("/UMBRACO/Management/API/v1/user/current")]
    public void Restores_on_any_path_under_the_backoffice_path(string path)
    {
        HttpContext context = CreateContext((BackOfficeHeader, "Bearer abc"));
        context.Request.Path = path;

        WindowsAuthenticationHeadersResult result = WindowsAuthenticationHeaders.Apply(context);

        AssertRestored(context, result);
    }

    private static void AssertRestored(HttpContext context, WindowsAuthenticationHeadersResult result)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(WindowsAuthenticationHeadersResult.Restored));
            Assert.That(context.Request.Headers.Authorization.ToString(), Is.EqualTo("Bearer abc"));
            Assert.That(context.Request.Headers.ContainsKey(BackOfficeHeader), Is.False);
            Assert.That(context.Items.ContainsKey(WindowsAuthenticationDefaults.AppliedItemKey), Is.True);
            Assert.That(ResponseFeature(context).StartingCallbacks, Is.EqualTo(1));
        }
    }

    private static void AssertUntouched(HttpContext context, List<string> before, WindowsAuthenticationHeadersResult result, WindowsAuthenticationHeadersResult expected)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(Snapshot(context), Is.EqualTo(before));
            Assert.That(context.Items, Is.Empty);
            Assert.That(ResponseFeature(context).StartingCallbacks, Is.Zero, "The response must be left alone too");
        }
    }

    internal static HttpContext CreateContext(params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpResponseFeature>(new StartingResponseFeature());

        // Only requests under /umbraco are relayed; tests that need another path set it themselves.
        context.Request.Path = "/umbraco/management/api/v1/test";

        foreach ((string name, string value) in headers)
        {
            // Set rather than Append for the first value: Append drops an empty value, which a real server would keep.
            IHeaderDictionary requestHeaders = context.Request.Headers;
            requestHeaders[name] = requestHeaders.TryGetValue(name, out StringValues existing) ? StringValues.Concat(existing, value) : new StringValues(value);
        }

        return context;
    }

    internal static List<string> Snapshot(HttpContext context)
        => context.Request.Headers.Select(h => $"{h.Key}: {string.Join("|", h.Value.ToArray())}").Order().ToList();

    private static StartingResponseFeature ResponseFeature(HttpContext context)
        => (StartingResponseFeature)context.Features.Get<IHttpResponseFeature>()!;

    private static ClaimsPrincipal WindowsUser(string authenticationType)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"DOMAIN\user")], authenticationType));

    private static void SetServerVariable(HttpContext context, string name, string value)
        => context.Features.Set<IServerVariablesFeature>(new ServerVariablesFeature { [name] = value });

    /// <summary>Records OnStarting callbacks so a test can run them the way a server does before sending the response.</summary>
    internal sealed class StartingResponseFeature : HttpResponseFeature
    {
        private readonly List<(Func<object, Task> Callback, object State)> _callbacks = [];

        public int StartingCallbacks => _callbacks.Count;

        public override void OnStarting(Func<object, Task> callback, object state) => _callbacks.Add((callback, state));

        public async Task StartAsync()
        {
            foreach ((Func<object, Task> callback, object state) in Enumerable.Reverse(_callbacks))
            {
                await callback(state);
            }
        }
    }

    private sealed class ServerVariablesFeature : IServerVariablesFeature
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? this[string variableName]
        {
            get => _values.GetValueOrDefault(variableName);
            set => _values[variableName] = value;
        }
    }
}
