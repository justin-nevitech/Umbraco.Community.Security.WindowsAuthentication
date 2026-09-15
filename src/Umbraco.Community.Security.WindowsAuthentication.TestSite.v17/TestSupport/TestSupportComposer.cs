using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Notifications;

namespace Umbraco.Community.Security.WindowsAuthentication.TestSite.TestSupport;

/// <summary>
/// Adds the pieces the Playwright tests use to prove the package leaves front-end and custom authentication alone: seeded
/// member-protected content, and JWT, API key and Basic authentication schemes that have nothing to do with the backoffice.
/// </summary>
public sealed class TestSupportComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        IConfigurationSection section = builder.Config.GetSection(TestSiteOptions.SectionName);
        builder.Services.Configure<TestSiteOptions>(section);
        TestSiteOptions options = section.Get<TestSiteOptions>() ?? new TestSiteOptions();

        if (options.SeedTestData)
        {
            builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, TestDataSeeder>();
        }

        if (options.EnableTestAuthentication)
        {
            builder.Services.AddAuthentication()
                .AddJwtBearer(TestAuthenticationSchemes.Jwt, jwt =>
                {
                    jwt.MapInboundClaims = false;
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidIssuer = TestAuthenticationSchemes.JwtIssuer,
                        ValidAudience = TestAuthenticationSchemes.JwtIssuer,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtSigningKey)),
                        NameClaimType = "sub",
                    };
                })
                .AddScheme<AuthenticationSchemeOptions, TestApiKeyHandler>(TestAuthenticationSchemes.ApiKey, null)
                .AddScheme<AuthenticationSchemeOptions, TestBasicHandler>(TestAuthenticationSchemes.Basic, null);
        }
    }
}

public sealed class TestApiKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptions<TestSiteOptions> testSite)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.TryGetValue("X-Api-Key", out var key) is false)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        return Task.FromResult(key == testSite.Value.ApiKey
            ? AuthenticateResult.Success(TestTicket.Create(Scheme.Name, "api-client"))
            : AuthenticateResult.Fail("Invalid API key"));
    }
}

public sealed class TestBasicHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, IOptions<TestSiteOptions> testSite)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) is false)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string credentials;
        try
        {
            credentials = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..]));
        }
        catch (FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed credentials"));
        }

        return Task.FromResult(credentials == $"{testSite.Value.BasicUsername}:{testSite.Value.BasicPassword}"
            ? AuthenticateResult.Success(TestTicket.Create(Scheme.Name, testSite.Value.BasicUsername))
            : AuthenticateResult.Fail("Invalid credentials"));
    }
}

internal static class TestTicket
{
    public static AuthenticationTicket Create(string scheme, string name)
        => new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], scheme)), scheme);
}
