using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.ContentPublishing;
using Umbraco.Cms.Core.Models.ContentTypeEditing;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.ContentTypeEditing;

namespace Umbraco.Community.Security.WindowsAuthentication.TestSite.TestSupport;

/// <summary>
/// Creates a small site the Playwright tests use for front-end authentication: a published home page, a members-only page
/// protected by public access, a member login page, an unpublished draft, a member group and a member.
/// </summary>
public sealed class TestDataSeeder(
    IRuntimeState runtimeState,
    IWebHostEnvironment hostEnvironment,
    ITemplateService templateService,
    IContentTypeEditingService contentTypeEditingService,
    IContentEditingService contentEditingService,
    IContentPublishingService contentPublishingService,
    IMemberGroupService memberGroupService,
    IMemberTypeService memberTypeService,
    IMemberEditingService memberEditingService,
    IUserService userService,
    IPublicAccessService publicAccessService,
    ILogger<TestDataSeeder> logger)
    : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private static readonly Guid SuperUserKey = Constants.Security.SuperUserKey;

    public async Task HandleAsync(UmbracoApplicationStartedNotification notification, CancellationToken cancellationToken)
    {
        if (runtimeState.Level != RuntimeLevel.Run || await templateService.GetAsync(TestData.PageTemplateAlias) is not null)
        {
            return;
        }

        ITemplate pageTemplate = await CreateTemplateAsync("Test Page", TestData.PageTemplateAlias);
        ITemplate loginTemplate = await CreateTemplateAsync("Member Login", TestData.LoginTemplateAlias);

        // The page type has to exist before the home type can allow it as a child.
        Ensure(await contentTypeEditingService.CreateAsync(PageType(TestData.PageTypeKey, "testPage", "Test Page", pageTemplate, loginTemplate, allowedAsRoot: false, []), SuperUserKey));
        Ensure(await contentTypeEditingService.CreateAsync(
            PageType(TestData.HomeTypeKey, "testHome", "Test Home", pageTemplate, loginTemplate, allowedAsRoot: true, [new ContentTypeSort(TestData.PageTypeKey, 0, "testPage")]),
            SuperUserKey));

        await CreatePageAsync(TestData.HomeKey, TestData.HomeTypeKey, "Home", parentKey: null, pageTemplate.Key, publish: true);
        await CreatePageAsync(TestData.MembersOnlyKey, TestData.PageTypeKey, "Members Only", TestData.HomeKey, pageTemplate.Key, publish: true);
        await CreatePageAsync(TestData.MemberLoginKey, TestData.PageTypeKey, "Member Login", TestData.HomeKey, loginTemplate.Key, publish: true);
        await CreatePageAsync(TestData.DraftKey, TestData.PageTypeKey, "Draft Page", TestData.HomeKey, pageTemplate.Key, publish: false);

        IMemberGroup group = Ensure(await memberGroupService.CreateAsync(new MemberGroup { Name = TestData.MemberGroup }));

        IUser superUser = await userService.GetAsync(SuperUserKey) ?? throw new InvalidOperationException("Super user not found");
        Ensure(await memberEditingService.CreateAsync(
            new MemberCreateModel
            {
                Email = TestData.MemberEmail,
                Username = TestData.MemberEmail,
                Password = TestData.MemberPassword,
                ContentTypeKey = (memberTypeService.Get(Constants.Security.DefaultMemberTypeAlias) ?? throw new InvalidOperationException("Default member type not found")).Key,
                IsApproved = true,
                Roles = [group.Key],
                Variants = [new VariantModel { Name = "Test Member" }],
            },
            superUser));

        Ensure(await publicAccessService.CreateAsync(new PublicAccessEntrySlim
        {
            ContentId = TestData.MembersOnlyKey,
            MemberGroupNames = [TestData.MemberGroup],
            MemberUserNames = [],
            LoginPageId = TestData.MemberLoginKey,
            ErrorPageId = TestData.MemberLoginKey,
        }));

        logger.LogInformation("Seeded Playwright test data");
    }

    private async Task<ITemplate> CreateTemplateAsync(string name, string alias)
    {
        string content = await System.IO.File.ReadAllTextAsync(Path.Combine(hostEnvironment.ContentRootPath, "Views", alias + ".cshtml"));
        return Ensure(await templateService.CreateAsync(name, alias, content, SuperUserKey));
    }

    private static ContentTypeCreateModel PageType(Guid key, string alias, string name, ITemplate pageTemplate, ITemplate loginTemplate, bool allowedAsRoot, ContentTypeSort[] allowedChildren)
        => new()
        {
            Key = key,
            Alias = alias,
            Name = name,
            Icon = "icon-document",
            AllowedAsRoot = allowedAsRoot,
            AllowedTemplateKeys = [pageTemplate.Key, loginTemplate.Key],
            DefaultTemplateKey = pageTemplate.Key,
            AllowedContentTypes = allowedChildren,
            Properties =
            [
                new ContentTypePropertyTypeModel
                {
                    Key = Guid.NewGuid(),
                    Alias = "bodyText",
                    Name = "Body Text",
                    DataTypeKey = Constants.DataTypes.Guids.TextstringGuid,
                },
            ],
        };

    private async Task CreatePageAsync(Guid key, Guid contentTypeKey, string name, Guid? parentKey, Guid templateKey, bool publish)
    {
        Ensure(await contentEditingService.CreateAsync(
            new ContentCreateModel
            {
                Key = key,
                ContentTypeKey = contentTypeKey,
                ParentKey = parentKey,
                TemplateKey = templateKey,
                Variants = [new VariantModel { Name = name }],
                Properties = [new PropertyValueModel { Alias = "bodyText", Value = $"{name} body" }],
            },
            SuperUserKey));

        if (publish)
        {
            Ensure(await contentPublishingService.PublishAsync(key, [new CulturePublishScheduleModel { Culture = null }], SuperUserKey));
        }
    }

    [return: System.Diagnostics.CodeAnalysis.NotNull]
    private static TResult Ensure<TResult, TStatus>(Attempt<TResult, TStatus> attempt)
        => attempt.Success
            ? attempt.Result ?? throw new InvalidOperationException($"Seeding returned no result ({attempt.Status})")
            : throw new InvalidOperationException($"Seeding failed with {typeof(TStatus).Name}.{attempt.Status}");
}
