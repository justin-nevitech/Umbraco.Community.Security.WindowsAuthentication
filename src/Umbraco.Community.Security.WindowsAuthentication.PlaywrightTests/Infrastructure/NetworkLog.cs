using System.Collections.Concurrent;
using Microsoft.Playwright;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

/// <summary>
/// Watches everything a page sends to and receives from the test site: bearer headers that reached the wire, failed
/// responses, script errors and WebSockets.
/// </summary>
public sealed class NetworkLog
{
    private readonly string _origin;

    public NetworkLog(Uri baseUrl) => _origin = baseUrl.GetLeftPart(UriPartial.Authority);

    public ConcurrentQueue<string> BearerRequests { get; } = new();

    public ConcurrentQueue<string> RelayedRequests { get; } = new();

    public ConcurrentQueue<(int Status, string Method, string Url)> FailedResponses { get; } = new();

    public ConcurrentQueue<string> ConsoleErrors { get; } = new();

    public ConcurrentQueue<string> PageErrors { get; } = new();

    public ConcurrentQueue<WebSocketEntry> WebSockets { get; } = new();

    public ConcurrentQueue<string> RequestedUrls { get; } = new();

    public void Watch(IPage page)
    {
        page.Request += async (_, request) =>
        {
            if (request.Url.StartsWith(_origin, StringComparison.OrdinalIgnoreCase) is false)
            {
                return;
            }

            RequestedUrls.Enqueue(request.Url);
            Dictionary<string, string> headers;
            try
            {
                headers = await request.AllHeadersAsync();
            }
            catch (PlaywrightException)
            {
                headers = request.Headers;
            }

            if (headers.TryGetValue("authorization", out string? authorization) && authorization.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase))
            {
                BearerRequests.Enqueue($"{request.Method} {request.Url}");
            }

            if (headers.ContainsKey("x-umb-authorization"))
            {
                RelayedRequests.Enqueue($"{request.Method} {request.Url}");
            }
        };

        page.Response += (_, response) =>
        {
            if (response.Status >= 400 && response.Url.StartsWith(_origin, StringComparison.OrdinalIgnoreCase))
            {
                FailedResponses.Enqueue((response.Status, response.Request.Method, response.Url));
            }
        };

        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                ConsoleErrors.Enqueue(message.Text);
            }
        };

        page.PageError += (_, error) => PageErrors.Enqueue(error);

        page.WebSocket += (_, socket) =>
        {
            var entry = new WebSocketEntry(socket.Url);
            WebSockets.Enqueue(entry);
            socket.FrameReceived += (_, _) => entry.FramesReceived++;
            socket.SocketError += (_, error) => entry.Error = error;
        };
    }

    /// <summary>
    /// Failed responses that point at a real problem. On first load the backoffice always asks the token endpoint to refresh a
    /// session that doesn't exist yet, which is answered with 400 whether or not the package is installed.
    /// </summary>
    public IEnumerable<string> UnexpectedFailures()
        => FailedResponses
            .Where(r => (r.Status == 400 && r.Url.Contains("/security/back-office/token", StringComparison.OrdinalIgnoreCase)) is false)
            .Select(r => $"{r.Status} {r.Method} {r.Url}");

    public sealed class WebSocketEntry(string url)
    {
        public string Url { get; } = url;

        public int FramesReceived { get; set; }

        public string? Error { get; set; }
    }
}
