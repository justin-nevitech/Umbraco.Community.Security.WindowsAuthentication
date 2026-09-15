using System.Text.Json;
using Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Infrastructure;

namespace Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.Backoffice;

/// <summary>
/// Create, update, publish and delete workflows across the backoffice, driven through the backoffice's own generated
/// Management API client in the signed-in page, the same code the backoffice UI runs.
/// </summary>
public class BackofficeWorkflowTests(Hosting hosting) : PlaywrightTest(hosting)
{
    private BackofficeSession _session = null!;

    [OneTimeSetUp]
    public async Task SignIn() => _session = await BackofficeSession.SignInAsync(Site, await NewBrowserContextAsync());

    [OneTimeTearDown]
    public async Task SignOut() => await _session.DisposeAsync();

    [TearDown]
    public void NoBearerHeaderReachedTheWire() => Assert.That(_session.Network.BearerRequests, Is.Empty);

    [Test]
    public async Task Dictionary_item_can_be_created_updated_and_deleted()
    {
        var id = Guid.NewGuid();

        await ExpectAsync(201, $"api.DictionaryService.postDictionary({{ body: {{ id: '{id}', name: 'test-{id:N}', parent: null, translations: [] }} }})");
        await ExpectAsync(200, $"api.DictionaryService.putDictionaryById({{ path: {{ id: '{id}' }}, body: {{ name: 'test-{id:N}-renamed', translations: [{{ isoCode: 'en-US', translation: 'Test' }}] }} }})");
        ApiResult item = await ExpectAsync(200, $"api.DictionaryService.getDictionaryById({{ path: {{ id: '{id}' }} }})");
        await ExpectAsync(200, $"api.DictionaryService.deleteDictionaryById({{ path: {{ id: '{id}' }} }})");

        Assert.That(item.Data.GetProperty("name").GetString(), Is.EqualTo($"test-{id:N}-renamed"));
    }

    [Test]
    public async Task Data_type_can_be_created_and_deleted()
    {
        var id = Guid.NewGuid();

        await ExpectAsync(201, $"api.DataTypeService.postDataType({{ body: {{ id: '{id}', name: 'test-{id:N}', editorAlias: 'Umbraco.TextBox', editorUiAlias: 'Umb.PropertyEditorUi.TextBox', values: [], parent: null }} }})");
        await ExpectAsync(200, $"api.DataTypeService.deleteDataTypeById({{ path: {{ id: '{id}' }} }})");
    }

    [Test]
    public async Task Template_can_be_created_updated_and_deleted()
    {
        var id = Guid.NewGuid();
        string alias = $"test{id:N}";

        await ExpectAsync(201, $"api.TemplateService.postTemplate({{ body: {{ id: '{id}', name: '{alias}', alias: '{alias}', content: '<p>created</p>' }} }})");
        await ExpectAsync(200, $"api.TemplateService.putTemplateById({{ path: {{ id: '{id}' }}, body: {{ name: '{alias}', alias: '{alias}', content: '<p>updated</p>' }} }})");
        ApiResult template = await ExpectAsync(200, $"api.TemplateService.getTemplateById({{ path: {{ id: '{id}' }} }})");
        await ExpectAsync(200, $"api.TemplateService.deleteTemplateById({{ path: {{ id: '{id}' }} }})");

        Assert.That(template.Data.GetProperty("content").GetString(), Is.EqualTo("<p>updated</p>"));
    }

    [TestCase("PartialView", "cshtml", "<p>test</p>")]
    [TestCase("Stylesheet", "css", "body { color: red; }")]
    [TestCase("Script", "js", "console.log('test');")]
    public async Task File_system_item_can_be_created_updated_and_deleted(string type, string extension, string content)
    {
        string name = $"test-{Guid.NewGuid():N}.{extension}";

        ApiResult created = await ExpectAsync(201, $"api.{type}Service.post{type}({{ body: {{ name: '{name}', parent: null, content: {JsonSerializer.Serialize(content)} }} }})");
        string path = Uri.UnescapeDataString(created.GeneratedResource ?? $"/{name}");

        await ExpectAsync(200, $"api.{type}Service.put{type}ByPath({{ path: {{ path: '{path}' }}, body: {{ content: {JsonSerializer.Serialize(content + " ")} }} }})");
        await ExpectAsync(200, $"api.{type}Service.delete{type}ByPath({{ path: {{ path: '{path}' }} }})");
    }

