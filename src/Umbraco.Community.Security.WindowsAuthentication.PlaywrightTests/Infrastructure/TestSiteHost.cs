using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

public enum Hosting
{
    /// <summary>Kestrel, no Windows authentication. The package must be harmless on an ordinary site.</summary>
    Kestrel,

    /// <summary>IIS Express, in-process, anonymous authentication off and Windows authentication on.</summary>
    IisExpressWindowsAuth,
}

/// <summary>
/// Runs the built test site as a separate process on a fresh SQLite database. Each host gets its own database, temp folder
/// (Examine indexes, uploads) and log folder, so hosts never share state with each other or with a developer's own run.
/// </summary>
public abstract class TestSiteHost : IAsyncDisposable
{
    public const string AdminEmail = "admin@example.com";
    public const string AdminPassword = "WindowsAuth-Test-1234";

    private Process? _process;

    protected TestSiteHost(Hosting hosting)
    {
        Hosting = hosting;
        RunDirectory = TestSitePaths.CreateRunDirectory(hosting.ToString());
    }

    public Hosting Hosting { get; }

    public Uri BaseUrl { get; protected set; } = null!;

    public string RunDirectory { get; }

    public bool UsesWindowsAuthentication => Hosting == Hosting.IisExpressWindowsAuth;

    public static TestSiteHost Create(Hosting hosting) => hosting switch
    {
        Hosting.Kestrel => new KestrelTestSiteHost(),
        Hosting.IisExpressWindowsAuth => new IisExpressTestSiteHost(),
        _ => throw new ArgumentOutOfRangeException(nameof(hosting)),
    };

    public async Task StartAsync()
    {
        if (File.Exists(TestSitePaths.SiteExecutable) is false)
        {
            throw new InvalidOperationException($"Build the test site first: {TestSitePaths.SiteExecutable} is missing.");
        }

        _process = Process.Start(CreateStartInfo()) ?? throw new InvalidOperationException("Failed to start the test site.");
        _process.OutputDataReceived += (_, e) => AppendLog(e.Data);
        _process.ErrorDataReceived += (_, e) => AppendLog(e.Data);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await WaitUntilRunningAsync(TimeSpan.FromMinutes(5));
    }

    public HttpClient CreateHttpClient(bool allowRedirects = false)
        => new(new HttpClientHandler
        {
            AllowAutoRedirect = allowRedirects,
            UseDefaultCredentials = UsesWindowsAuthentication,
            UseCookies = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        })
        {
            BaseAddress = BaseUrl,
            Timeout = TimeSpan.FromMinutes(2),
        };

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Settings shared by every hosting model: fresh database, isolated temp and log folders, Development environment.</summary>
    protected IReadOnlyDictionary<string, string> EnvironmentVariables => new Dictionary<string, string>
    {
        ["ASPNETCORE_ENVIRONMENT"] = "Development",
        ["ConnectionStrings__umbracoDbDSN"] = $"Data Source={Path.Combine(RunDirectory, "Umbraco.sqlite.db")};Cache=Shared;Foreign Keys=True;Pooling=True",
        ["ConnectionStrings__umbracoDbDSN_ProviderName"] = "Microsoft.Data.Sqlite",
        ["Umbraco__CMS__Hosting__LocalTempStorageLocation"] = "EnvironmentTemp",
        ["Umbraco__CMS__Logging__Directory"] = Path.Combine(RunDirectory, "logs"),
        ["Umbraco__CMS__Global__UmbracoMediaPhysicalRootPath"] = Directory.CreateDirectory(Path.Combine(RunDirectory, "media")).FullName,
        ["TMP"] = Directory.CreateDirectory(Path.Combine(RunDirectory, "temp")).FullName,
        ["TEMP"] = Path.Combine(RunDirectory, "temp"),
    };

    protected abstract ProcessStartInfo CreateStartInfo();

    protected static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void AppendLog(string? line)
    {
        if (line is not null)
        {
            File.AppendAllText(Path.Combine(RunDirectory, "host.log"), line + Environment.NewLine);
        }
    }

