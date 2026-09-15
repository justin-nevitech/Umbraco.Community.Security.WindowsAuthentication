using System.Net;
using System.Net.Http.Json;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.FrontEnd;

/// <summary>
/// The package must have no effect on anything outside the backoffice: front-end pages, member authentication, public access,
/// the Delivery API and custom authentication schemes all behave exactly as they would without the package.
/// </summary>
public class FrontEndAuthenticationIsolationTests(Hosting hosting) : PlaywrightTest(hosting)
{
    private const string MemberEmail = "member@example.com";
    private const string MemberPassword = "WindowsAuth-Member-1234";
    private const string DeliveryApiKey = "test-delivery-api-key";
    private const string HomeKey = "7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a02";
    private const string DraftKey = "7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a05";

    [Test]
    public async Task Client_script_is_never_loaded_on_front_end_pages()
    {
        IBrowserContext context = await NewBrowserContextAsync();
        IPage page = await context.NewPageAsync();
        var network = new NetworkLog(Site.BaseUrl);
        network.Watch(page);

        foreach (string path in new[] { "/", "/members-only/", "/member-login/" })
        {
            await page.GotoAsync(path);
            Assert.That(await page.EvaluateAsync<bool>("() => window.__umbWindowsAuthentication === undefined"), Is.True, path);
        }

        await context.CloseAsync();
        Assert.That(network.RequestedUrls, Has.None.Contains("windows-authentication"));
    }

