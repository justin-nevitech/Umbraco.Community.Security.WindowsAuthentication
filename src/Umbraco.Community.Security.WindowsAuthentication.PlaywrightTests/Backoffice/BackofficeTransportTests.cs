using System.Text.Json;
using Microsoft.Playwright;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Backoffice;

/// <summary>
/// Sends every kind of authenticated request the backoffice and its packages make to a plain [Authorize] backoffice API,
/// and checks each arrives exactly as sent, authenticated, with no bearer header on the wire.
/// </summary>
public class BackofficeTransportTests(Hosting hosting) : PlaywrightTest(hosting)
{
    private const string EchoScript = """
        async ({ via, method, bodyKind }) => {
            const { umbHttpClient } = await import('@umbraco-cms/backoffice/http-client');
            const token = await umbHttpClient.getConfig().auth();
            const url = '/umbraco/windows-authentication/api/v1/echo?source=test';
            const sha256 = async bytes => [...new Uint8Array(await crypto.subtle.digest('SHA-256', bytes))].map(b => b.toString(16).padStart(2, '0')).join('').toUpperCase();
            const binary = new Uint8Array(70000).map((_, i) => i % 251);
            const bodies = {
                none: async () => ({ body: undefined, bytes: new Uint8Array(0) }),
                json: async () => { const text = JSON.stringify({ hello: 'world', n: 1 }); return { body: text, bytes: new TextEncoder().encode(text), contentType: 'application/json' }; },
                binary: async () => ({ body: binary, bytes: binary, contentType: 'application/octet-stream' }),
                blob: async () => ({ body: new Blob([binary], { type: 'application/octet-stream' }), bytes: binary }),
                urlencoded: async () => ({ body: new URLSearchParams({ a: '1', b: 'two' }), form: { fields: { a: '1', b: 'two' }, files: [] } }),
                formdata: async () => {
                    const form = new FormData();
                    form.append('file', new Blob([binary]), 'test.bin');
                    form.append('field', 'value');
                    return { body: form, form: { fields: { field: 'value' }, files: [{ name: 'file', fileName: 'test.bin', length: binary.length, sha256: await sha256(binary) }] } };
                },
            };
            const { body, bytes, contentType, form } = await bodies[bodyKind]();
            const headers = { Authorization: `Bearer ${token}`, 'X-Test-Trace': 'abc' };
            if (contentType) headers['Content-Type'] = contentType;

            let status, echo;
            if (via === 'fetch') {
                const response = await fetch(url, { method, body, headers, credentials: 'include' });
                status = response.status;
                echo = await response.json();
            } else {
                ({ status, echo } = await new Promise((resolve, reject) => {
                    const xhr = new XMLHttpRequest();
                    xhr.open(method, url);
                    xhr.withCredentials = true;
                    for (const [name, value] of Object.entries(headers)) xhr.setRequestHeader(name, value);
                    xhr.onload = () => resolve({ status: xhr.status, echo: JSON.parse(xhr.responseText) });
                    xhr.onerror = () => reject(new Error('XHR network error'));
                    xhr.send(body ?? null);
                }));
            }

            return {
                status,
                echo,
                expectedSha256: bytes ? await sha256(bytes) : null,
                expectedLength: bytes ? bytes.length : null,
                expectedForm: form ?? null,
            };
        }
        """;

    private BackofficeSession _session = null!;

    [OneTimeSetUp]
    public async Task SignIn() => _session = await BackofficeSession.SignInAsync(Site, await NewBrowserContextAsync());

    [OneTimeTearDown]
    public async Task SignOut() => await _session.DisposeAsync();

