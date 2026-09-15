using Microsoft.Playwright;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Backoffice;

/// <summary>
/// Sign-out and sign-in in their own fixture: the test site disallows concurrent logins, so signing in again ends every other
/// backoffice session for the same user.
/// </summary>
public class BackofficeSignOutTests(Hosting hosting) : PlaywrightTest(hosting)
{
    [Test]
    public async Task Signing_out_and_back_in_works()
    {
        IBrowserContext context = await NewBrowserContextAsync();
        try
        {
            BackofficeSession session = await BackofficeSession.SignInAsync(Site, context);

            await session.Page.GotoAsync("/umbraco/section/settings/dashboard/windows-authentication");
            ILocator dashboard = session.Page.Locator("windows-authentication-diagnostics");
            await dashboard.WaitForAsync();
            try
            {
                await dashboard.EvaluateAsync("""
                    async element => {
                        const { UMB_AUTH_CONTEXT } = await import('@umbraco-cms/backoffice/auth');
                        const context = await new Promise(resolve => element.consumeContext(UMB_AUTH_CONTEXT, resolve));
                        await context.signOut();
                    }
                    """);
            }
            catch (PlaywrightException)
            {
                // Signing out navigates away, which can end the script before it returns.
            }

            // Sign-out ends on /umbraco/logout; opening the backoffice again must show the login form.
            await session.Page.WaitForURLAsync("**/umbraco/logout**", new PageWaitForURLOptions { Timeout = 60_000 });
            await session.Page.CloseAsync();
            BackofficeSession again = await BackofficeSession.SignInAsync(Site, context);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(session.Network.BearerRequests, Is.Empty);
                Assert.That(again.Network.BearerRequests, Is.Empty);
                Assert.That(again.Network.UnexpectedFailures(), Is.Empty);
            }
        }
        finally
        {
            await context.CloseAsync();
        }
    }
}