    private async Task WaitUntilRunningAsync(TimeSpan timeout)
    {
        using HttpClient client = CreateHttpClient();
        var stopwatch = Stopwatch.StartNew();
        string lastError = "no response";

        while (stopwatch.Elapsed < timeout)
        {
            if (_process is { HasExited: true })
            {
                throw new InvalidOperationException($"The test site exited with code {_process.ExitCode}. See {RunDirectory}.");
            }

            try
            {
                string status = await client.GetStringAsync("umbraco/management/api/v1/server/status");
                if (status.Contains("\"Run\"", StringComparison.Ordinal))
                {
                    // The seeder runs on UmbracoApplicationStartedNotification; wait for its content to be routable.
                    using HttpResponseMessage home = await client.GetAsync("/");
                    if (home.IsSuccessStatusCode)
                    {
                        return;
                    }

                    lastError = $"home page returned {(int)home.StatusCode}";
                }
                else
                {
                    lastError = status;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                lastError = exception.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new TimeoutException($"The test site did not start within {timeout} ({lastError}). See {RunDirectory}.");
    }
}

public sealed class KestrelTestSiteHost() : TestSiteHost(Hosting.Kestrel)
{
    protected override ProcessStartInfo CreateStartInfo()
    {
        BaseUrl = new Uri($"https://localhost:{GetFreePort()}/");

        var startInfo = new ProcessStartInfo(TestSitePaths.SiteExecutable)
        {
            WorkingDirectory = TestSitePaths.SiteDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "--contentRoot", TestSitePaths.SiteDirectory, "--urls", BaseUrl.ToString().TrimEnd('/') },
        };

        foreach ((string name, string value) in EnvironmentVariables)
        {
            startInfo.Environment[name] = value;
        }

        return startInfo;
    }
}

/// <summary>
/// IIS Express with the ASP.NET Core Module V2, in-process hosting from the project folder (as Visual Studio runs it),
/// anonymous authentication disabled and Windows authentication enabled.
/// </summary>
public sealed class IisExpressTestSiteHost() : TestSiteHost(Hosting.IisExpressWindowsAuth)
{
    /// <summary>The IIS Express development certificate is only bound to ports 44300-44399.</summary>
    private static int HttpsPort => int.TryParse(Environment.GetEnvironmentVariable("IISEXPRESS_HTTPS_PORT"), out int port) ? port : TestSitePaths.IisExpressHttpsPort;

    public static bool IsAvailable => OperatingSystem.IsWindows()
        && File.Exists(Path.Combine(TestSitePaths.IisExpressDirectory, "iisexpress.exe"))
        && File.Exists(Path.Combine(TestSitePaths.IisExpressDirectory, "Asp.Net Core Module", "V2", "aspnetcorev2.dll"));

    protected override ProcessStartInfo CreateStartInfo()
    {
        BaseUrl = new Uri($"https://localhost:{HttpsPort}/");
        string configPath = Path.Combine(RunDirectory, "applicationhost.config");
        File.WriteAllText(configPath, BuildApplicationHostConfig());

        return new ProcessStartInfo(Path.Combine(TestSitePaths.IisExpressDirectory, "iisexpress.exe"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { $"/config:{configPath}", $"/site:{TestSitePaths.SiteName}", "/systray:false" },
        };
    }

    private string BuildApplicationHostConfig()
    {
        string template = File.ReadAllText(Path.Combine(TestSitePaths.IisExpressDirectory, "config", "templates", "PersonalWebServer", "applicationhost.config"));

        string environment = string.Join(
            Environment.NewLine,
            EnvironmentVariables.Select(e => $"""                    <environmentVariable name="{e.Key}" value="{SecurityElementEscape(e.Value)}" />"""));

        string sites = $"""
            <sites>
                        <site name="{TestSitePaths.SiteName}" id="1" serverAutoStart="true">
                            <application path="/">
                                <virtualDirectory path="/" physicalPath="{SecurityElementEscape(TestSitePaths.SiteDirectory)}" />
                            </application>
                            <bindings>
                                <binding protocol="https" bindingInformation=":{HttpsPort}:localhost" />
                            </bindings>
                        </site>
                        <siteDefaults>
                            <logFile logFormat="W3C" directory="{SecurityElementEscape(Path.Combine(RunDirectory, "iis-logs"))}" />
                            <traceFailedRequestsLogging directory="{SecurityElementEscape(Path.Combine(RunDirectory, "iis-trace"))}" enabled="false" />
                        </siteDefaults>
                        <applicationDefaults applicationPool="Clr4IntegratedAppPool" />
                        <virtualDirectoryDefaults allowSubDirConfig="true" />
                    </sites>
            """;

        string location = $"""
                <location path="{TestSitePaths.SiteName}">
                    <system.webServer>
                        <handlers>
                            <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
                        </handlers>
                        <aspNetCore processPath="{SecurityElementEscape(TestSitePaths.SiteExecutable)}" arguments="" hostingModel="InProcess" startupTimeLimit="300">
                            <environmentVariables>
            {environment}
                            </environmentVariables>
                        </aspNetCore>
                        <security>
                            <authentication>
                                <anonymousAuthentication enabled="false" />
                                <windowsAuthentication enabled="true" />
                            </authentication>
                        </security>
                    </system.webServer>
                </location>
            </configuration>
            """;

        return template
            .Replace("""<section name="asp" overrideModeDefault="Deny" />""", """<section name="asp" overrideModeDefault="Deny" /><section name="aspNetCore" overrideModeDefault="Allow" />""")
            .Replace("</globalModules>", """<add name="AspNetCoreModuleV2" image="%IIS_BIN%\Asp.Net Core Module\V2\aspnetcorev2.dll" /></globalModules>""")
            .Replace("</modules>", """<add name="AspNetCoreModuleV2" lockItem="true" /></modules>""")
            .ReplaceSection("<sites>", "</sites>", sites)
            .Replace("</configuration>", location);
    }

    private static string SecurityElementEscape(string value) => System.Security.SecurityElement.Escape(value);
}

internal static class StringExtensions
{
    public static string ReplaceSection(this string value, string start, string end, string replacement)
    {
        int startIndex = value.IndexOf(start, StringComparison.Ordinal);
        int endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal) + end.Length;
        return string.Concat(value.AsSpan(0, startIndex), replacement, value.AsSpan(endIndex));
    }
}
