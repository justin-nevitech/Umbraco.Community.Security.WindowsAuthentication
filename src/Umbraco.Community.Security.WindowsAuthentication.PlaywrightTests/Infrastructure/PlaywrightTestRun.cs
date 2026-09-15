using Microsoft.Playwright;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

// Every fixture shares one browser and one site per hosting model; hosts are started on first use.
namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests;

[SetUpFixture]
public class PlaywrightTestRun
{
    private static readonly Dictionary<Hosting, Lazy<Task<TestSiteHost>>> Hosts = new();
    private static readonly Lock Gate = new();
    private static IPlaywright? _playwright;

    public static IBrowser Browser { get; private set; } = null!;

    public static Task<TestSiteHost> GetHostAsync(Hosting hosting)
    {
        lock (Gate)
        {
            if (Hosts.TryGetValue(hosting, out Lazy<Task<TestSiteHost>>? host) is false)
            {
                host = new Lazy<Task<TestSiteHost>>(async () =>
                {
                    TestSiteHost site = TestSiteHost.Create(hosting);
                    TestContext.Progress.WriteLine($"Starting {hosting} test site in {site.RunDirectory}");
                    await site.StartAsync();
                    TestContext.Progress.WriteLine($"{hosting} test site running at {site.BaseUrl}");
                    return site;
                });
                Hosts[hosting] = host;
            }

            return host.Value;
        }
    }

    [OneTimeSetUp]
    public async Task LaunchBrowser()
    {
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Channel = Environment.GetEnvironmentVariable("BROWSER_CHANNEL") ?? "msedge",
            Headless = Environment.GetEnvironmentVariable("HEADED") is null,
            // A headless browser can't show a credentials prompt, so allow integrated Windows authentication for localhost.
            Args = ["--auth-server-allowlist=localhost"],
        });
    }

    [OneTimeTearDown]
    public async Task StopEverything()
    {
        foreach (Lazy<Task<TestSiteHost>> host in Hosts.Values.Where(h => h.IsValueCreated))
        {
            if (host.Value.IsCompletedSuccessfully)
            {
                await host.Value.Result.DisposeAsync();
            }
        }

        if (Browser is not null)
        {
            await Browser.CloseAsync();
        }

        _playwright?.Dispose();
    }
}
