using Microsoft.Playwright;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

/// <summary>
/// Base for tests that run against the test site under every hosting model: Kestrel without Windows authentication, and
/// IIS Express with anonymous authentication disabled and Windows authentication enabled.
/// </summary>
[TestFixtureSource(typeof(PlaywrightTest), nameof(HostingModels))]
[Category("Playwright")]
public abstract class PlaywrightTest(Hosting hosting)
{
    public static IEnumerable<TestFixtureData> HostingModels()
    {
        yield return new TestFixtureData(Hosting.Kestrel).SetArgDisplayNames("Kestrel");
        yield return new TestFixtureData(Hosting.IisExpressWindowsAuth).SetArgDisplayNames("IIS Express, Windows auth");
    }

    protected Hosting Hosting { get; } = hosting;

    protected TestSiteHost Site { get; private set; } = null!;

    [OneTimeSetUp]
    public async Task StartSite()
    {
        if (Hosting == Hosting.IisExpressWindowsAuth && IisExpressTestSiteHost.IsAvailable is false)
        {
            Assert.Ignore("IIS Express with the ASP.NET Core Module V2 is not installed.");
        }

        string? only = Environment.GetEnvironmentVariable("PLAYWRIGHT_HOSTING");
        if (only is not null && string.Equals(only, Hosting.ToString(), StringComparison.OrdinalIgnoreCase) is false)
        {
            Assert.Ignore($"PLAYWRIGHT_HOSTING={only}");
        }

        Site = await PlaywrightTestRun.GetHostAsync(Hosting);
    }

    protected Task<IBrowserContext> NewBrowserContextAsync()
        => PlaywrightTestRun.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = Site.BaseUrl.ToString(),
            IgnoreHTTPSErrors = true,
        });
}
