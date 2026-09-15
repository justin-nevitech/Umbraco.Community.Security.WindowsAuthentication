using System.Security.Cryptography;
using Asp.Versioning;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.IIS;
using Umbraco.Cms.Web.Common.Authorization;
using Umbraco.Cms.Web.Common.Routing;
using Umbraco.Community.Security.WindowsAuthentication;

namespace Umbraco.Community.Security.WindowsAuthentication.TestSite.Diagnostics;

/// <summary>
/// Stands in for a third-party package's backoffice API: a plain [Authorize] controller that knows nothing about this package.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[BackOfficeRoute("windows-authentication/api/v{version:apiVersion}")]
[Authorize(Policy = AuthorizationPolicies.BackOfficeAccess)]
[ApiExplorerSettings(IgnoreApi = true)]
public class WindowsAuthenticationDiagnosticsController(IAuthenticationSchemeProvider schemeProvider) : ControllerBase
{
    [HttpGet("whoami")]
    public async Task<IActionResult> WhoAmI()
    {
        string? windowsUser = null;
        if (await schemeProvider.GetSchemeAsync(IISServerDefaults.AuthenticationScheme) is not null)
        {
            AuthenticateResult windows = await HttpContext.AuthenticateAsync(IISServerDefaults.AuthenticationScheme);
            windowsUser = windows.Principal?.Identity?.Name;
        }

        return Ok(new
        {
            umbracoUser = User.Identity?.Name,
            windowsUser,
            authorizationRestored = HttpContext.Items.ContainsKey(WindowsAuthenticationDefaults.AppliedItemKey),
            server = HttpContext.Request.Host.Value,
        });
    }

    /// <summary>Reports exactly what arrived, so tests can compare it with what the browser sent.</summary>
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", Route = "echo")]
    public async Task<IActionResult> Echo()
    {
        // Form bodies can already have been read by the pipeline, so report them through the form reader, which caches them.
        object? form = null;
        byte[] body = [];
        if (Request.HasFormContentType)
        {
            IFormCollection collection = await Request.ReadFormAsync();
            form = new
            {
                fields = collection.ToDictionary(field => field.Key, field => field.Value.ToString()),
                files = collection.Files.Select(file =>
                {
                    using Stream stream = file.OpenReadStream();
                    return new { name = file.Name, fileName = file.FileName, length = file.Length, sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
                }).ToArray(),
            };
        }
        else
        {
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer);
            body = buffer.ToArray();
        }

        return Ok(new
        {
            method = Request.Method,
            contentType = Request.ContentType,
            bodyLength = body.Length,
            bodySha256 = Convert.ToHexString(SHA256.HashData(body)),
            form,
            query = Request.QueryString.Value,
            testHeaders = Request.Headers
                .Where(h => h.Key.StartsWith("X-Test", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString()),
            umbracoUser = User.Identity?.Name,
            authorizationRestored = HttpContext.Items.ContainsKey(WindowsAuthenticationDefaults.AppliedItemKey),
            backOfficeHeaderPresent = Request.Headers.ContainsKey(WindowsAuthenticationDefaults.HeaderName),
        });
    }

    [HttpGet("download")]
    public IActionResult Download(int size = 64 * 1024)
        => File(Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray(), "application/octet-stream", "download-test.bin");
}
