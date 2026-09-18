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

            // Headers set by script are already known when the request starts, so record them straight away. Waiting for
            // AllHeadersAsync first can leave a request unrecorded when a test aborts it and asserts before the call returns.
            if (Record(request, request.Headers))
            {
                return;
            }

            try
            {
                Record(request, await request.AllHeadersAsync());
            }
            catch (PlaywrightException)
            {
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

    /// <summary>Queues the request under whichever of the two headers it carries; true when it carried either.</summary>
    private bool Record(IRequest request, Dictionary<string, string> headers)
    {
        bool bearer = headers.TryGetValue("authorization", out string? authorization) && authorization.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase);
        bool relayed = headers.ContainsKey("x-umb-authorization");

        if (bearer)
        {
            BearerRequests.Enqueue($"{request.Method} {request.Url}");
        }

        if (relayed)
        {
            RelayedRequests.Enqueue($"{request.Method} {request.Url}");
        }

        return bearer || relayed;
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
