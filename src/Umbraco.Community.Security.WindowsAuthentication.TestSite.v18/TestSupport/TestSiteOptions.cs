namespace Umbraco.Community.Security.WindowsAuthentication.TestSite.TestSupport;

/// <summary>
/// Test-only settings, bound from the "TestSite" configuration section. Everything here is off unless configured, which the
/// Development appsettings and the Playwright tests do.
/// </summary>
public sealed class TestSiteOptions
{
    public const string SectionName = "TestSite";

    public bool SeedTestData { get; set; }

    public bool EnableTestAuthentication { get; set; }

    public string JwtSigningKey { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string BasicUsername { get; set; } = string.Empty;

    public string BasicPassword { get; set; } = string.Empty;
}

public static class TestAuthenticationSchemes
{
    public const string Jwt = "TestJwt";

    public const string ApiKey = "TestApiKey";

    public const string Basic = "TestBasic";

    public const string JwtIssuer = "windows-authentication-test-site";
}

/// <summary>Content, member and key values the seeder creates and the Playwright tests rely on.</summary>
public static class TestData
{
    public static readonly Guid PageTypeKey = new("7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a01");
    public static readonly Guid HomeTypeKey = new("7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a06");
    public static readonly Guid HomeKey = new("7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a02");
    public static readonly Guid MembersOnlyKey = new("7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a03");
    public static readonly Guid MemberLoginKey = new("7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a04");
    public static readonly Guid DraftKey = new("7d2b2a4c-9f0e-4a4b-8b35-5c8c0f6b3a05");

    public const string PageTemplateAlias = "testPage";
    public const string LoginTemplateAlias = "memberLogin";
    public const string MemberGroup = "Testers";
    public const string MemberEmail = "member@example.com";
    public const string MemberPassword = "WindowsAuth-Member-1234";
}
