using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Server;

/// <summary>
/// Runs a real ASP.NET Core authentication stack with and without the middleware in front of it. Every request the
/// backoffice client script did not produce must get exactly the same response from both, apart from a failed backoffice
/// sign-in that the host authenticated with Windows authentication.
/// </summary>
[TestFixture]
public class AuthenticationSchemeIsolationTests
{
    private const string BackOfficeHeader = WindowsAuthenticationDefaults.HeaderName;
    private const string LoginPath = WindowsAuthenticationDefaults.BackOfficeLoginPath;

    /// <summary>Makes the test server behave as if IIS had authenticated the request with Windows authentication.</summary>
    private const string HostAuthenticationHeader = "X-Test-Host-Authentication";

    private const string WrongPassword = """{"username":"admin@example.com","password":"wrong"}""";
    private const string RightPassword = """{"username":"admin@example.com","password":"right"}""";

    private static readonly SymmetricSecurityKey SigningKey = new(Encoding.UTF8.GetBytes(new string('k', 64)));
    private static readonly string ValidJwt = CreateJwt("front-end-user");
    private static readonly string ValidBasic = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass"));

    private IHost _withMiddleware = null!;
    private IHost _withoutMiddleware = null!;

    [OneTimeSetUp]
    public async Task StartServers()
    {
        _withMiddleware = await StartAsync(withMiddleware: true);
        _withoutMiddleware = await StartAsync(withMiddleware: false);
    }

    [OneTimeTearDown]
    public async Task StopServers()
    {
        await _withMiddleware.StopAsync();
        _withMiddleware.Dispose();
        await _withoutMiddleware.StopAsync();
        _withoutMiddleware.Dispose();
    }

    public static IEnumerable<TestCaseData> RequestsNotFromTheBackOffice()
    {
        RequestSpec[] specs =
        [
            new("Anonymous request", "GET", "/echo", 200, [("X-Custom", "1")]),
            new("POST with a JSON body", "POST", "/echo", 200, [("X-Custom", "1")], Body: """{"a":1}"""),
            new("Bearer token to an anonymous endpoint", "GET", "/echo", 200, [("Authorization", $"Bearer {ValidJwt}")]),
            new("JWT scheme with a valid token", "GET", "/auth/jwt", 200, [("Authorization", $"Bearer {ValidJwt}")]),
            new("JWT scheme with a lowercase scheme name", "GET", "/auth/jwt", 200, [("Authorization", $"bearer {ValidJwt}")]),
            new("JWT scheme without a token", "GET", "/auth/jwt", 401, []),
            new("JWT scheme with an invalid token", "GET", "/auth/jwt", 401, [("Authorization", "Bearer not-a-jwt")]),
            new("Cookie scheme, signed in", "GET", "/auth/cookie", 200, [], SignInFirst: true),
            new("Cookie scheme, not signed in", "GET", "/auth/cookie", 401, []),
            new("API key scheme with a valid key", "GET", "/auth/api-key", 200, [("X-Api-Key", "secret")]),
            new("API key scheme with a wrong key", "GET", "/auth/api-key", 401, [("X-Api-Key", "wrong")]),
            new("Basic scheme with valid credentials", "GET", "/auth/basic", 200, [("Authorization", $"Basic {ValidBasic}")]),
            new("Basic scheme with wrong credentials", "GET", "/auth/basic", 401, [("Authorization", "Basic d3Jvbmc6d3Jvbmc=")]),
            new("Negotiate handled by the application", "GET", "/auth/negotiate", 200, [("Authorization", "Negotiate valid")]),
            new("Negotiate handled by the application, no token", "GET", "/auth/negotiate", 401, []),

            // Requests carrying an X-Umb-Authorization header the middleware must ignore, including the 401 responses they get.
            new("Backoffice header that is not a bearer value", "GET", "/echo", 200, [(BackOfficeHeader, "Basic abc")]),
            new("Backoffice header with only the scheme name", "GET", "/auth/jwt", 401, [(BackOfficeHeader, "Bearer")]),
            new("Two backoffice header values", "GET", "/auth/jwt", 401, [(BackOfficeHeader, $"Bearer {ValidJwt}"), (BackOfficeHeader, $"Bearer {ValidJwt}")]),
            new("Backoffice header next to a JWT the application reads", "GET", "/auth/jwt", 200, [("Authorization", $"Bearer {ValidJwt}"), (BackOfficeHeader, "Bearer other")]),
            new("Backoffice header next to an invalid JWT the application reads", "GET", "/auth/jwt", 401, [("Authorization", "Bearer not-a-jwt"), (BackOfficeHeader, $"Bearer {ValidJwt}")]),
            new("Backoffice header next to Basic credentials the application reads", "GET", "/auth/basic", 200, [("Authorization", $"Basic {ValidBasic}"), (BackOfficeHeader, $"Bearer {ValidJwt}")]),
            new("Backoffice header next to a Negotiate token the application reads", "GET", "/auth/negotiate", 200, [("Authorization", "Negotiate valid"), (BackOfficeHeader, $"Bearer {ValidJwt}")]),
            new("Backoffice header next to an API key", "GET", "/auth/api-key", 200, [("X-Api-Key", "secret"), (BackOfficeHeader, "Basic abc")]),

            // Behind host Windows authentication, only a failed backoffice sign-in may change; front-end 401s must not.
            new("JWT scheme without a token, behind host Windows authentication", "GET", "/auth/jwt", 401, [(HostAuthenticationHeader, "NTLM")]),
            new("Cookie scheme, not signed in, behind host Windows authentication", "GET", "/auth/cookie", 401, [(HostAuthenticationHeader, "Negotiate")]),
            new("POST with a JSON body behind host Windows authentication", "POST", "/echo", 200, [(HostAuthenticationHeader, "NTLM")], Body: """{"a":1}"""),
            new("Failed backoffice sign-in without host Windows authentication", "POST", LoginPath, 401, [], Body: WrongPassword),
            new("Successful backoffice sign-in behind host Windows authentication", "POST", LoginPath, 200, [(HostAuthenticationHeader, "NTLM")], Body: RightPassword),
            new("GET on the sign-in path behind host Windows authentication", "GET", LoginPath, 405, [(HostAuthenticationHeader, "NTLM")]),
        ];

        return specs.Select(spec => new TestCaseData(spec).SetName($"Same result with and without the middleware: {spec.Name}"));
    }

