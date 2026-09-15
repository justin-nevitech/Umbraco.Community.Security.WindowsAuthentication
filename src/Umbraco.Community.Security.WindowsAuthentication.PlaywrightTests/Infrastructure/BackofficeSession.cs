using System.Text.Json;
using Microsoft.Playwright;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

/// <summary>A browser signed in to the backoffice, with every request to the site recorded.</summary>
public sealed class BackofficeSession : IAsyncDisposable
{
    private BackofficeSession(IBrowserContext context, IPage page, NetworkLog network)
    {
        Context = context;
        Page = page;
        Network = network;
    }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    public NetworkLog Network { get; }

    /// <param name="blockClientScript">Block the client script, so the run shows what happens without it.</param>
    public static async Task<BackofficeSession> SignInAsync(TestSiteHost site, IBrowserContext context, bool blockClientScript = false)
    {
        if (blockClientScript)
        {
            // The built script's file name carries a content hash.
            await context.RouteAsync("**/App_Plugins/WindowsAuthentication/windows-authentication-*.js*", route => route.AbortAsync());
        }

        IPage page = await context.NewPageAsync();
        var network = new NetworkLog(site.BaseUrl);
        network.Watch(page);

        await page.GotoAsync("/umbraco", new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        ILocator username = page.Locator("input[name=\"username\"], input[type=\"email\"]").First;
        await username.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        await username.FillAsync(TestSiteHost.AdminEmail);
        await page.Locator("input[name=\"password\"], input[type=\"password\"]").First.FillAsync(TestSiteHost.AdminPassword);
        await page.Locator("uui-button[type=\"submit\"], button[type=\"submit\"]").First.ClickAsync();

        var session = new BackofficeSession(context, page, network);
        if (blockClientScript is false)
        {
            await page.WaitForURLAsync("**/umbraco/section/**", new PageWaitForURLOptions { Timeout = 90_000 });
            await session.WaitForBackofficeAsync();
        }

        return session;
    }

    /// <summary>Waits until the backoffice has finished loading its extensions and the current user.</summary>
    public async Task WaitForBackofficeAsync()
    {
        await Page.Locator("umb-backoffice-header").WaitForAsync(new LocatorWaitForOptions { Timeout = 90_000 });
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle, new PageWaitForLoadStateOptions { Timeout = 30_000 }).ContinueWith(_ => { });
    }

    /// <summary>
    /// Runs a script in the backoffice page and returns its JSON result. Playwright applies no timeout to scripts, so one is
    /// added here: a request stuck waiting for credentials must fail the test rather than stall the run.
    /// </summary>
    public async Task<JsonElement> EvaluateAsync(string script, object? argument = null)
        => await Page.EvaluateAsync<JsonElement>(script, argument).WaitAsync(TimeSpan.FromMinutes(2));

    public async ValueTask DisposeAsync() => await Context.CloseAsync();
}
