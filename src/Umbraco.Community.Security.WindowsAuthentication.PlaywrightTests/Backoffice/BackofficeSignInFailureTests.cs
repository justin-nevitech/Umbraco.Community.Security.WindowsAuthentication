using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Backoffice;

/// <summary>
/// Umbraco answers a failed backoffice sign-in (wrong credentials, or a locked-out user) with 401. Behind IIS Windows authentication,
/// IIS adds its Negotiate/NTLM challenge to that 401 and the browser prompts for Windows credentials instead of showing Umbraco's
/// message (a headless browser just waits). The sign-in page shows the same message for 400, so the server sends the 401 as 400.
/// </summary>
public class BackofficeSignInFailureTests(Hosting hosting) : PlaywrightTest(hosting)
{
    [Test]
    public async Task Failed_sign_in_shows_umbracos_message_instead_of_a_windows_credentials_prompt()
    {
        IBrowserContext context = await NewBrowserContextAsync();
        try
        {
            IPage page = await context.NewPageAsync();
            await page.GotoAsync("/umbraco", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

            ILocator username = page.Locator("input[name=\"username\"], input[type=\"email\"]").First;
            await username.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

            // An account that doesn't exist, so the shared admin account's failed attempts (and lockout) are never touched.
            await username.FillAsync("nobody@example.com");
            await page.Locator("input[name=\"password\"], input[type=\"password\"]").First.FillAsync("not-the-password");

            Task<IResponse> signIn = page.WaitForResponseAsync(
                response => response.Request.Method == "POST" && response.Url.Contains("/security/back-office/login", StringComparison.OrdinalIgnoreCase),
                new PageWaitForResponseOptions { Timeout = 30_000 });
            await page.Locator("uui-button[type=\"submit\"], button[type=\"submit\"]").First.ClickAsync();
            IResponse response = await signIn;

            bool messageShown;
            try
            {
                await page.GetByText(new Regex("couldn.t log you in", RegexOptions.IgnoreCase)).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
                messageShown = true;
            }
            catch (TimeoutException)
            {
                await page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(Site.RunDirectory, "sign-in-failure.png"), FullPage = true });
                messageShown = false;
            }

            string challenge = response.Headers.GetValueOrDefault("www-authenticate") ?? string.Empty;
            string? marker = response.Headers.GetValueOrDefault("x-umb-authorization-status");

            using (Assert.EnterMultipleScope())
            {
                if (Site.UsesWindowsAuthentication)
                {
                    Assert.That(response.Status, Is.EqualTo(400), "Sent as 400 so IIS does not add its Windows challenge");
                    Assert.That(marker, Is.EqualTo("401"));
                }
                else
                {
                    Assert.That(response.Status, Is.EqualTo(401), "Without Windows authentication Umbraco's response is left alone");
                    Assert.That(marker, Is.Null);
                }

                Assert.That(challenge, Does.Not.Contain("Negotiate").And.Not.Contain("NTLM"), "No Windows challenge reaches the browser");
                Assert.That(messageShown, Is.True, "Umbraco's failed sign-in message is shown");
                Assert.That(page.Url, Does.Contain("/umbraco/login"), "The sign-in page stays open");
            }
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
