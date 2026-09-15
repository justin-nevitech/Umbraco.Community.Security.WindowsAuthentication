using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.IIS;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Umbraco.Community.Security.WindowsAuthentication.TestSite.TestSupport;

/// <summary>
/// Front-end endpoints protected by authentication that has nothing to do with the backoffice. The Playwright tests call
/// these with and without the package in the picture and expect identical behaviour.
/// </summary>
[ApiController]
[Route("api/test-auth")]
public class TestAuthController(IOptions<TestSiteOptions> options, IAuthenticationSchemeProvider schemeProvider) : ControllerBase
{
    [HttpGet("jwt")]
    [Authorize(AuthenticationSchemes = TestAuthenticationSchemes.Jwt)]
    public Task<IActionResult> Jwt() => DescribeAsync();

    [HttpGet("api-key")]
    [Authorize(AuthenticationSchemes = TestAuthenticationSchemes.ApiKey)]
    public Task<IActionResult> ApiKey() => DescribeAsync();

    [HttpGet("basic")]
    [Authorize(AuthenticationSchemes = TestAuthenticationSchemes.Basic)]
    public Task<IActionResult> Basic() => DescribeAsync();

    /// <summary>Authenticates with the member cookie explicitly, so an anonymous caller gets 401 rather than a login redirect.</summary>
    [HttpGet("member")]
    [AllowAnonymous]
    public async Task<IActionResult> Member()
    {
        AuthenticateResult member = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        if (member is not { Succeeded: true, Principal: not null })
        {
            return Unauthorized();
        }

        HttpContext.User = member.Principal;
        return await DescribeAsync();
    }

    [HttpGet("anonymous")]
    [AllowAnonymous]
    public Task<IActionResult> Anonymous() => DescribeAsync();

    [HttpPost("jwt-token")]
    [AllowAnonymous]
    public IActionResult JwtToken([FromQuery] string subject)
        => Ok(new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestAuthenticationSchemes.JwtIssuer,
            Audience = TestAuthenticationSchemes.JwtIssuer,
            Subject = new ClaimsIdentity([new Claim("sub", subject)]),
            Expires = DateTime.UtcNow.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Value.JwtSigningKey)), SecurityAlgorithms.HmacSha256),
        }));

    private async Task<IActionResult> DescribeAsync()
    {
        string? windowsUser = null;
        if (await schemeProvider.GetSchemeAsync(IISServerDefaults.AuthenticationScheme) is not null)
        {
            windowsUser = (await HttpContext.AuthenticateAsync(IISServerDefaults.AuthenticationScheme)).Principal?.Identity?.Name;
        }

        return Ok(new
        {
            scheme = User.Identity?.AuthenticationType,
            name = User.Identity?.Name,
            windowsUser,
            authorizationRestored = HttpContext.Items.ContainsKey(WindowsAuthenticationDefaults.AppliedItemKey),
            authorizationScheme = Request.Headers.Authorization.ToString().Split(' ')[0],
            backOfficeHeaderPresent = Request.Headers.ContainsKey(WindowsAuthenticationDefaults.HeaderName),
        });
    }
}