    [Test]
    public async Task Document_type_and_document_full_lifecycle()
    {
        var typeId = Guid.NewGuid();
        var documentId = Guid.NewGuid();

        await ExpectAsync(201, $$"""
            api.DocumentTypeService.postDocumentType({ body: {
                id: '{{typeId}}', alias: 'test{{typeId:N}}', name: 'Test {{typeId:N}}', icon: 'icon-document',
                allowedAsRoot: true, variesByCulture: false, variesBySegment: false, isElement: false,
                properties: [], containers: [], parent: null, allowedTemplates: [], defaultTemplate: null,
                cleanup: { preventCleanup: false }, allowedDocumentTypes: [], compositions: []
            } })
            """);

        await ExpectAsync(201, $$"""
            api.DocumentService.postDocument({ body: {
                id: '{{documentId}}', parent: null, documentType: { id: '{{typeId}}' }, template: null,
                values: [], variants: [{ culture: null, segment: null, name: 'Test document' }]
            } })
            """);

        await ExpectAsync(200, $$"""
            api.DocumentService.putDocumentById({ path: { id: '{{documentId}}' }, body: {
                template: null, values: [], variants: [{ culture: null, segment: null, name: 'Test document renamed' }]
            } })
            """);

        await ExpectAsync(200, $"api.DocumentService.putDocumentByIdPublish({{ path: {{ id: '{documentId}' }}, body: {{ publishSchedules: [{{ culture: null }}] }} }})");
        ApiResult published = await ExpectAsync(200, $"api.DocumentService.getDocumentById({{ path: {{ id: '{documentId}' }} }})");
        await ExpectAsync(200, $"api.DocumentService.putDocumentByIdUnpublish({{ path: {{ id: '{documentId}' }}, body: {{ cultures: null }} }})");
        await ExpectAsync(200, $"api.DocumentService.putDocumentByIdMoveToRecycleBin({{ path: {{ id: '{documentId}' }} }})");
        await ExpectAsync(200, $"api.DocumentService.deleteRecycleBinDocumentById({{ path: {{ id: '{documentId}' }} }})");
        await ExpectAsync(200, $"api.DocumentTypeService.deleteDocumentTypeById({{ path: {{ id: '{typeId}' }} }})");

        using (Assert.EnterMultipleScope())
        {
            JsonElement variant = published.Data.GetProperty("variants")[0];
            Assert.That(variant.GetProperty("name").GetString(), Is.EqualTo("Test document renamed"));
            Assert.That(variant.GetProperty("state").GetString(), Is.EqualTo("Published"));
        }
    }

    [Test]
    public async Task Media_folder_can_be_created_recycled_and_deleted()
    {
        var id = Guid.NewGuid();
        string folderType = await MediaTypeIdAsync("Folder");

        await ExpectAsync(201, $$"""
            api.MediaService.postMedia({ body: {
                id: '{{id}}', parent: null, mediaType: { id: '{{folderType}}' },
                values: [], variants: [{ culture: null, segment: null, name: 'Test folder' }]
            } })
            """);
        await ExpectAsync(200, $"api.MediaService.putMediaByIdMoveToRecycleBin({{ path: {{ id: '{id}' }} }})");
        await ExpectAsync(200, $"api.MediaService.deleteRecycleBinMediaById({{ path: {{ id: '{id}' }} }})");
    }

