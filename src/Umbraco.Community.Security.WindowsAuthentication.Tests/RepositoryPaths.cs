using System.Text.Json;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests;

internal static class RepositoryPaths
{
    /// <summary>The folder holding the solution file (<c>src</c>).</summary>
    public static string SourceDirectory { get; } = FindSourceDirectory();

    public static string PluginDirectory { get; } = Path.Combine(
        SourceDirectory, "Umbraco.Community.Security.WindowsAuthentication", "wwwroot", "App_Plugins", "WindowsAuthentication");

    public static string Manifest { get; } = Path.Combine(PluginDirectory, "umbraco-package.json");

    /// <summary>The built client script. Its file name carries a content hash, so it is read from the built manifest.</summary>
    public static string ClientScript { get; } = FindClientScript();

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

    private static string FindClientScript()
    {
        if (File.Exists(Manifest) is false)
        {
            return Path.Combine(PluginDirectory, "windows-authentication-[hash].js");
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(Manifest));
        string js = manifest.RootElement.GetProperty("extensions").EnumerateArray()
            .Single(extension => extension.GetProperty("type").GetString() == "appEntryPoint")
            .GetProperty("js").GetString()!;

        return Path.Combine(PluginDirectory, Path.GetFileName(js));
    }
}
