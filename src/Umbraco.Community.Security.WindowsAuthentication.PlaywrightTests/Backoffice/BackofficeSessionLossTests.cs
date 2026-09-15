using Microsoft.Playwright;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Backoffice;

/// <summary>
/// When a backoffice session ends, the next API call gets a 401. Behind IIS Windows authentication, IIS adds its Negotiate/NTLM
/// challenge to that 401 and the browser prompts for Windows credentials (a headless browser just waits). The server sends the
/// 401 as 403 with X-Umb-Authorization-Status instead, and the client script restores the 401 so Umbraco shows its own re-login.
/// </summary>
public class BackofficeSessionLossTests(Hosting hosting) : PlaywrightTest(hosting)
{
    [Test]
    public async Task Lost_session_shows_the_umbraco_re_login_instead_of_a_windows_credentials_prompt()
    {
        IBrowserContext context = await NewBrowserContextAsync();
        try
        {
            BackofficeSession session = await BackofficeSession.SignInAsync(Site, context);

            // The backoffice token lives in an httpOnly cookie; without it the next API call is unauthorised.
            await context.ClearCookiesAsync();

            Task<IResponse> hiddenUnauthorized = session.Page.WaitForResponseAsync(
                response => response.Url.Contains("/umbraco/management/api/", StringComparison.OrdinalIgnoreCase)
                            && response.Status == 403
                            && response.Headers.TryGetValue("x-umb-authorization-status", out string? status)
                            && status == "401",
                new PageWaitForResponseOptions { Timeout = 60_000 });

            // Fire and forget: GET requests that get a 401 wait until the user has signed in again.
            await session.Page.EvaluateAsync("() => { import('@umbraco-cms/backoffice/external/backend-api').then(api => api.UserService.getUserCurrent()); }");

            IResponse response = await hiddenUnauthorized;
            bool reLoginShown;
            try
            {
                await session.Page.Locator("umb-app-auth-modal").WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
                reLoginShown = true;
            }
            catch (TimeoutException)
            {
                await session.Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Site.RunDirectory, "session-loss.png"), FullPage = true });
                reLoginShown = false;
            }

            int restored = await session.Page.EvaluateAsync<int>("() => window.__umbWindowsAuthentication.unauthorizedRestored");

            using (Assert.EnterMultipleScope())
            {
                Assert.That(response.Headers["www-authenticate"], Does.Contain("Bearer"), "The original challenge is kept");
                Assert.That(response.Headers.ContainsKey("www-authenticate") && response.Headers["www-authenticate"].Contains("Negotiate", StringComparison.OrdinalIgnoreCase), Is.False, "IIS must not add its Windows challenge");
                Assert.That(restored, Is.GreaterThan(0), "The client script restored the 401");
                Assert.That(reLoginShown, Is.True, "Umbraco's re-login is shown");
                Assert.That(session.Network.BearerRequests, Is.Empty);
            }
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
