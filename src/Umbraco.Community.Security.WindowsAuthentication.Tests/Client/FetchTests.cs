using Microsoft.Playwright;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Client;

[TestFixture]
public class FetchTests : ClientScriptBrowserTest
{
    [TestCase("fetch('/umbraco/a', { headers: { Authorization: 'Bearer abc' } })", "Bearer abc", TestName = "Object headers")]
    [TestCase("fetch('/umbraco/a', { headers: { authorization: 'Bearer abc' } })", "Bearer abc", TestName = "Lowercase header name")]
    [TestCase("fetch('/umbraco/a', { headers: { Authorization: 'bearer abc' } })", "bearer abc", TestName = "Lowercase scheme")]
    [TestCase("fetch('/umbraco/a', { headers: new Headers({ Authorization: 'Bearer abc' }) })", "Bearer abc", TestName = "Headers instance")]
    [TestCase("fetch('/umbraco/a', { headers: [['Authorization', 'Bearer abc']] })", "Bearer abc", TestName = "Header tuples")]
    [TestCase("fetch(new URL('/umbraco/a', location.href), { headers: { Authorization: 'Bearer abc' } })", "Bearer abc", TestName = "URL object")]
    [TestCase("fetch('a', { headers: { Authorization: 'Bearer abc' } })", "Bearer abc", TestName = "Relative URL resolved against the base href")]
    [TestCase("fetch('https://backoffice.test/umbraco/a', { headers: { Authorization: 'Bearer abc' } })", "Bearer abc", TestName = "Absolute same-origin URL")]
    [TestCase("fetch(new Request('/umbraco/a', { headers: { Authorization: 'Bearer abc' } }))", "Bearer abc", TestName = "Request with a bearer header")]
    [TestCase("fetch(new Request('/umbraco/a'), { headers: { Authorization: 'Bearer abc' } })", "Bearer abc", TestName = "Request with bearer in init")]
    [TestCase("fetch('/umbraco/a', { headers: { Authorization: 'Bearer [redacted]' } })", "Bearer [redacted]", TestName = "Umbraco 17 redacted placeholder")]
    public async Task Moves_a_backoffice_bearer_header_into_the_backoffice_header(string call, string expectedValue)
    {
        await OpenBackofficeAsync();

        await Page.EvaluateAsync($"() => {call}.then(r => r.status)");

        RecordedRequest request = SingleRequest("/umbraco/a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Header("x-umb-authorization"), Is.EqualTo(expectedValue));
            Assert.That(request.Header("authorization"), Is.Null);
            Assert.That(await RewriteCountsAsync(), Is.EqualTo((1, 0)));
        }
    }

    [Test]
    public async Task Keeps_every_other_header()
    {
        await OpenBackofficeAsync();

        await Page.EvaluateAsync("() => fetch('/umbraco/a', { headers: { Authorization: 'Bearer abc', 'X-Test': '1', 'Accept-Language': 'da' } }).then(r => r.status)");

        RecordedRequest request = SingleRequest("/umbraco/a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Header("x-test"), Is.EqualTo("1"));
            Assert.That(request.Header("accept-language"), Is.EqualTo("da"));
        }
    }