    [Test]
    public async Task Bearer_header_set_by_front_end_script_is_sent_unmodified()
    {
        IBrowserContext context = await NewBrowserContextAsync();
        IPage page = await context.NewPageAsync();
        var network = new NetworkLog(Site.BaseUrl);
        network.Watch(page);
        await page.GotoAsync("/");

        int status = await page.EvaluateAsync<int>("""
            async () => {
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), 10000);
                try {
                    const response = await fetch('/api/test-auth/anonymous', { headers: { Authorization: 'Bearer front-end-token' }, signal: controller.signal });
                    return response.status;
                } catch {
                    return -1;
                } finally {
                    clearTimeout(timer);
                }
            }
            """);
        await context.CloseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(network.BearerRequests, Has.Some.Contains("/api/test-auth/anonymous"));
            Assert.That(network.RelayedRequests, Is.Empty);

            // Behind Windows authentication IIS refuses a script-set Authorization header whether or not the package is installed,
            // and a headless browser then waits for credentials it cannot prompt for (-1: aborted after 10 seconds).
            Assert.That(status, Site.UsesWindowsAuthentication ? Is.AnyOf(401, -1) : Is.EqualTo(200));
        }
    }

    [Test]
    public async Task Public_page_renders_for_an_anonymous_visitor()
    {
        IBrowserContext context = await NewBrowserContextAsync();
        IPage page = await context.NewPageAsync();

        IResponse? response = await page.GotoAsync("/");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response?.Status, Is.EqualTo(200));
            Assert.That(await page.Locator("#page-name").TextContentAsync(), Is.EqualTo("Home"));
            Assert.That(await page.Locator("#member").TextContentAsync(), Is.EqualTo("anonymous"));
        }

        await context.CloseAsync();
    }

    [Test]
    public async Task Protected_page_requires_member_login_and_member_cookie_authentication_works()
    {
        IBrowserContext context = await NewBrowserContextAsync();
        IPage page = await context.NewPageAsync();
        var network = new NetworkLog(Site.BaseUrl);
        network.Watch(page);

        await page.GotoAsync("/members-only/");
        string? beforeLogin = await page.Locator("#page-name").TextContentAsync();
        int memberApiBefore = await StatusFromPageAsync(page, "/api/test-auth/member");

        await page.Locator("#username").FillAsync(MemberEmail);
        await page.Locator("#password").FillAsync(MemberPassword);
        await SubmitLoginAsync(page);
        await page.GotoAsync("/members-only/");

        string? afterLogin = await page.Locator("#page-name").TextContentAsync();
        string? member = await page.Locator("#member").TextContentAsync();
        JsonElement memberApi = await page.EvaluateAsync<JsonElement>("() => fetch('/api/test-auth/member').then(async r => ({ status: r.status, body: await r.json() }))");

        await context.CloseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(beforeLogin, Is.EqualTo("Member Login"), "Public access shows the login page to anonymous visitors");
            Assert.That(memberApiBefore, UnauthorisedInBrowser());
            Assert.That(afterLogin, Is.EqualTo("Members Only"));
            Assert.That(member, Is.EqualTo(MemberEmail));
            Assert.That(memberApi.GetProperty("status").GetInt32(), Is.EqualTo(200));
            Assert.That(memberApi.GetProperty("body").GetProperty("name").GetString(), Is.EqualTo(MemberEmail));
            Assert.That(memberApi.GetProperty("body").GetProperty("authorizationRestored").GetBoolean(), Is.False);
            Assert.That(network.RelayedRequests, Is.Empty);
        }
    }

    [Test]
    public async Task Wrong_member_password_is_rejected()
    {
        IBrowserContext context = await NewBrowserContextAsync();
        IPage page = await context.NewPageAsync();

        await page.GotoAsync("/members-only/");
        await page.Locator("#username").FillAsync(MemberEmail);
        await page.Locator("#password").FillAsync("wrong-password");
        await SubmitLoginAsync(page);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await page.Locator("#page-name").TextContentAsync(), Is.EqualTo("Member Login"));
            Assert.That(await StatusFromPageAsync(page, "/api/test-auth/member"), UnauthorisedInBrowser());
        }

        await context.CloseAsync();
    }

    [Test]
    public async Task Custom_jwt_scheme_behaves_normally_for_front_end_clients()
    {
        AssumeHeaderBasedClientsCanReachTheSite();
        using HttpClient client = Site.CreateHttpClient();
        string jwt = await IssueJwtAsync(client);

        using (Assert.EnterMultipleScope())
        {
            JsonElement valid = await GetJsonAsync(client, "api/test-auth/jwt", HttpStatusCode.OK, ("Authorization", $"Bearer {jwt}"));
            Assert.That(valid.GetProperty("name").GetString(), Is.EqualTo("front-end-user"));
            Assert.That(valid.GetProperty("authorizationRestored").GetBoolean(), Is.False);

            await GetJsonAsync(client, "api/test-auth/jwt", HttpStatusCode.Unauthorized);
            await GetJsonAsync(client, "api/test-auth/jwt", HttpStatusCode.Unauthorized, ("Authorization", "Bearer not-a-jwt"));

            // A stray X-Umb-Authorization header next to the client's own bearer token changes nothing.
            JsonElement stray = await GetJsonAsync(client, "api/test-auth/jwt", HttpStatusCode.OK, ("Authorization", $"Bearer {jwt}"), ("X-Umb-Authorization", "Bearer something-else"));
            Assert.That(stray.GetProperty("name").GetString(), Is.EqualTo("front-end-user"));
            Assert.That(stray.GetProperty("authorizationRestored").GetBoolean(), Is.False);
        }
    }

    [Test]
    public async Task Custom_basic_scheme_behaves_normally_for_front_end_clients()
    {
        AssumeHeaderBasedClientsCanReachTheSite();
        using HttpClient client = Site.CreateHttpClient();
        string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("basic-user:basic-pass"));

        using (Assert.EnterMultipleScope())
        {
            JsonElement valid = await GetJsonAsync(client, "api/test-auth/basic", HttpStatusCode.OK, ("Authorization", $"Basic {credentials}"));
            Assert.That(valid.GetProperty("name").GetString(), Is.EqualTo("basic-user"));

            await GetJsonAsync(client, "api/test-auth/basic", HttpStatusCode.Unauthorized, ("Authorization", "Basic d3Jvbmc6d3Jvbmc="));

            JsonElement stray = await GetJsonAsync(client, "api/test-auth/basic", HttpStatusCode.OK, ("Authorization", $"Basic {credentials}"), ("X-Umb-Authorization", "Bearer something-else"));
            Assert.That(stray.GetProperty("name").GetString(), Is.EqualTo("basic-user"));
            Assert.That(stray.GetProperty("authorizationScheme").GetString(), Is.EqualTo("Basic"));
            Assert.That(stray.GetProperty("authorizationRestored").GetBoolean(), Is.False);
        }
    }

    [Test]
    public async Task Custom_api_key_scheme_behaves_normally()
    {
        using HttpClient client = Site.CreateHttpClient();

        using (Assert.EnterMultipleScope())
        {
            JsonElement valid = await GetJsonAsync(client, "api/test-auth/api-key", HttpStatusCode.OK, ("X-Api-Key", "test-api-key"));
            Assert.That(valid.GetProperty("name").GetString(), Is.EqualTo("api-client"));

            await GetJsonAsync(client, "api/test-auth/api-key", HttpStatusCode.Unauthorized, ("X-Api-Key", "wrong"));

            JsonElement stray = await GetJsonAsync(client, "api/test-auth/api-key", HttpStatusCode.OK, ("X-Api-Key", "test-api-key"), ("X-Umb-Authorization", "Basic abc"));
            Assert.That(stray.GetProperty("backOfficeHeaderPresent").GetBoolean(), Is.True, "An invalid X-Umb-Authorization header is left exactly as sent");
            Assert.That(stray.GetProperty("authorizationRestored").GetBoolean(), Is.False);
        }
    }

    [Test]
    public async Task Custom_jwt_scheme_called_from_the_backoffice_is_passed_through_transparently()
    {
        using HttpClient anonymousClient = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseDefaultCredentials = Site.UsesWindowsAuthentication }) { BaseAddress = Site.BaseUrl };
        string jwt = await IssueJwtAsync(anonymousClient);

        await using BackofficeSession session = await BackofficeSession.SignInAsync(Site, await NewBrowserContextAsync());
        JsonElement result = await session.EvaluateAsync(
            "jwt => fetch('/api/test-auth/jwt', { headers: { Authorization: `Bearer ${jwt}` } }).then(async r => ({ status: r.status, body: await r.json() }))",
            jwt);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetInt32(), Is.EqualTo(200));
            Assert.That(result.GetProperty("body").GetProperty("name").GetString(), Is.EqualTo("front-end-user"));
            Assert.That(result.GetProperty("body").GetProperty("authorizationRestored").GetBoolean(), Is.True);
            Assert.That(session.Network.BearerRequests, Is.Empty);
        }
    }

    [Test]
    public async Task Delivery_api_behaves_normally_with_and_without_its_api_key()
    {
        using HttpClient client = Site.CreateHttpClient();

        using (Assert.EnterMultipleScope())
        {
            JsonElement home = await GetJsonAsync(client, $"umbraco/delivery/api/v2/content/item/{HomeKey}", HttpStatusCode.OK);
            Assert.That(home.GetProperty("name").GetString(), Is.EqualTo("Home"));

            JsonElement draft = await GetJsonAsync(client, $"umbraco/delivery/api/v2/content/item/{DraftKey}", HttpStatusCode.OK, ("Api-Key", DeliveryApiKey), ("Preview", "true"));
            Assert.That(draft.GetProperty("name").GetString(), Is.EqualTo("Draft Page"));

            using HttpResponseMessage withoutKey = await client.GetAsync($"umbraco/delivery/api/v2/content/item/{DraftKey}");
            Assert.That(withoutKey.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

            using var request = new HttpRequestMessage(HttpMethod.Get, $"umbraco/delivery/api/v2/content/item/{DraftKey}");
            request.Headers.Add("Api-Key", "wrong-key");
            request.Headers.Add("Preview", "true");
            using HttpResponseMessage wrongKey = await client.SendAsync(request);
            Assert.That(wrongKey.StatusCode, Is.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.NotFound));
        }
    }

    [Test]
    public async Task Windows_identity_is_still_available_to_front_end_code()
    {
        Assume.That(Site.UsesWindowsAuthentication && OperatingSystem.IsWindows(), "Only applies behind Windows authentication");

        using HttpClient client = Site.CreateHttpClient();
        JsonElement result = await GetJsonAsync(client, "api/test-auth/anonymous", HttpStatusCode.OK);

        Assert.That(result.GetProperty("windowsUser").GetString(), Is.EqualTo(CurrentWindowsUser()));
    }

    /// <summary>
    /// Status of a fetch made by the page, or -1 if no response arrived within 10 seconds. Behind Windows authentication IIS
    /// adds its Negotiate/NTLM challenge to any 401 the application returns, and a headless browser then waits for credentials
    /// it cannot prompt for.
    /// </summary>
    private static Task<int> StatusFromPageAsync(IPage page, string path)
        => page.EvaluateAsync<int>(
            """
            async path => {
                const controller = new AbortController();
                const timer = setTimeout(() => controller.abort(), 10000);
                try {
                    return (await fetch(path, { signal: controller.signal })).status;
                } catch {
                    return -1;
                } finally {
                    clearTimeout(timer);
                }
            }
            """,
            path);

    private NUnit.Framework.Constraints.IResolveConstraint UnauthorisedInBrowser()
        => Site.UsesWindowsAuthentication ? Is.AnyOf(401, -1) : Is.EqualTo(401);

    private static async Task SubmitLoginAsync(IPage page)
    {
        Task<IResponse> posted = page.WaitForResponseAsync(response => response.Request.Method == "POST" && response.Request.IsNavigationRequest);
        await page.Locator("#login").ClickAsync();
        await posted;
        await page.WaitForLoadStateAsync(LoadState.Load);
    }

    private void AssumeHeaderBasedClientsCanReachTheSite()
        => Assume.That(
            Site.UsesWindowsAuthentication,
            Is.False,
            "Behind Windows authentication IIS refuses any client-supplied Authorization header, with or without the package");

    private static async Task<string> IssueJwtAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync("api/test-auth/jwt-token?subject=front-end-user", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadAsStringAsync()).Trim('"');
    }

    private static async Task<JsonElement> GetJsonAsync(HttpClient client, string path, HttpStatusCode expectedStatus, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        foreach ((string name, string value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        Assert.That(response.StatusCode, Is.EqualTo(expectedStatus), $"GET {path}: {body}");
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private static string? CurrentWindowsUser() => OperatingSystem.IsWindows() ? WindowsIdentity.GetCurrent().Name : null;
}
