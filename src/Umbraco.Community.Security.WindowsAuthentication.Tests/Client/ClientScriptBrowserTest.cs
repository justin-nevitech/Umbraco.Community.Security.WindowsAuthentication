using System.Collections.Concurrent;
using System.Text;
using Microsoft.Playwright;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Client;

/// <summary>
/// Loads the compiled client script into a page on a fake backoffice origin. Every request the page makes is intercepted and
/// recorded, so tests see exactly what would have gone over the wire. Before the client script loads, the page wraps the native
/// <c>fetch</c> and <c>XMLHttpRequest.open</c> with recorders, so tests can also see exactly what the client script passed on.
/// </summary>
[Category("Browser")]
public abstract class ClientScriptBrowserTest
{
    protected const string Origin = "https://backoffice.test";
    protected const string OtherOrigin = "https://other.test";
    protected const string ServerOrigin = "https://api.test";

    private static readonly Lazy<string> ClientScriptSource = new(() => File.ReadAllText(RepositoryPaths.ClientScript));

    private static readonly Dictionary<string, string> CorsHeaders = new()
    {
        ["Access-Control-Allow-Origin"] = Origin,
        ["Access-Control-Allow-Credentials"] = "true",
        ["Access-Control-Allow-Headers"] = "authorization, x-umb-authorization, content-type, x-test",
        ["Access-Control-Allow-Methods"] = "GET, POST, PUT, PATCH, DELETE",
        ["Access-Control-Expose-Headers"] = "x-echo, x-umb-authorization-status, www-authenticate",
    };

    private IBrowserContext _context = null!;
    private ICDPSession _coverage = null!;
    private string _html = string.Empty;

    protected IPage Page { get; private set; } = null!;

    protected ConcurrentQueue<RecordedRequest> Requests { get; private set; } = new();

    protected ConcurrentQueue<IConsoleMessage> ConsoleMessages { get; private set; } = new();

    [SetUp]
    public async Task CreatePage()
    {
        Requests = new ConcurrentQueue<RecordedRequest>();
        ConsoleMessages = new ConcurrentQueue<IConsoleMessage>();

        _context = await BrowserHost.Browser.NewContextAsync();
        await _context.RouteAsync("**/*", HandleRouteAsync);
        Page = await _context.NewPageAsync();
        Page.Console += (_, message) => ConsoleMessages.Enqueue(message);
        _coverage = await JsCoverage.StartAsync(_context, Page);
    }

    [TearDown]
    public async Task ClosePage()
    {
        await JsCoverage.CollectAsync(_coverage, ClientScriptSource.Value, $"{GetType().Name}.{TestContext.CurrentContext.Test.MethodName}");
        await _context.CloseAsync();
    }

    /// <param name="serverUrl"><c>null</c> for no umb-app element, empty for one without a server-url attribute.</param>
    /// <param name="beforeScript">Script that runs before the client script loads.</param>
    protected async Task OpenBackofficeAsync(string? serverUrl = null, string beforeScript = "")
    {
        string umbApp = serverUrl switch
        {
            null => string.Empty,
            "" => "<umb-app></umb-app>",
            _ => $"<umb-app server-url=\"{serverUrl}\"></umb-app>",
        };

        _html = $$"""
            <!doctype html>
            <html>
            <head>
            <base href="/umbraco/">
            <script>
                window.__fetchCalls = [];
                const nativeFetch = window.fetch;
                window.__recordingFetch = function (...args) {
                    window.__fetchCalls.push(args);
                    return nativeFetch.apply(this, args);
                };
                window.fetch = window.__recordingFetch;

                window.__openCalls = [];
                const nativeOpen = XMLHttpRequest.prototype.open;
                XMLHttpRequest.prototype.open = function (...args) {
                    window.__openCalls.push(args);
                    return nativeOpen.apply(this, args);
                };
                window.__recordingOpen = XMLHttpRequest.prototype.open;
                window.__nativeSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;

                {{beforeScript}}
            </script>
            <script type="module" src="/windows-authentication.js"></script>
            </head>
            <body>{{umbApp}}</body>
            </html>
            """;

        await Page.GotoAsync($"{Origin}/umbraco/");
    }

    protected RecordedRequest SingleRequest(string origin, string path)
    {
        RecordedRequest[] matches = Requests.Where(r => r.Origin == origin && r.Url.AbsolutePath == path).ToArray();
        Assert.That(matches, Has.Length.EqualTo(1), $"Expected one {origin}{path} request, saw: {string.Join(", ", Requests.Select(r => r.Method + " " + r.Url))}");
        return matches[0];
    }

    protected RecordedRequest SingleRequest(string path) => SingleRequest(Origin, path);

    protected async Task<(int Fetch, int Xhr)> RewriteCountsAsync()
    {
        int[] counts = await Page.EvaluateAsync<int[]>("() => [window.__umbWindowsAuthentication.rewrites.fetch, window.__umbWindowsAuthentication.rewrites.xhr]");
        return (counts[0], counts[1]);
    }

    private async Task HandleRouteAsync(IRoute route)
    {
        IRequest request = route.Request;
        var url = new Uri(request.Url);
        string origin = url.GetLeftPart(UriPartial.Authority);

        if (request.Method == "OPTIONS")
        {
            await route.FulfillAsync(new RouteFulfillOptions { Status = 204, Headers = CorsHeaders });
            return;
        }

        if (origin == Origin && url.AbsolutePath == "/umbraco/")
        {
            await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/html", Body = _html });
            return;
        }

        if (origin == Origin && url.AbsolutePath == "/windows-authentication.js")
        {
            await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "text/javascript", Body = ClientScriptSource.Value });
            return;
        }

        Requests.Enqueue(new RecordedRequest(request.Method, url, await request.AllHeadersAsync(), request.PostDataBuffer));

        if (url.AbsolutePath == "/umbraco/unauthorized")
        {
            // What the server sends for a 401 to a relayed request.
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 403,
                Headers = new Dictionary<string, string>(CorsHeaders)
                {
                    ["X-Umb-Authorization-Status"] = "401",
                    ["WWW-Authenticate"] = "Bearer error=\"invalid_token\"",
                    ["Content-Type"] = "text/plain",
                },
                Body = "denied",
            });
            return;
        }

        if (url.AbsolutePath == "/umbraco/forbidden")
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 403,
                Headers = new Dictionary<string, string>(CorsHeaders) { ["Content-Type"] = "text/plain" },
                Body = "forbidden",
            });
            return;
        }

        if (url.AbsolutePath == "/umbraco/response")
        {
            await route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 201,
                Headers = new Dictionary<string, string>(CorsHeaders) { ["X-Echo"] = "yes", ["Content-Type"] = "text/plain" },
                Body = "hello",
            });
            return;
        }

        await route.FulfillAsync(new RouteFulfillOptions { Status = 200, ContentType = "application/json", Headers = CorsHeaders, Body = """{"ok":true}""" });
    }
}

public sealed record RecordedRequest(string Method, Uri Url, IReadOnlyDictionary<string, string> Headers, byte[]? Body)
{
    public string Origin => Url.GetLeftPart(UriPartial.Authority);

    public string BodyText => Body is null ? string.Empty : Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name.ToLowerInvariant(), out string? value) ? value : null;
}
