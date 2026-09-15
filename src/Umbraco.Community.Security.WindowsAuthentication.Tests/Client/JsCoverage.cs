using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Umbraco.Community.Security.WindowsAuthentication.Tests.Client;

/// <summary>
/// Collects V8 block coverage of the client script across every client test and fails the run if any code was never executed.
/// The check only runs when every client test ran, so filtered runs don't report false gaps.
/// </summary>
internal static partial class JsCoverage
{
    private static readonly Lock Gate = new();
    private static readonly HashSet<string> ExecutedTests = [];
    private static bool[]? _covered;

    public static async Task<ICDPSession> StartAsync(IBrowserContext context, IPage page)
    {
        ICDPSession session = await context.NewCDPSessionAsync(page);
        await session.SendAsync("Profiler.enable");
        await session.SendAsync("Profiler.startPreciseCoverage", new Dictionary<string, object> { ["callCount"] = true, ["detailed"] = true });
        return session;
    }

    public static async Task CollectAsync(ICDPSession session, string source, string testName)
    {
        JsonElement? coverage = await session.SendAsync("Profiler.takePreciseCoverage");
        if (coverage is null)
        {
            return;
        }

        lock (Gate)
        {
            ExecutedTests.Add(testName);
            _covered ??= new bool[source.Length];

            foreach (JsonElement script in coverage.Value.GetProperty("result").EnumerateArray())
            {
                if (Uri.TryCreate(script.GetProperty("url").GetString(), UriKind.Absolute, out Uri? url) is false || url.AbsolutePath != "/windows-authentication.js")
                {
                    continue;
                }

                // Apply outer ranges before the ranges nested inside them, the way V8 reports block coverage.
                var ranges = script.GetProperty("functions").EnumerateArray()
                    .SelectMany(function => function.GetProperty("ranges").EnumerateArray())
                    .Select(range => (Start: range.GetProperty("startOffset").GetInt32(), End: range.GetProperty("endOffset").GetInt32(), Count: range.GetProperty("count").GetInt32()))
                    .OrderBy(range => range.Start)
                    .ThenByDescending(range => range.End);

                var counts = new int[source.Length];
                foreach ((int start, int end, int count) in ranges)
                {
                    Array.Fill(counts, count, start, Math.Min(end, source.Length) - start);
                }

                for (var i = 0; i < counts.Length; i++)
                {
                    _covered[i] |= counts[i] > 0;
                }
            }
        }
    }

    public static void ReportAndAssert()
    {
        string source = File.ReadAllText(RepositoryPaths.ClientScript);
        List<string> gaps = FindGaps(source);

        string report = gaps.Count == 0
            ? "windows-authentication.js: every block executed."
            : $"windows-authentication.js: {gaps.Count} block(s) never executed:\n{string.Join("\n", gaps)}";

        string reportPath = Path.Combine(AppContext.BaseDirectory, "windows-authentication.coverage.txt");
        File.WriteAllText(reportPath, report);
        TestContext.Progress.WriteLine(report);

        HashSet<string> expected = AllClientTests();
        if (_covered is null || expected.IsSubsetOf(ExecutedTests) is false)
        {
            TestContext.Progress.WriteLine("Client test run was filtered, skipping the client script coverage check.");
            return;
        }

        Assert.That(gaps, Is.Empty, $"Uncovered code in the client script, see {reportPath}");
    }

    private static List<string> FindGaps(string source)
    {
        var gaps = new List<string>();
        if (_covered is null)
        {
            return gaps;
        }

        for (var start = 0; start < source.Length;)
        {
            if (_covered[start])
            {
                start++;
                continue;
            }

            int end = start;
            while (end < source.Length && _covered[end] is false)
            {
                end++;
            }

            string code = CommentsAndPunctuation().Replace(source[start..end], string.Empty);
            if (code.Length > 0)
            {
                int line = source.AsSpan(0, start).Count('\n') + 1;
                gaps.Add($"  line {line}: {source[start..end].Trim()}");
            }

            start = end;
        }

        return gaps;
    }

    private static HashSet<string> AllClientTests()
        => typeof(JsCoverage).Assembly.GetTypes()
            .Where(type => type.IsAbstract is false && type.IsSubclassOf(typeof(ClientScriptBrowserTest)))
            .SelectMany(type => type.GetMethods().Where(method => method.GetCustomAttributes().Any(a => a is TestAttribute or TestCaseAttribute or TestCaseSourceAttribute)).Select(method => $"{type.Name}.{method.Name}"))
            .ToHashSet();

    [GeneratedRegex(@"//[^\n]*|/\*.*?\*/|[\s{}();,]", RegexOptions.Singleline)]
    private static partial Regex CommentsAndPunctuation();
}
