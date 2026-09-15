using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Backoffice;

/// <summary>Real clicks and typing in the backoffice UI.</summary>
public class BackofficeUiTests(Hosting hosting) : PlaywrightTest(hosting)
{
    private const string HomeKey = "7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a02";

    private BackofficeSession _session = null!;

    [OneTimeSetUp]
    public async Task SignIn() => _session = await BackofficeSession.SignInAsync(Site, await NewBrowserContextAsync());

    [OneTimeTearDown]
    public async Task SignOut() => await _session.DisposeAsync();

    [TearDown]
    public async Task ScreenshotOnFailure()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
        {
            string path = Path.Combine(Site.RunDirectory, $"{TestContext.CurrentContext.Test.Name.Replace('"', '\'')}.png");
            await _session.Page.ScreenshotAsync(new PageScreenshotOptions { Path = string.Concat(path.Split(Path.GetInvalidPathChars())), FullPage = true });
            TestContext.Out.WriteLine($"Screenshot: {path}");
        }
    }

    [TestCase("content")]
    [TestCase("media")]
    [TestCase("settings")]
    [TestCase("packages")]
    [TestCase("user-management")]
    [TestCase("member-management")]
    [TestCase("translation")]
    public async Task Section_loads_from_a_fresh_page_load_without_errors(string section)
    {
        int failuresBefore = _session.Network.UnexpectedFailures().Count();
        int pageErrorsBefore = _session.Network.PageErrors.Count;

        await _session.Page.GotoAsync($"/umbraco/section/{section}");
        await _session.Page.Locator("umb-section-default").WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        await _session.WaitForBackofficeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_session.Network.UnexpectedFailures().Skip(failuresBefore), Is.Empty);
            Assert.That(_session.Network.PageErrors.Skip(pageErrorsBefore), Is.Empty);
            Assert.That(_session.Network.BearerRequests, Is.Empty);
        }
    }

    [Test]
    public async Task Document_can_be_edited_and_published_in_the_editor()
    {
        string text = $"Published from the editor {Guid.NewGuid():N}";
        IPage page = _session.Page;

        await page.GotoAsync($"/umbraco/section/content/workspace/document/edit/{HomeKey}");
        ILocator bodyText = page.Locator("umb-property-editor-ui-text-box input").First;
        await bodyText.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });
        await bodyText.FillAsync(text);

        Task<IResponse> published = page.WaitForResponseAsync(
            response => response.Request.Method == "PUT" && response.Url.Contains(HomeKey, StringComparison.OrdinalIgnoreCase) && response.Url.Contains("publish", StringComparison.OrdinalIgnoreCase),
            new PageWaitForResponseOptions { Timeout = 60_000 });
        await page.Locator("[data-mark='workspace-action:Umb.WorkspaceAction.Document.SaveAndPublish']").ClickAsync();
        IResponse response = await published;

        IPage frontEnd = await _session.Context.NewPageAsync();
        string? rendered = null;
        for (var attempt = 0; attempt < 20 && rendered != text; attempt++)
        {
            await frontEnd.GotoAsync("/");
            rendered = await frontEnd.Locator("#body-text").TextContentAsync();
            if (rendered != text)
            {
                await Task.Delay(500);
            }
        }

        await frontEnd.CloseAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.Status, Is.EqualTo(200));
            Assert.That(rendered, Is.EqualTo(text));
            Assert.That(_session.Network.BearerRequests, Is.Empty);
        }
    }

    [Test]
    public async Task File_can_be_uploaded_in_the_media_section()
    {
        string name = $"ui-upload-test-{Guid.NewGuid():N}";
        IPage page = _session.Page;

        await page.GotoAsync("/umbraco/section/media");
        ILocator fileInput = page.Locator("umb-dropzone-media input[type=file]").First;
        await fileInput.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 60_000 });

        Task<IResponse> uploaded = page.WaitForResponseAsync(
            response => response.Request.Method == "POST" && response.Url.EndsWith("/temporary-file", StringComparison.OrdinalIgnoreCase),
            new PageWaitForResponseOptions { Timeout = 60_000 });
        Task<IResponse> created = page.WaitForResponseAsync(
            response => response.Request.Method == "POST" && response.Url.EndsWith("/management/api/v1/media", StringComparison.OrdinalIgnoreCase),
            new PageWaitForResponseOptions { Timeout = 60_000 });

        await fileInput.SetInputFilesAsync(new FilePayload
        {
            Name = name + ".txt",
            MimeType = "text/plain",
            Buffer = Encoding.UTF8.GetBytes("uploaded through the media section"),
        });

        IResponse uploadResponse = await uploaded;
        IResponse createResponse = await created;

        JsonElement root = await _session.EvaluateAsync("""
            async () => {
                const api = await import('@umbraco-cms/backoffice/external/backend-api');
                const { data } = await api.MediaService.getTreeMediaRoot({ query: { skip: 0, take: 500 } });
                return data.items.map(item => item.variants?.[0]?.name ?? item.name);
            }
            """);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(uploadResponse.Status, Is.EqualTo(201));
            Assert.That(createResponse.Status, Is.EqualTo(201));
            // Umbraco turns the file name into a title ("Ui Upload Test …"), so compare letters and digits only.
            Assert.That(root.EnumerateArray().Select(item => Normalise(item.GetString())), Has.Some.EqualTo(Normalise(name)));
            Assert.That(_session.Network.BearerRequests, Is.Empty);
        }
    }

    private static string Normalise(string? value) => string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
