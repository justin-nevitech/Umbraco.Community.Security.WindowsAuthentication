using System.Text.Json;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Backoffice;

/// <summary>
/// Calls every Management API GET endpoint that needs no parameters through the signed-in backoffice, the same way the
/// backoffice and packages do. None may be refused, and the anonymous control pass shows the endpoints really need auth.
/// </summary>
public class ManagementApiSweepTests(Hosting hosting) : PlaywrightTest(hosting)
{
    // Endpoints that talk to the outside world, change install state or drive the sign-in flow itself.
    private static readonly string[] Excluded = ["/security/", "/install/", "/upgrade/", "/news-dashboard", "/help", "/oembed"];

    private const string SweepScript = """
        async ({ paths, authorize }) => {
            const { umbHttpClient } = await import('@umbraco-cms/backoffice/http-client');
            const token = await umbHttpClient.getConfig().auth();
            const results = [];
            for (const path of paths) {
                const response = await fetch(path, {
                    headers: authorize ? { Authorization: `Bearer ${token}` } : {},
                    credentials: authorize ? 'include' : 'omit',
                });
                results.push({ path, status: response.status });
            }
            return results;
        }
        """;

    private BackofficeSession _session = null!;
    private string[] _paths = [];

    [OneTimeSetUp]
    public async Task SignInAndReadOpenApi()
    {
        _session = await BackofficeSession.SignInAsync(Site, await NewBrowserContextAsync());

        using HttpClient client = Site.CreateHttpClient();
#if UMBRACO_18
        // Umbraco 18 replaced Swashbuckle with Microsoft.AspNetCore.OpenApi, which serves the document from a different URL.
        const string openApiDocument = "umbraco/openapi/management.json";
#else
        const string openApiDocument = "umbraco/swagger/management/swagger.json";
#endif
        using JsonDocument openApi = JsonDocument.Parse(await client.GetStringAsync(openApiDocument));

        _paths = openApi.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Value.TryGetProperty("get", out JsonElement get) && RequiresNoParameters(get))
            .Select(path => path.Name)
            .Where(path => Excluded.Any(path.Contains) is false)
            .Order()
            .ToArray();
    }

    [OneTimeTearDown]
    public async Task SignOut() => await _session.DisposeAsync();

    [Test]
    public async Task Every_parameterless_get_endpoint_is_authorised_through_the_backoffice()
    {
        Assume.That(_paths, Has.Length.GreaterThan(50), "Expected the Management API OpenAPI document to list many endpoints");

        (string Path, int Status)[] authorised = await SweepAsync(authorize: true);
        (string Path, int Status)[] anonymous = await SweepAsync(authorize: false);

        TestContext.Out.WriteLine(string.Join(Environment.NewLine, authorised.Select(r => $"{r.Status} {r.Path}")));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(authorised.Where(r => r.Status is 401 or 403 || r.Status >= 500).Select(r => $"{r.Status} {r.Path}"), Is.Empty);
            Assert.That(anonymous.Count(r => r.Status == 401), Is.GreaterThan(_paths.Length / 2), "Most endpoints must refuse anonymous callers, or the sweep proves nothing");
            Assert.That(_session.Network.BearerRequests, Is.Empty);
        }
    }

    private async Task<(string Path, int Status)[]> SweepAsync(bool authorize)
    {
        JsonElement results = await _session.EvaluateAsync(SweepScript, new { paths = _paths, authorize });
        return results.EnumerateArray().Select(r => (r.GetProperty("path").GetString()!, r.GetProperty("status").GetInt32())).ToArray();
    }

    private static bool RequiresNoParameters(JsonElement operation)
        => operation.TryGetProperty("parameters", out JsonElement parameters) is false
           || parameters.EnumerateArray().All(p => p.GetProperty("in").GetString() != "path" && (p.TryGetProperty("required", out JsonElement required) is false || required.GetBoolean() is false));
}