    [TestCaseSource(nameof(RequestsNotFromTheBackOffice))]
    public async Task Request_gets_the_same_response_with_and_without_the_middleware(RequestSpec spec)
    {
        Response withoutMiddleware = await SendAsync(_withoutMiddleware, spec);
        Response withMiddleware = await SendAsync(_withMiddleware, spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutMiddleware.Status, Is.EqualTo(spec.ExpectedStatus), "Baseline status, proves the scheme is really in play");
            Assert.That(withMiddleware, Is.EqualTo(withoutMiddleware));
            Assert.That(withMiddleware.StatusMarker, Is.Null);
        }
    }

    [TestCase("/auth/jwt", 200)]
    [TestCase("/echo", 200)]
    public async Task Relayed_bearer_reaches_the_application_exactly_as_a_direct_bearer(string path, int expectedStatus)
    {
        Response direct = await SendAsync(_withoutMiddleware, new RequestSpec("direct", "GET", path, expectedStatus, [("Authorization", $"Bearer {ValidJwt}"), ("X-Custom", "1")]));
        Response relayed = await SendAsync(_withMiddleware, new RequestSpec("relayed", "GET", path, expectedStatus, [(BackOfficeHeader, $"Bearer {ValidJwt}"), ("X-Custom", "1")]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(direct.Status, Is.EqualTo(expectedStatus));
            Assert.That(relayed with { Body = relayed.Body.Replace("restored=True", "restored=False") }, Is.EqualTo(direct));
        }
    }

    [Test]
    public async Task Relayed_invalid_bearer_is_rejected_exactly_as_a_direct_invalid_bearer_once_the_client_restores_the_401()
    {
        Response direct = await SendAsync(_withoutMiddleware, new RequestSpec("direct", "GET", "/auth/jwt", 401, [("Authorization", "Bearer not-a-jwt")]));
        Response relayed = await SendAsync(_withMiddleware, new RequestSpec("relayed", "GET", "/auth/jwt", 401, [(BackOfficeHeader, "Bearer not-a-jwt")]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(direct.Status, Is.EqualTo(401));
            Assert.That(direct.WwwAuthenticate, Does.Contain("invalid_token"));
            Assert.That(relayed.Status, Is.EqualTo(403), "Sent as 403 so IIS does not add its Windows challenge");
            Assert.That(relayed.StatusMarker, Is.EqualTo("401"));
            Assert.That(relayed with { Status = 401, StatusMarker = null }, Is.EqualTo(direct));
        }
    }

    [TestCase("/auth/cookie")]
    [TestCase("/auth/basic")]
    [TestCase("/auth/api-key")]
    [TestCase("/auth/negotiate")]
    public async Task Any_401_to_a_relayed_request_is_sent_as_403_with_the_status_marker(string path)
    {
        Response direct = await SendAsync(_withoutMiddleware, new RequestSpec("direct", "GET", path, 401, [("Authorization", $"Bearer {ValidJwt}")]));
        Response relayed = await SendAsync(_withMiddleware, new RequestSpec("relayed", "GET", path, 401, [(BackOfficeHeader, $"Bearer {ValidJwt}")]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(direct.Status, Is.EqualTo(401));
            Assert.That(relayed.Status, Is.EqualTo(403));
            Assert.That(relayed.StatusMarker, Is.EqualTo("401"));
            Assert.That(relayed with { Status = 401, StatusMarker = null }, Is.EqualTo(direct));
        }
    }

    [TestCase("NTLM")]
    [TestCase("Negotiate")]
    public async Task Failed_backoffice_sign_in_behind_host_windows_authentication_is_sent_as_400_with_the_status_marker(string authType)
    {
        Response direct = await SendAsync(_withoutMiddleware, new RequestSpec("direct", "POST", LoginPath, 401, [(HostAuthenticationHeader, authType)], Body: WrongPassword));
        Response hidden = await SendAsync(_withMiddleware, new RequestSpec("hidden", "POST", LoginPath, 401, [(HostAuthenticationHeader, authType)], Body: WrongPassword));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(direct.Status, Is.EqualTo(401));
            Assert.That(hidden.Status, Is.EqualTo(400), "Sent as 400 so IIS does not add its Windows challenge; the sign-in page shows the same message");
            Assert.That(hidden.StatusMarker, Is.EqualTo("401"));
            Assert.That(hidden with { Status = 401, StatusMarker = null }, Is.EqualTo(direct), "Everything else, including the body, is unchanged");
        }
    }

    public sealed record RequestSpec(string Name, string Method, string Path, int ExpectedStatus, (string Name, string Value)[] Headers, bool SignInFirst = false, string? Body = null)
    {
        public override string ToString() => Name;
    }

    private sealed record Response(int Status, string WwwAuthenticate, string? StatusMarker, string Body);

    private static async Task<Response> SendAsync(IHost host, RequestSpec spec)
    {
        HttpClient client = host.GetTestClient();
        using var request = new HttpRequestMessage(new HttpMethod(spec.Method), spec.Path);
        if (spec.Body is not null)
        {
            request.Content = new StringContent(spec.Body, Encoding.UTF8, "application/json");
        }

        foreach ((string name, string value) in spec.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (spec.SignInFirst)
        {
            using HttpResponseMessage signIn = await client.PostAsync("/cookie/sign-in", null);
            request.Headers.Add("Cookie", signIn.Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        return new Response(
            (int)response.StatusCode,
            string.Join(" | ", response.Headers.WwwAuthenticate),
            response.Headers.TryGetValues(WindowsAuthenticationDefaults.StatusHeaderName, out IEnumerable<string>? marker) ? string.Join(",", marker) : null,
            await response.Content.ReadAsStringAsync());
    }

    private static async Task<IHost> StartAsync(bool withMiddleware)
    {
        IHost host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddAuthentication()
                        .AddJwtBearer("Jwt", options =>
                        {
                            options.MapInboundClaims = false;
                            options.TokenValidationParameters = new TokenValidationParameters
                            {
                                ValidIssuer = "test",
                                ValidAudience = "test",
                                IssuerSigningKey = SigningKey,
                                NameClaimType = "sub",
                            };
                        })
                        .AddCookie("Cookies", options => options.Events.OnRedirectToLogin = context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        })
                        .AddScheme<AuthenticationSchemeOptions, ApiKeyHandler>("ApiKey", null)
                        .AddScheme<AuthenticationSchemeOptions, BasicHandler>("Basic", null)
                        .AddScheme<AuthenticationSchemeOptions, ApplicationNegotiateHandler>("Negotiate", null);
                })
                .Configure(app =>
                {
                    // Stands in for IIS Windows authentication, which runs before the application and reports AUTH_TYPE.
                    app.Use((context, next) =>
                    {
                        if (context.Request.Headers.TryGetValue(HostAuthenticationHeader, out StringValues authType))
                        {
                            context.Features.Set<IServerVariablesFeature>(new HostServerVariables(authType.ToString()));
                        }

                        return next(context);
                    });

                    if (withMiddleware)
                    {
                        app.UseMiddleware<WindowsAuthenticationMiddleware>();
                    }

                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        foreach ((string path, string scheme) in new[] { ("jwt", "Jwt"), ("cookie", "Cookies"), ("api-key", "ApiKey"), ("basic", "Basic"), ("negotiate", "Negotiate") })
                        {
                            endpoints.MapGet($"/auth/{path}", Describe).RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = scheme });
                        }

                        endpoints.MapPost("/cookie/sign-in", (HttpContext context) =>
                            context.SignInAsync("Cookies", Principal("member", "Cookies")));

                        // Stands in for Umbraco's backoffice sign-in, which answers wrong credentials with a 401 problem details body.
                        endpoints.MapPost(LoginPath, async context =>
                        {
                            using JsonDocument credentials = await JsonDocument.ParseAsync(context.Request.Body);
                            bool valid = credentials.RootElement.GetProperty("password").GetString() == "right";
                            context.Response.StatusCode = valid ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized;
                            context.Response.ContentType = valid ? "application/json" : "application/problem+json";
                            await context.Response.WriteAsync(valid
                                ? """{"signedIn":true}"""
                                : """{"type":"Error","title":"Invalid credentials","status":401,"detail":"The provided credentials are invalid. User has not been signed in."}""");
                        });

                        endpoints.MapMethods("/echo", ["GET", "POST"], async (HttpContext context) =>
                        {
                            string body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                            IEnumerable<string> headers = context.Request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase).Select(h => $"{h.Key}={h.Value}");
                            return $"{Describe(context)}\n{string.Join("\n", headers)}\nbody={body}";
                        });
                    });
                }))
            .Build();

        await host.StartAsync();
        return host;
    }

    private static string Describe(HttpContext context)
        => $"user={context.User.Identity?.AuthenticationType}:{context.User.Identity?.Name} restored={context.Items.ContainsKey(WindowsAuthenticationDefaults.AppliedItemKey)}";

    private static string CreateJwt(string subject)
        => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "test",
            Audience = "test",
            Subject = new ClaimsIdentity([new Claim("sub", subject)]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
        });

    private static ClaimsPrincipal Principal(string name, string authenticationType)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], authenticationType));

    private sealed class HostServerVariables(string authType) : IServerVariablesFeature
    {
        public string? this[string variableName]
        {
            get => string.Equals(variableName, "AUTH_TYPE", StringComparison.OrdinalIgnoreCase) ? authType : null;
            set => throw new NotSupportedException();
        }
    }

    private sealed class ApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.TryGetValue("X-Api-Key", out var key) is false
                ? AuthenticateResult.NoResult()
                : key == "secret" ? Success(Scheme.Name, "api-client") : AuthenticateResult.Fail("Invalid API key"));
    }

    private sealed class BasicHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string authorization = Request.Headers.Authorization.ToString();
            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) is false)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            string credentials = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..]));
            return Task.FromResult(credentials == "user:pass" ? Success(Scheme.Name, "user") : AuthenticateResult.Fail("Invalid credentials"));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Headers.WWWAuthenticate = "Basic realm=\"test\"";
            return base.HandleChallengeAsync(properties);
        }
    }

    /// <summary>Stands in for the ASP.NET Core Negotiate handler, which reads the Authorization header inside the application.</summary>
    private sealed class ApplicationNegotiateHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(Request.Headers.Authorization.ToString() == "Negotiate valid"
                ? Success(Scheme.Name, @"DOMAIN\user")
                : AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Headers.WWWAuthenticate = "Negotiate";
            return base.HandleChallengeAsync(properties);
        }
    }

    private static AuthenticateResult Success(string scheme, string name)
        => AuthenticateResult.Success(new AuthenticationTicket(Principal(name, scheme), scheme));
}