    [Test]
    public async Task Backoffice_loads_without_a_bearer_header_reaching_the_wire()
    {
        JsonElement state = await _session.EvaluateAsync("() => window.__umbWindowsAuthentication");
        string[] preInstall = state.GetProperty("preInstallRequests").EnumerateArray().Select(e => e.GetString()!).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_session.Network.BearerRequests, Is.Empty);
            Assert.That(_session.Network.RelayedRequests, Is.Not.Empty);
            Assert.That(_session.Network.UnexpectedFailures(), Is.Empty);
            Assert.That(_session.Network.PageErrors, Is.Empty);
            Assert.That(preInstall, Is.All.Contains("/server/"), "Only anonymous server calls may start before the client script");
        }
    }

    public static IEnumerable<TestCaseData> Requests()
    {
        foreach (string via in new[] { "fetch", "xhr" })
        {
            foreach (string method in new[] { "GET", "DELETE" })
            {
                yield return new TestCaseData(via, method, "none").SetArgDisplayNames(via, method, "no body");
            }

            foreach (string method in new[] { "POST", "PUT", "PATCH" })
            {
                foreach (string body in new[] { "none", "json", "binary", "blob", "urlencoded", "formdata" })
                {
                    yield return new TestCaseData(via, method, body).SetArgDisplayNames(via, method, body);
                }
            }
        }
    }

    [TestCaseSource(nameof(Requests))]
    public async Task Request_reaches_a_backoffice_api_exactly_as_sent(string via, string method, string bodyKind)
    {
        JsonElement result = await _session.EvaluateAsync(EchoScript, new { via, method, bodyKind });
        JsonElement echo = result.GetProperty("echo");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetInt32(), Is.EqualTo(200));
            Assert.That(echo.GetProperty("method").GetString(), Is.EqualTo(method));
            Assert.That(echo.GetProperty("umbracoUser").GetString(), Is.EqualTo(TestSiteHost.AdminEmail));
            Assert.That(echo.GetProperty("authorizationRestored").GetBoolean(), Is.True);
            Assert.That(echo.GetProperty("backOfficeHeaderPresent").GetBoolean(), Is.False);
            Assert.That(echo.GetProperty("query").GetString(), Is.EqualTo("?source=test"));
            Assert.That(echo.GetProperty("testHeaders").GetProperty("x-test-trace").GetString(), Is.EqualTo("abc"));

            if (result.GetProperty("expectedForm") is { ValueKind: JsonValueKind.Object } expectedForm)
            {
                JsonElement form = echo.GetProperty("form");
                Assert.That(echo.GetProperty("contentType").GetString(), Does.StartWith(bodyKind == "formdata" ? "multipart/form-data; boundary=" : "application/x-www-form-urlencoded"));
                Assert.That(Canonical(form.GetProperty("fields")), Is.EqualTo(Canonical(expectedForm.GetProperty("fields"))));
                Assert.That(Canonical(form.GetProperty("files")), Is.EqualTo(Canonical(expectedForm.GetProperty("files"))));
            }
            else
            {
                Assert.That(echo.GetProperty("bodyLength").GetInt32(), Is.EqualTo(result.GetProperty("expectedLength").GetInt32()));
                Assert.That(echo.GetProperty("bodySha256").GetString(), Is.EqualTo(result.GetProperty("expectedSha256").GetString()));
            }

            Assert.That(_session.Network.BearerRequests, Is.Empty);
        }
    }

    /// <summary>
    /// JSON with object keys sorted, so objects built in the browser and on the server compare by content. Playwright adds
    /// "$id" reference markers to objects returned from the page, which are ignored.
    /// </summary>
    private static string Canonical(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Where(p => p.Name != "$id").OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => $"\"{p.Name}\":{Canonical(p.Value)}")) + "}",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(Canonical)) + "]",
        _ => element.GetRawText(),
    };

    [Test]
    public async Task Umbraco_http_client_json_request_reaches_a_backoffice_api()
    {
        JsonElement result = await _session.EvaluateAsync("""
            async () => {
                const { umbHttpClient } = await import('@umbraco-cms/backoffice/http-client');
                try {
                    const { data, error, response } = await umbHttpClient.put({
                        url: '/umbraco/windows-authentication/api/v1/echo',
                        body: { hello: 'world' },
                        headers: { 'Content-Type': 'application/json' },
                        security: [{ scheme: 'bearer', type: 'http' }],
                    });
                    return { status: response?.status ?? 0, echo: data ?? null, error: error ? JSON.stringify(error) : null };
                } catch (thrown) {
                    return { status: -1, echo: null, error: `${thrown?.name}: ${thrown?.message} ${JSON.stringify(thrown)}` };
                }
            }
            """);

        Assert.That(result.GetProperty("status").GetInt32(), Is.EqualTo(200), result.GetProperty("error").ToString());
        JsonElement echo = result.GetProperty("echo");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(echo.GetProperty("method").GetString(), Is.EqualTo("PUT"));
            Assert.That(echo.GetProperty("contentType").GetString(), Does.StartWith("application/json"));
            Assert.That(echo.GetProperty("authorizationRestored").GetBoolean(), Is.True);
        }
    }

    [Test]
    public async Task Binary_download_arrives_intact()
    {
        JsonElement result = await _session.EvaluateAsync("""
            async () => {
                const { umbHttpClient } = await import('@umbraco-cms/backoffice/http-client');
                const token = await umbHttpClient.getConfig().auth();
                const response = await fetch('/umbraco/windows-authentication/api/v1/download?size=200000', { headers: { Authorization: `Bearer ${token}` } });
                const bytes = new Uint8Array(await response.arrayBuffer());
                return { status: response.status, length: bytes.length, intact: bytes.every((b, i) => b === i % 251), disposition: response.headers.get('content-disposition') };
            }
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("status").GetInt32(), Is.EqualTo(200));
            Assert.That(result.GetProperty("length").GetInt32(), Is.EqualTo(200000));
            Assert.That(result.GetProperty("intact").GetBoolean(), Is.True);
            Assert.That(result.GetProperty("disposition").GetString(), Does.Contain("download-test.bin"));
        }
    }

    [Test]
    public async Task Core_xhr_upload_with_progress_succeeds()
    {
        JsonElement result = await _session.EvaluateAsync("""
            async () => {
                const { tryXhrRequest } = await import('@umbraco-cms/backoffice/resources');
                const body = new FormData();
                const id = crypto.randomUUID();
                body.append('Id', id);
                body.append('File', new Blob([new Uint8Array(300000)], { type: 'application/octet-stream' }), 'upload-test.bin');
                let progressEvents = 0;
                const before = window.__umbWindowsAuthentication.rewrites.xhr;
                const { error } = await tryXhrRequest(document.querySelector('umb-app'), {
                    url: '/umbraco/management/api/v1/temporary-file',
                    method: 'POST',
                    responseHeader: 'Umb-Generated-Resource',
                    disableNotifications: true,
                    onProgress: () => progressEvents++,
                    body,
                });
                return { error: error ? String(error.message ?? error) : null, xhrRewrites: window.__umbWindowsAuthentication.rewrites.xhr - before, progressEvents, id };
            }
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("error").ValueKind, Is.EqualTo(JsonValueKind.Null), result.GetProperty("error").ToString());
            Assert.That(result.GetProperty("xhrRewrites").GetInt32(), Is.EqualTo(1));
            Assert.That(result.GetProperty("progressEvents").GetInt32(), Is.GreaterThan(0));
        }
    }

    [Test]
    public async Task Server_events_are_delivered_over_signalr()
    {
        await _session.Page.GotoAsync("/umbraco/section/settings/dashboard/windows-authentication");
        ILocator dashboard = _session.Page.Locator("windows-authentication-diagnostics");
        await dashboard.GetByText("Connected", new LocatorGetByTextOptions { Exact = true }).WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        JsonElement result = await dashboard.EvaluateAsync<JsonElement>("""
            async element => {
                const { UMB_MANAGEMENT_API_SERVER_EVENT_CONTEXT } = await import('@umbraco-cms/backoffice/management-api');
                const { umbHttpClient } = await import('@umbraco-cms/backoffice/http-client');
                const context = await new Promise(resolve => element.consumeContext(UMB_MANAGEMENT_API_SERVER_EVENT_CONTEXT, resolve));
                const received = new Promise(resolve => {
                    const subscription = context.events.subscribe(event => { subscription.unsubscribe(); resolve(event); });
                    setTimeout(() => resolve(null), 20000);
                });
                const { response } = await umbHttpClient.post({
                    url: '/umbraco/management/api/v1/dictionary',
                    body: { name: `signalr-test-${crypto.randomUUID()}`, parent: null, translations: [] },
                    headers: { 'Content-Type': 'application/json' },
                    security: [{ scheme: 'bearer', type: 'http' }],
                });
                const event = await received;
                return { createStatus: response.status, eventSource: event?.eventSource ?? null, eventType: event?.eventType ?? null };
            }
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.GetProperty("createStatus").GetInt32(), Is.EqualTo(201));
            Assert.That(result.GetProperty("eventSource").GetString(), Is.Not.Null.And.Contains("Dictionary"));
            Assert.That(_session.Network.WebSockets.Where(s => s.Url.Contains("serverEventHub")), Has.Some.Matches<NetworkLog.WebSocketEntry>(s => s.Error is null && s.FramesReceived > 0));
        }
    }

    [Test]
    public async Task Session_refresh_succeeds()
    {
        await _session.Page.GotoAsync("/umbraco/section/settings/dashboard/windows-authentication");
        ILocator dashboard = _session.Page.Locator("windows-authentication-diagnostics");
        await dashboard.WaitForAsync();

        bool refreshed = await dashboard.EvaluateAsync<bool>("""
            async element => {
                const { UMB_AUTH_CONTEXT } = await import('@umbraco-cms/backoffice/auth');
                const context = await new Promise(resolve => element.consumeContext(UMB_AUTH_CONTEXT, resolve));
                return await context.validateToken();
            }
            """);

        Assert.That(refreshed, Is.True);
    }
}
