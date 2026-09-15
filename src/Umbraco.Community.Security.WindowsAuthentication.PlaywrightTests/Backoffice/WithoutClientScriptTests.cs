using Microsoft.Playwright;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Backoffice;

/// <summary>
/// Negative control: with the client script blocked, the backoffice must fail behind Windows authentication and still work
/// without it. If this ever passes with Windows authentication on, the other tests are no longer proving anything.
/// </summary>
public class WithoutClientScriptTests(Hosting hosting) : PlaywrightTest(hosting)
{
    [Test]
    public async Task Without_the_client_script_the_backoffice_only_works_when_windows_authentication_is_off()
    {
        await using BackofficeSession session = await BackofficeSession.SignInAsync(Site, await NewBrowserContextAsync(), blockClientScript: true);

        bool loaded;
        try
        {
            await session.Page.Locator("umb-backoffice-header").WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
            await session.WaitForBackofficeAsync();
            loaded = true;
        }
        catch (TimeoutException)
        {
            loaded = false;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.Network.BearerRequests, Has.Some.Contains("/manifest/manifest/private"), "Without the client script the bearer header goes out as normal");
            if (Site.UsesWindowsAuthentication)
            {
                Assert.That(loaded, Is.False, "Behind Windows authentication the backoffice must not load without the client script");
            }
            else
            {
                Assert.That(loaded, Is.True, "Without Windows authentication the backoffice works without the client script");
            }
        }
    }
}