    [Test]
    public async Task File_can_be_uploaded_and_saved_as_media()
    {
        var temporaryFileId = Guid.NewGuid();
        var mediaId = Guid.NewGuid();
        string fileType = await MediaTypeIdAsync("File");

        await ExpectAsync(201, $$"""
            api.TemporaryFileService.postTemporaryFile({ body: { Id: '{{temporaryFileId}}', File: new File(['test upload ' + '0123456789'.repeat(5000)], 'upload-test.txt', { type: 'text/plain' }) } })
            """);

        await ExpectAsync(201, $$"""
            api.MediaService.postMedia({ body: {
                id: '{{mediaId}}', parent: null, mediaType: { id: '{{fileType}}' },
                values: [{ alias: 'umbracoFile', culture: null, segment: null, value: { temporaryFileId: '{{temporaryFileId}}' } }],
                variants: [{ culture: null, segment: null, name: 'Test upload' }]
            } })
            """);

        ApiResult media = await ExpectAsync(200, $"api.MediaService.getMediaById({{ path: {{ id: '{mediaId}' }} }})");
        string? src = media.Data.GetProperty("values").EnumerateArray()
            .Single(v => v.GetProperty("alias").GetString() == "umbracoFile")
            .GetProperty("value").GetProperty("src").GetString();

        using HttpClient client = Site.CreateHttpClient();
        string served = await client.GetStringAsync(src!.TrimStart('/'));

        await ExpectAsync(200, $"api.MediaService.putMediaByIdMoveToRecycleBin({{ path: {{ id: '{mediaId}' }} }})");
        await ExpectAsync(200, $"api.MediaService.deleteRecycleBinMediaById({{ path: {{ id: '{mediaId}' }} }})");

        Assert.That(served, Does.StartWith("test upload 0123456789"));
    }

    [Test]
    public async Task Member_group_and_member_can_be_created_and_deleted()
    {
        var groupId = Guid.NewGuid();
        var memberId = Guid.NewGuid();
        ApiResult memberTypes = await ExpectAsync(200, "api.MemberTypeService.getTreeMemberTypeRoot({ query: { skip: 0, take: 100 } })");
        string memberType = memberTypes.Data.GetProperty("items")[0].GetProperty("id").GetString()!;

        await ExpectAsync(201, $"api.MemberGroupService.postMemberGroup({{ body: {{ id: '{groupId}', name: 'test-{groupId:N}' }} }})");
        await ExpectAsync(201, $$"""
            api.MemberService.postMember({ body: {
                id: '{{memberId}}', email: 'test-{{memberId:N}}@example.com', username: 'test-{{memberId:N}}@example.com',
                password: 'Test-Member-Password-1234', memberType: { id: '{{memberType}}' }, groups: ['{{groupId}}'], isApproved: true,
                values: [], variants: [{ culture: null, segment: null, name: 'Test member' }]
            } })
            """);
        await ExpectAsync(200, $"api.MemberService.deleteMemberById({{ path: {{ id: '{memberId}' }} }})");
        await ExpectAsync(200, $"api.MemberGroupService.deleteMemberGroupById({{ path: {{ id: '{groupId}' }} }})");
    }

    [Test]
    public async Task Language_can_be_created_and_deleted()
    {
        await ExpectAsync(201, "api.LanguageService.postLanguage({ body: { name: 'Danish', isoCode: 'da-DK', isDefault: false, isMandatory: false, fallbackIsoCode: null } })");
        await ExpectAsync(200, "api.LanguageService.deleteLanguageByIsoCode({ path: { isoCode: 'da-DK' } })");
    }

    [Test]
    public async Task Webhook_can_be_created_and_deleted()
    {
        var id = Guid.NewGuid();

        await ExpectAsync(201, $"api.WebhookService.postWebhook({{ body: {{ id: '{id}', enabled: false, name: 'test', url: 'https://example.com/hook', events: ['Umbraco.ContentPublish'], contentTypeKeys: [], headers: {{}} }} }})");
        await ExpectAsync(200, $"api.WebhookService.deleteWebhookById({{ path: {{ id: '{id}' }} }})");
    }

    [Test]
    public async Task User_group_and_user_can_be_created_and_deleted()
    {
        var groupId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await ExpectAsync(201, $$"""
            api.UserGroupService.postUserGroup({ body: {
                id: '{{groupId}}', name: 'Test {{groupId:N}}', alias: 'test{{groupId:N}}', sections: ['Umb.Section.Content'],
                languages: [], hasAccessToAllLanguages: true, documentRootAccess: true, mediaRootAccess: true,
                fallbackPermissions: [], permissions: []
            } })
            """);
        await ExpectAsync(201, $$"""
            api.UserService.postUser({ body: {
                id: '{{userId}}', email: 'test-{{userId:N}}@example.com', userName: 'test-{{userId:N}}@example.com', name: 'Test user',
                userGroupIds: [{ id: '{{groupId}}' }], kind: 'Default'
            } })
            """);
        await ExpectAsync(200, $"api.UserService.deleteUserById({{ path: {{ id: '{userId}' }} }})");
        await ExpectAsync(200, $"api.UserGroupService.deleteUserGroupById({{ path: {{ id: '{groupId}' }} }})");
    }