    [TestCase("'/umbraco/a'", "{ headers: { 'X-Test': '1' } }", Origin, null, TestName = "No Authorization header")]
    [TestCase("'/umbraco/a'", "undefined", Origin, null, TestName = "No init")]
    [TestCase("'/umbraco/a'", "{ headers: { Authorization: 'Basic dXNlcjpwYXNz' } }", Origin, "Basic dXNlcjpwYXNz", TestName = "Basic credentials")]
    [TestCase("'/umbraco/a'", "{ headers: { Authorization: 'ApiKey abc' } }", Origin, "ApiKey abc", TestName = "Custom scheme")]
    [TestCase("'/umbraco/a'", "{ headers: { Authorization: 'Bearer' } }", Origin, "Bearer", TestName = "Bearer without a token")]
    [TestCase("'/umbraco/a'", "{ headers: { Authorization: 'Bearer   ' } }", Origin, "Bearer", TestName = "Bearer with only whitespace")]
    [TestCase("'https://other.test/a'", "{ headers: { Authorization: 'Bearer abc' } }", OtherOrigin, "Bearer abc", TestName = "Bearer to another origin")]
    [TestCase("new Request('/umbraco/a', { headers: { Authorization: 'Bearer abc' } })", "{ headers: { 'X-Test': '1' } }", Origin, null, TestName = "init headers replace a Request's bearer header")]
    public async Task Passes_other_requests_to_fetch_untouched(string input, string init, string origin, string? expectedAuthorization)
    {
        await OpenBackofficeAsync();

        bool sameArguments = await Page.EvaluateAsync<bool>($$"""
            async () => {
                const input = {{input}};
                const init = {{init}};
                window.__fetchCalls.length = 0;
                await fetch(input, init);
                const call = window.__fetchCalls[0];
                return window.__fetchCalls.length === 1 && call.length === 2 && call[0] === input && call[1] === init;
            }
            """);

        RecordedRequest request = SingleRequest(origin, "/" + (origin == Origin ? "umbraco/a" : "a"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(sameArguments, Is.True, "The client script must pass the original input and init objects to fetch");
            Assert.That(request.Header("authorization"), Is.EqualTo(expectedAuthorization));
            Assert.That(request.Header("x-umb-authorization"), Is.Null);
            Assert.That(await RewriteCountsAsync(), Is.EqualTo((0, 0)));
        }
    }

    [TestCase("'{\"a\":1}'", "text/plain;charset=UTF-8", "{\"a\":1}", TestName = "String body")]
    [TestCase("new URLSearchParams({ a: '1', b: '2' })", "application/x-www-form-urlencoded;charset=UTF-8", "a=1&b=2", TestName = "URLSearchParams body")]
    [TestCase("new Blob(['blob body'], { type: 'text/plain' })", "text/plain", "blob body", TestName = "Blob body")]
    [TestCase("new TextEncoder().encode('bytes body')", null, "bytes body", TestName = "Binary body")]
    [TestCase("(() => { const form = new FormData(); form.append('file', new Blob(['file body']), 'test.txt'); return form; })()", "multipart/form-data; boundary=", "file body", TestName = "FormData body")]
    public async Task Preserves_the_body_of_a_rewritten_request(string body, string? contentTypePrefix, string expectedBodyFragment)
    {
        await OpenBackofficeAsync();

        await Page.EvaluateAsync($"() => fetch('/umbraco/a', {{ method: 'POST', body: {body}, headers: {{ Authorization: 'Bearer abc' }} }}).then(r => r.status)");

        RecordedRequest request = SingleRequest("/umbraco/a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Method, Is.EqualTo("POST"));
            Assert.That(request.Header("x-umb-authorization"), Is.EqualTo("Bearer abc"));
            Assert.That(request.BodyText, Does.Contain(expectedBodyFragment));
            if (contentTypePrefix is null)
            {
                Assert.That(request.Header("content-type"), Is.Null);
            }
            else
            {
                Assert.That(request.Header("content-type"), Does.StartWith(contentTypePrefix));
            }
        }
    }

    [TestCase("fetch('/umbraco/a', { method: 'PUT', body: 'put body', headers: { Authorization: 'Bearer abc' } })", "PUT", "put body")]
    [TestCase("fetch('/umbraco/a', { method: 'PATCH', body: 'patch body', headers: { Authorization: 'Bearer abc' } })", "PATCH", "patch body")]
    [TestCase("fetch('/umbraco/a', { method: 'DELETE', headers: { Authorization: 'Bearer abc' } })", "DELETE", "")]
    [TestCase("fetch(new Request('/umbraco/a', { method: 'PUT', body: 'request body', headers: { Authorization: 'Bearer abc' } }))", "PUT", "request body")]
    public async Task Preserves_the_method_of_a_rewritten_request(string call, string expectedMethod, string expectedBody)
    {
        await OpenBackofficeAsync();

        await Page.EvaluateAsync($"() => {call}.then(r => r.status)");

        RecordedRequest request = SingleRequest("/umbraco/a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Method, Is.EqualTo(expectedMethod));
            Assert.That(request.BodyText, Is.EqualTo(expectedBody));
            Assert.That(request.Header("x-umb-authorization"), Is.EqualTo("Bearer abc"));
        }
    }

    [Test]
    public async Task Returns_the_response_unchanged()
    {
        await OpenBackofficeAsync();

        string[] response = await Page.EvaluateAsync<string[]>("""
            async () => {
                const response = await fetch('/umbraco/response', { headers: { Authorization: 'Bearer abc' } });
                return [String(response.status), response.headers.get('x-echo'), await response.text()];
            }
            """);

        Assert.That(response, Is.EqualTo(new[] { "201", "yes", "hello" }));
    }

    [Test]
    public async Task Honours_the_abort_signal_of_a_rewritten_request()
    {
        await OpenBackofficeAsync();

        string errorName = await Page.EvaluateAsync<string>("""
            async () => {
                const controller = new AbortController();
                controller.abort();
                try {
                    await fetch('/umbraco/a', { signal: controller.signal, headers: { Authorization: 'Bearer abc' } });
                    return 'not aborted';
                } catch (error) {
                    return error.name;
                }
            }
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(errorName, Is.EqualTo("AbortError"));
            Assert.That(Requests, Is.Empty);
        }
    }

    [Test]
    public async Task Passes_an_invalid_url_through_so_fetch_rejects_it_as_usual()
    {
        await OpenBackofficeAsync();

        string[] result = await Page.EvaluateAsync<string[]>("""
            async () => {
                const init = { headers: { Authorization: 'Bearer abc' } };
                window.__fetchCalls.length = 0;
                try {
                    await fetch('http://[', init);
                    return ['resolved'];
                } catch (error) {
                    const call = window.__fetchCalls[0];
                    return [error.name, String(call[0] === 'http://[' && call[1] === init)];
                }
            }
            """);

        Assert.That(result, Is.EqualTo(new[] { "TypeError", "true" }));
    }

    [Test]
    public async Task Treats_the_umb_app_server_url_origin_as_backoffice()
    {
        await OpenBackofficeAsync(serverUrl: ServerOrigin);

        await Page.EvaluateAsync("() => fetch('https://api.test/umbraco/a', { headers: { Authorization: 'Bearer abc' } }).then(r => r.status)");

        RecordedRequest request = SingleRequest(ServerOrigin, "/umbraco/a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Header("x-umb-authorization"), Is.EqualTo("Bearer abc"));
            Assert.That(request.Header("authorization"), Is.Null);
        }
    }

    [TestCase("", TestName = "umb-app without server-url")]
    [TestCase("http://[", TestName = "Invalid server-url")]
    public async Task Leaves_other_origins_alone_when_server_url_does_not_name_them(string serverUrl)
    {
        await OpenBackofficeAsync(serverUrl);

        await Page.EvaluateAsync("() => fetch('https://api.test/umbraco/a', { headers: { Authorization: 'Bearer abc' } }).then(r => r.status)");

        RecordedRequest request = SingleRequest(ServerOrigin, "/umbraco/a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Header("authorization"), Is.EqualTo("Bearer abc"));
            Assert.That(request.Header("x-umb-authorization"), Is.Null);
        }
    }

    [Test]
    public async Task Server_url_does_not_stop_same_origin_requests_being_rewritten()
    {
        await OpenBackofficeAsync(serverUrl: ServerOrigin);

        await Page.EvaluateAsync("() => fetch('/umbraco/a', { headers: { Authorization: 'Bearer abc' } }).then(r => r.status)");

        Assert.That(SingleRequest("/umbraco/a").Header("x-umb-authorization"), Is.EqualTo("Bearer abc"));
    }

    [Test]
    public async Task Restores_a_401_the_server_sent_as_403_to_keep_iis_from_challenging()
    {
        await OpenBackofficeAsync();

        string?[] response = await Page.EvaluateAsync<string?[]>("""
            async () => {
                const response = await fetch('/umbraco/unauthorized', { headers: { Authorization: 'Bearer abc' } });
                return [
                    String(response.status),
                    response.statusText,
                    String(response.ok),
                    response.headers.get('x-umb-authorization-status'),
                    response.headers.get('www-authenticate'),
                    response.url,
                    String(response.redirected),
                    await response.text(),
                    String(window.__umbWindowsAuthentication.unauthorizedRestored),
                ];
            }
            """);

        Assert.That(response, Is.EqualTo(new string?[]
        {
            "401", "Unauthorized", "false", null, "Bearer error=\"invalid_token\"", $"{Origin}/umbraco/unauthorized", "false", "denied", "1",
        }));
    }

    [TestCase("'/umbraco/forbidden'", "{ headers: { Authorization: 'Bearer abc' } }", TestName = "A real 403 to a rewritten request")]
    [TestCase("'/umbraco/unauthorized'", "{ headers: { 'X-Test': '1' } }", TestName = "Status marker on a request without Authorization")]
    [TestCase("'/umbraco/unauthorized'", "{ headers: { Authorization: 'Basic abc' } }", TestName = "Status marker on a request with other credentials")]
    [TestCase("'https://other.test/umbraco/unauthorized'", "{ headers: { Authorization: 'Bearer abc' } }", TestName = "Status marker from another origin")]
    public async Task Leaves_other_403_responses_alone(string input, string init)
    {
        await OpenBackofficeAsync();

        int[] result = await Page.EvaluateAsync<int[]>($$"""
            async () => {
                const response = await fetch({{input}}, {{init}});
                return [response.status, window.__umbWindowsAuthentication.unauthorizedRestored];
            }
            """);

        Assert.That(result, Is.EqualTo(new[] { 403, 0 }));
    }

    [Test]
    public async Task Restores_a_hidden_401_from_the_server_url_origin()
    {
        // A cross-origin Management API has to expose X-Umb-Authorization-Status through CORS for this to work.
        await OpenBackofficeAsync(serverUrl: ServerOrigin);

        int[] result = await Page.EvaluateAsync<int[]>("""
            async () => {
                const response = await fetch('https://api.test/umbraco/unauthorized', { headers: { Authorization: 'Bearer abc' } });
                return [response.status, window.__umbWindowsAuthentication.unauthorizedRestored];
            }
            """);

        Assert.That(result, Is.EqualTo(new[] { 401, 1 }));
    }
}
