using System.Globalization;
using System.Reflection;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

internal static class TestSitePaths
{
    /// <summary>The test site this suite runs: TestSite.v17 or TestSite.v18, set by the wrapper project.</summary>
    public static string SiteName { get; } = GetAssemblyMetadata("TestSiteName");

    /// <summary>
    /// The IIS Express HTTPS port for this suite, set by the wrapper project. It differs per Umbraco major so both suites can run
    /// side by side, and it must be in 44300-44399, the range the IIS Express development certificate is bound to.
    /// </summary>
    public static int IisExpressHttpsPort { get; } = int.Parse(GetAssemblyMetadata("IisExpressHttpsPort"), CultureInfo.InvariantCulture);

    /// <summary>The folder holding the solution file (<c>src</c>).</summary>
    public static string SourceDirectory { get; } = FindSourceDirectory();

    public static string SiteDirectory { get; } = Path.Combine(SourceDirectory, SiteName);

    public static string SiteExecutable { get; } = Path.Combine(SiteDirectory, "bin", Configuration, "net10.0", SiteName + ".exe");

    public static string IisExpressDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "IIS Express");

    private static string Configuration =>
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static string CreateRunDirectory(string name)
    {
        // SiteName ends in the major, e.g. "v17".
        string directory = Path.Combine(Path.GetTempPath(), "windows-authentication-tests", $"{DateTime.Now:yyyyMMdd-HHmmss}-{SiteName[^3..]}-{name}-{Guid.NewGuid():N}"[..52]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string GetAssemblyMetadata(string key)
        => typeof(TestSitePaths).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(attribute => attribute.Key == key)?.Value
           ?? throw new InvalidOperationException($"The Playwright test project must set the {key} property.");

    private static string FindSourceDirectory()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.sln").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"Could not find the solution folder above {AppContext.BaseDirectory}.");
    }
}