    [Test]
    public async Task Package_can_be_created_downloaded_and_deleted()
    {
        var id = Guid.NewGuid();

        await ExpectAsync(201, $$"""
            api.PackageService.postPackageCreated({ body: {
                id: '{{id}}', name: 'Test {{id:N}}', contentLoadChildNodes: false, mediaIds: [], mediaLoadChildNodes: false,
                documentTypes: [], mediaTypes: [], dataTypes: [], templates: [], partialViews: [], stylesheets: [], scripts: [],
                languages: [], dictionaryItems: []
            } })
            """);
        ApiResult download = await ExpectAsync(200, $"api.PackageService.getPackageCreatedByIdDownload({{ path: {{ id: '{id}' }} }})");
        await ExpectAsync(200, $"api.PackageService.deletePackageCreatedById({{ path: {{ id: '{id}' }} }})");

        Assert.That(download.BodySize, Is.GreaterThan(0));
    }

    [Test]
    public async Task Health_checks_run()
    {
        ApiResult groups = await ExpectAsync(200, "api.HealthCheckService.getHealthCheckGroup({ query: { skip: 0, take: 100 } })");
        string group = groups.Data.GetProperty("items")[0].GetProperty("name").GetString()!;

        await ExpectAsync(200, $"api.HealthCheckService.postHealthCheckGroupByNameCheck({{ path: {{ name: {JsonSerializer.Serialize(group)} }} }})");
    }

    [Test]
    public async Task Search_index_rebuild_and_cache_reload_run()
    {
        await ExpectAsync(200, "api.IndexerService.postIndexerByIndexNameRebuild({ path: { indexName: 'ExternalIndex' } })");
        await ExpectAsync(200, "api.PublishedCacheService.postPublishedCacheReload({})");
    }

    private async Task<string> MediaTypeIdAsync(string name)
    {
        ApiResult root = await ExpectAsync(200, "api.MediaTypeService.getTreeMediaTypeRoot({ query: { skip: 0, take: 100 } })");
        return root.Data.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
    }

    private async Task<ApiResult> ExpectAsync(int expectedStatus, string call)
    {
        JsonElement result = await _session.EvaluateAsync($$"""
            async () => {
                const api = await import('@umbraco-cms/backoffice/external/backend-api');
                try {
                    const result = await {{call}};
                    let data = result.data ?? null;
                    let bodySize = Number(result.response?.headers.get('Content-Length') ?? NaN) || null;
                    if (data instanceof Blob) {
                        bodySize = data.size;
                        data = null;
                    } else if (data instanceof ArrayBuffer) {
                        bodySize = data.byteLength;
                        data = null;
                    } else if (typeof data === 'string') {
                        bodySize = data.length;
                    }
                    return {
                        status: result.response?.status ?? 0,
                        data,
                        bodySize,
                        generatedResource: result.response?.headers.get('Umb-Generated-Resource') ?? null,
                        error: result.error ? JSON.stringify(result.error) : null,
                    };
                } catch (thrown) {
                    return { status: thrown?.status ?? -1, data: null, bodySize: null, generatedResource: null, error: JSON.stringify(thrown) ?? String(thrown) };
                }
            }
            """);

        var apiResult = new ApiResult(
            result.GetProperty("status").GetInt32(),
            result.GetProperty("data"),
            result.GetProperty("bodySize").ValueKind == JsonValueKind.Number ? result.GetProperty("bodySize").GetInt64() : null,
            result.GetProperty("generatedResource").ValueKind == JsonValueKind.String ? result.GetProperty("generatedResource").GetString() : null,
            result.GetProperty("error").ToString());

        Assert.That(apiResult.Status, Is.EqualTo(expectedStatus), $"{call.Trim()}\n{apiResult.Error}");
        return apiResult;
    }

    private sealed record ApiResult(int Status, JsonElement Data, long? BodySize, string? GeneratedResource, string Error);
}
