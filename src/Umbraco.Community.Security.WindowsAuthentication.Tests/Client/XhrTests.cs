namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Client;

[TestFixture]
public class XhrTests : ClientScriptBrowserTest
{
    [TestCase("/umbraco/a", "Authorization", "Bearer abc", TestName = "Absolute path")]
    [TestCase("a", "Authorization", "Bearer abc", TestName = "Relative URL resolved against the base href")]
    [TestCase("https://backoffice.test/umbraco/a", "authorization", "bearer abc", TestName = "Lowercase header and scheme")]
    public async Task Moves_a_backoffice_bearer_header_into_the_backoffice_header(string url, string headerName, string value)
    {
        await OpenBackofficeAsync();

        await SendXhrAsync($"xhr.open('GET', '{url}'); xhr.setRequestHeader('{headerName}', '{value}'); xhr.setRequestHeader('X-Test', '1');");

        RecordedRequest request = SingleRequest("/umbraco/a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Header("x-umb-authorization"), Is.EqualTo(value));
            Assert.That(request.Header("authorization"), Is.Null);
            Assert.That(request.Header("x-test"), Is.EqualTo("1"));
            Assert.That(await RewriteCountsAsync(), Is.EqualTo((0, 1)));
        }
    }

    [TestCase("https://other.test/a", "Bearer abc", OtherOrigin, "/a", TestName = "Bearer to another origin")]
    [TestCase("/umbraco/a", "Basic dXNlcjpwYXNz", Origin, "/umbraco/a", TestName = "Basic credentials")]
    [TestCase("/umbraco/a", "Bearer", Origin, "/umbraco/a", TestName = "Bearer without a token")]
    public async Task Passes_other_authorization_headers_through_untouched(string url, string value, string origin, string path)
    {
        await OpenBackofficeAsync();

        await SendXhrAsync($"xhr.open('GET', '{url}'); xhr.setRequestHeader('Authorization', '{value}');");

        RecordedRequest request = SingleRequest(origin, path);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Header("authorization"), Is.EqualTo(value));
            Assert.That(request.Header("x-umb-authorization"), Is.Null);
            Assert.That(await RewriteCountsAsync(), Is.EqualTo((0, 0)));
        }
    }

    [Test]
    public async Task Uses_the_url_of_the_latest_open_call()
    {
        await OpenBackofficeAsync();

        await SendXhrAsync("xhr.open('GET', 'https://other.test/first'); xhr.open('GET', '/umbraco/second'); xhr.setRequestHeader('Authorization', 'Bearer abc');");
        await SendXhrAsync("xhr.open('GET', '/umbraco/first'); xhr.open('GET', 'https://other.test/second'); xhr.setRequestHeader('Authorization', 'Bearer abc');");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(SingleRequest(Origin, "/umbraco/second").Header("x-umb-authorization"), Is.EqualTo("Bearer abc"));
            Assert.That(SingleRequest(OtherOrigin, "/second").Header("authorization"), Is.EqualTo("Bearer abc"));
        }
    }

    [Test]
    public async Task Treats_the_umb_app_server_url_origin_as_backoffice()
    {
        await OpenBackofficeAsync(serverUrl: ServerOrigin);

        await SendXhrAsync("xhr.open('GET', 'https://api.test/umbraco/a'); xhr.setRequestHeader('Authorization', 'Bearer abc');");

        Assert.That(SingleRequest(ServerOrigin, "/umbraco/a").Header("x-umb-authorization"), Is.EqualTo("Bearer abc"));
    }

    [Test]
    public async Task Passes_open_arguments_through_exactly()
    {
        await OpenBackofficeAsync();

        int[] argumentCounts = await Page.EvaluateAsync<int[]>("""
            () => {
                window.__openCalls.length = 0;
                new XMLHttpRequest().open('GET', '/umbraco/a');
                new XMLHttpRequest().open('GET', '/umbraco/a', false);
                new XMLHttpRequest().open('GET', 'https://other.test/a', true, 'user', 'pass');
                return window.__openCalls.map(args => args.length);
            }
            """);

        // open(method, url, undefined) would make the request synchronous, so the argument count matters.
        Assert.That(argumentCounts, Is.EqualTo(new[] { 2, 3, 5 }));
    }

    [Test]
    public async Task Invalid_url_still_throws_from_open()
    {
        await OpenBackofficeAsync();

        string errorName = await Page.EvaluateAsync<string>("""
            () => {
                try {
                    new XMLHttpRequest().open('GET', 'http://[');
                    return 'no error';
                } catch (error) {
                    return error.name;
                }
            }
            """);

        Assert.That(errorName, Is.EqualTo("SyntaxError"));
    }

    [Test]
    public async Task Other_headers_use_the_native_setRequestHeader()
    {
        await OpenBackofficeAsync();

        await SendXhrAsync("xhr.open('POST', '/umbraco/a'); xhr.setRequestHeader('Content-Type', 'application/json'); xhr.setRequestHeader('X-Test', '2');", body: "'{\"a\":1}'");

        RecordedRequest request = SingleRequest("/umbraco/a");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Header("content-type"), Is.EqualTo("application/json"));
            Assert.That(request.Header("x-test"), Is.EqualTo("2"));
            Assert.That(request.BodyText, Is.EqualTo("{\"a\":1}"));
            Assert.That(request.Header("authorization"), Is.Null);
            Assert.That(request.Header("x-umb-authorization"), Is.Null);
        }
    }

    [Test]
    public async Task Restores_a_401_the_server_sent_as_403_to_keep_iis_from_challenging()
    {
        await OpenBackofficeAsync();

        string[] result = await Page.EvaluateAsync<string[]>("""
            () => new Promise(resolve => {
                const xhr = new XMLHttpRequest();
                const before = xhr.status;
                xhr.open('GET', '/umbraco/unauthorized');
                xhr.setRequestHeader('Authorization', 'Bearer abc');
                xhr.onloadend = () => resolve([
                    String(before),
                    String(xhr.status),
                    xhr.statusText,
                    String(xhr.status),
                    xhr.responseText,
                    String(window.__umbWindowsAuthentication.unauthorizedRestored),
                ]);
                xhr.send();
            })
            """);

        // Status is read twice on purpose: the restore is counted once per request.
        Assert.That(result, Is.EqualTo(new[] { "0", "401", "Unauthorized", "401", "denied", "1" }));
    }

    [TestCase("/umbraco/forbidden", "Bearer abc", TestName = "A real 403 to a rewritten request")]
    [TestCase("/umbraco/unauthorized", "Basic abc", TestName = "Status marker on a request that was not rewritten")]
    public async Task Leaves_other_403_responses_alone(string url, string authorization)
    {
        await OpenBackofficeAsync();

        string[] result = await Page.EvaluateAsync<string[]>($$"""
            () => new Promise(resolve => {
                const xhr = new XMLHttpRequest();
                xhr.open('GET', '{{url}}');
                xhr.setRequestHeader('Authorization', '{{authorization}}');
                xhr.onloadend = () => resolve([String(xhr.status), xhr.statusText, String(window.__umbWindowsAuthentication.unauthorizedRestored)]);
                xhr.send();
            })
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result[0], Is.EqualTo("403"));
            Assert.That(result[1], Is.Not.EqualTo("Unauthorized"));
            Assert.That(result[2], Is.EqualTo("0"));
        }
    }

    [Test]
    public async Task Reopening_a_request_forgets_that_it_was_rewritten()
    {
        await OpenBackofficeAsync();

        int status = await Page.EvaluateAsync<int>("""
            () => new Promise(resolve => {
                const xhr = new XMLHttpRequest();
                xhr.open('GET', '/umbraco/forbidden');
                xhr.setRequestHeader('Authorization', 'Bearer abc');
                xhr.open('GET', '/umbraco/unauthorized');
                xhr.onloadend = () => resolve(xhr.status);
                xhr.send();
            })
            """);

        Assert.That(status, Is.EqualTo(403));
    }

    private Task SendXhrAsync(string setup, string body = "null")
        => Page.EvaluateAsync($$"""
            () => new Promise(resolve => {
                const xhr = new XMLHttpRequest();
                {{setup}}
                xhr.withCredentials = true;
                xhr.onloadend = () => resolve(xhr.status);
                xhr.send({{body}});
            })
            """);
}
