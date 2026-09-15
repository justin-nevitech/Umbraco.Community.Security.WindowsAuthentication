using Microsoft.Playwright;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Client;

/// <summary>
/// One browser for every client script test. Uses an installed browser channel (Edge by default, override with BROWSER_CHANNEL),
/// so no Playwright browser download is needed.
/// </summary>
[SetUpFixture]
public class BrowserHost
{
    private static IPlaywright? _playwright;

    public static IBrowser Browser { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task LaunchBrowser()
    {
        // The built script is committed, so a missing one is a broken build rather than a reason to skip.
        Assert.That(File.Exists(RepositoryPaths.ClientScript), $"Build the client first (npm run build in Client), the client script is missing: {RepositoryPaths.ClientScript}");

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = Environment.GetEnvironmentVariable("BROWSER_CHANNEL") ?? "msedge",
            Headless = true,
        });
    }

    [OneTimeTearDown]
    public async Task CloseBrowser()
    {
        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();

        JsCoverage.ReportAndAssert();
    }
}
