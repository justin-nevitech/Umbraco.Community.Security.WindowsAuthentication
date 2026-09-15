using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Server;

[TestFixture]
public class WindowsAuthenticationMiddlewareTests
{
    [Test]
    public async Task Restores_the_header_before_calling_the_next_middleware()
    {
        HttpContext context = WindowsAuthenticationHeadersTests.CreateContext((WindowsAuthenticationDefaults.HeaderName, "Bearer abc"));
        string? seenAuthorization = null;
        var calls = 0;
        var middleware = new WindowsAuthenticationMiddleware(
            ctx =>
            {
                calls++;
                seenAuthorization = ctx.Request.Headers.Authorization;
                return Task.CompletedTask;
            },
            new ListLogger());

        await middleware.InvokeAsync(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(seenAuthorization, Is.EqualTo("Bearer abc"));
        }
    }

    [TestCase(null, 0)]
    [TestCase("Bearer abc", 1)]
    [TestCase("Basic abc", 1)]
    public async Task Always_calls_next_once_with_the_same_context_and_only_logs_backoffice_header_requests(string? headerValue, int expectedLogEntries)
    {
        HttpContext context = headerValue is null
            ? WindowsAuthenticationHeadersTests.CreateContext(("Authorization", "Bearer front-end"))
            : WindowsAuthenticationHeadersTests.CreateContext((WindowsAuthenticationDefaults.HeaderName, headerValue));
        var logger = new ListLogger();
        var contexts = new List<HttpContext>();
        var middleware = new WindowsAuthenticationMiddleware(
            ctx =>
            {
                contexts.Add(ctx);
                return Task.CompletedTask;
            },
            logger);

        await middleware.InvokeAsync(context);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(contexts, Is.EqualTo(new[] { context }));
            Assert.That(logger.Entries, Has.Count.EqualTo(expectedLogEntries));
            Assert.That(logger.Entries.All(e => e.Level == LogLevel.Debug), Is.True);
        }
    }

    [Test]
    public void Returns_the_task_from_next()
    {
        var tcs = new TaskCompletionSource();
        var middleware = new WindowsAuthenticationMiddleware(_ => tcs.Task, new ListLogger());

        Task task = middleware.InvokeAsync(new DefaultHttpContext());

        Assert.That(task, Is.SameAs(tcs.Task));
    }

    [TestCase("NTLM", 1, 400)]
    [TestCase(null, 0, 401)]
    public async Task Hides_a_failed_sign_in_only_behind_windows_authentication_and_logs_it(string? authType, int expectedLogEntries, int expectedStatus)
    {
        HttpContext context = WindowsAuthenticationHeadersTests.CreateContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = WindowsAuthenticationDefaults.BackOfficeLoginPath;
        if (authType is not null)
        {
            WindowsAuthenticationSignInTests.SetAuthType(context, authType);
        }

        var logger = new ListLogger();
        var calls = 0;
        var middleware = new WindowsAuthenticationMiddleware(
            _ =>
            {
                calls++;
                return Task.CompletedTask;
            },
            logger);

        await middleware.InvokeAsync(context);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ((WindowsAuthenticationHeadersTests.StartingResponseFeature)context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseFeature>()!).StartAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(context.Response.StatusCode, Is.EqualTo(expectedStatus));
            Assert.That(logger.Entries, Has.Count.EqualTo(expectedLogEntries));
            Assert.That(logger.Entries.All(e => e.Level == LogLevel.Debug), Is.True);
        }
    }

    private sealed class ListLogger : ILogger<WindowsAuthenticationMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
