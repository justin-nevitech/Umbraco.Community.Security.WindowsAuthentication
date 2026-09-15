namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Client;

[TestFixture]
public class ClientScriptInstallationTests : ClientScriptBrowserTest
{
    [Test]
    public async Task Patches_fetch_and_xhr_when_the_module_loads()
    {
        await OpenBackofficeAsync();

        bool[] patched = await Page.EvaluateAsync<bool[]>("""
            () => [
                window.fetch !== window.__recordingFetch,
                XMLHttpRequest.prototype.open !== window.__recordingOpen,
                XMLHttpRequest.prototype.setRequestHeader !== window.__nativeSetRequestHeader,
                window.__umbWindowsAuthentication.installedAt > 0,
            ]
            """);

        Assert.That(patched, Is.All.True);
    }

    [Test]
    public async Task Makes_no_requests_of_its_own()
    {
        await OpenBackofficeAsync();
        await Page.WaitForTimeoutAsync(250);

        Assert.That(Requests, Is.Empty);
    }

    [Test]
    public async Task Logs_a_debug_message_when_installed()
    {
        await OpenBackofficeAsync();

        Assert.That(ConsoleMessages.Where(m => m.Type == "debug").Select(m => m.Text), Has.One.StartsWith("[WindowsAuthentication] installed after"));
    }

    [Test]
    public async Task Loading_the_module_again_does_not_patch_twice()
    {
        await OpenBackofficeAsync();

        bool unchanged = await Page.EvaluateAsync<bool>("""
            async () => {
                const state = window.__umbWindowsAuthentication;
                const fetchBefore = window.fetch;
                const openBefore = XMLHttpRequest.prototype.open;
                await import('/windows-authentication.js?again');
                return window.__umbWindowsAuthentication === state && window.fetch === fetchBefore && XMLHttpRequest.prototype.open === openBefore;
            }
            """);

        await Page.EvaluateAsync("() => fetch('/umbraco/a', { headers: { Authorization: 'Bearer abc' } }).then(r => r.status)");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unchanged, Is.True);
            Assert.That(await RewriteCountsAsync(), Is.EqualTo((1, 0)));
        }
    }

    [Test]
    public async Task Records_management_api_requests_that_started_before_the_script_and_warns_about_authenticated_ones()
    {
        await OpenBackofficeAsync(beforeScript: """
            fetch('/umbraco/management/api/v1/server/status');
            fetch('/umbraco/management/api/v1/user/current');
            fetch('/umbraco/other');
            """);

        await Page.WaitForFunctionAsync("() => window.__umbWindowsAuthentication.preInstallRequests.length === 2");
        await Page.EvaluateAsync("() => fetch('/umbraco/management/api/v1/user/current').then(r => r.status)");
        await Page.WaitForTimeoutAsync(250);

        string[] preInstall = await Page.EvaluateAsync<string[]>("() => window.__umbWindowsAuthentication.preInstallRequests");
        string[] warnings = ConsoleMessages.Where(m => m.Type == "warning").Select(m => m.Text).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preInstall, Is.EquivalentTo(new[] { "/umbraco/management/api/v1/server/status", "/umbraco/management/api/v1/user/current" }));
            Assert.That(warnings, Has.Length.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("/umbraco/management/api/v1/user/current started before the client script was installed"));
        }
    }

    [Test]
    public async Task Installs_without_performance_observer()
    {
        await OpenBackofficeAsync(beforeScript: "window.PerformanceObserver = undefined;");

        await Page.EvaluateAsync("() => fetch('/umbraco/a', { headers: { Authorization: 'Bearer abc' } }).then(r => r.status)");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SingleRequest("/umbraco/a").Header("x-umb-authorization"), Is.EqualTo("Bearer abc"));
            Assert.That(await Page.EvaluateAsync<string[]>("() => window.__umbWindowsAuthentication.preInstallRequests"), Is.Empty);
        }
    }

    [Test]
    public async Task Stops_watching_for_early_requests_after_30_seconds()
    {
        await Page.Clock.InstallAsync();
        await OpenBackofficeAsync(beforeScript: """
            const disconnect = PerformanceObserver.prototype.disconnect;
            PerformanceObserver.prototype.disconnect = function () {
                window.__observerDisconnected = true;
                return disconnect.call(this);
            };
            """);

        bool beforeTimeout = await Page.EvaluateAsync<bool>("() => window.__observerDisconnected === true");
        await Page.Clock.RunForAsync(30_000);
        bool afterTimeout = await Page.EvaluateAsync<bool>("() => window.__observerDisconnected === true");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeTimeout, Is.False);
            Assert.That(afterTimeout, Is.True);
        }
    }
}
