# Building & dual-major (Umbraco 17 / 18) support

This package supports Umbraco **17** and **18** from a single set of sources. Both Umbraco
majors run on `net10.0`, so the usual TFM-based multi-targeting (`net6.0`/`net8.0`) does not
apply. Instead there are **two package projects that compile the same source files** against
different Umbraco package ranges.

## Layout

| Project | Umbraco | Symbol | Produces |
| ------- | ------- | ------ | -------- |
| `src/Umbraco.Community.Security.WindowsAuthentication.v17` | `[17.5.0, 18.0.0)` | — | package `17.x` |
| `src/Umbraco.Community.Security.WindowsAuthentication.v18` | `[18.0.0, 19.0.0)` | `UMBRACO_18` | package `18.x` |

**`src/Umbraco.Community.Security.WindowsAuthentication` is not a project — it is the shared
source folder.** It holds all the C# sources, the TypeScript client and the built backoffice
assets (`wwwroot/App_Plugins`), but no `.csproj`.

The two wrapper projects above own no sources of their own. Each imports
`WindowsAuthentication.Shared.props` and `SharedBackofficeAssets.targets` from that folder, which
declare the shared compile items, backoffice assets and package metadata using
`$(MSBuildThisFileDirectory)`-anchored paths. Add new files to the shared folder and both variants
pick them up automatically. Both produce the same assembly name and the same `PackageId`; the
*only* differences are the package ranges above and the `UMBRACO_18` symbol.

The Umbraco 17 floor is `17.5.0` because Umbraco 17.4.0–17.4.2 start the backoffice without
waiting for app entry points (see [What differs between the two variants](#what-differs-between-the-two-variants)).

Build/pack each variant:

```bash
# Umbraco 17
dotnet build src/Umbraco.Community.Security.WindowsAuthentication.v17/Umbraco.Community.Security.WindowsAuthentication.v17.csproj -c Release
dotnet pack  src/Umbraco.Community.Security.WindowsAuthentication.v17/Umbraco.Community.Security.WindowsAuthentication.v17.csproj -c Release /p:Version=17.0.0

# Umbraco 18
dotnet build src/Umbraco.Community.Security.WindowsAuthentication.v18/Umbraco.Community.Security.WindowsAuthentication.v18.csproj -c Release
dotnet pack  src/Umbraco.Community.Security.WindowsAuthentication.v18/Umbraco.Community.Security.WindowsAuthentication.v18.csproj -c Release /p:Version=18.0.0
```

The version ranges flow into the packed `.nuspec` dependency nodes, so a `17.x` release depends on
`Umbraco.Cms.Web.Common` `[17.5.0, 18.0.0)` and an `18.x` release on `[18.0.0, 19.0.0)`.

## Why two projects instead of one wide version range

The package code is identical on both majors today, so a single project with a range such as
`[17.5.0, 19.0.0)` would work. Two projects are used anyway, for the same reasons as the other
packages built from the [opinionated package starter](https://github.com/LottePitcher/opinionated-package-starter):

- **Each major is compiled against its own floor.** NuGet restores the lowest version in a range,
  so the `.v17` project compiles against Umbraco 17.5.0 and the `.v18` project against 18.0.0. An
  API that is missing or changed on either major's floor fails the build, rather than failing on a
  customer's site.
- **Version-aligned releases.** The package major tracks the Umbraco major (see below), which only
  works when each release declares a range for a single major.
- **Room for version-specific code.** If a future Umbraco release needs different code, it goes
  behind `#if UMBRACO_18` without restructuring. A single project could only express that with an
  MSBuild property such as `-p:UmbracoMajor=18`, and **NuGet restore evaluates each project once
  with its default properties**, so one solution could never restore the shared project at both
  majors at the same time.

Two project files give each major its own restore, so the whole solution — both package variants,
both test suites and both test sites — builds together, and both sites can run side by side.

## Packaging model: version-aligned, one PackageId

There is **one** PackageId (`Umbraco.Community.Security.WindowsAuthentication`). The package major
tracks the Umbraco major:

- Install on Umbraco 17 → `17.x` of this package.
- Install on Umbraco 18 → `18.x` of this package.

The release workflow (`.github/workflows/release.yml`) selects the project to pack from the leading
major of the pushed tag, so tagging `17.2.3` packs the `.v17` project and `18.0.0` packs the `.v18`
project. A tag whose major is neither 17 nor 18 fails the build rather than publishing the wrong
variant. The workflow runs the unit tests for that major before it packs, so a failing test blocks
the push to NuGet. Publishing needs a `NUGET_API_KEY` repository secret.

## The client script

The backoffice half of the package is a single TypeScript module,
`Client/src/windows-authentication.ts`, registered as an `appEntryPoint` in
`Client/public/umbraco-package.json`.

```bash
cd src/Umbraco.Community.Security.WindowsAuthentication/Client
npm install
npm run build   # tsc (type check) + vite build + scripts/update-manifest.js
```

- Vite writes `wwwroot/App_Plugins/WindowsAuthentication/windows-authentication-[hash].js` and copies
  the manifest from `Client/public`. The content hash in the file name busts browser caches when the
  package is updated.
- `scripts/update-manifest.js` points the built manifest's `appEntryPoint` at the hashed file name.
- The built output is **committed**. The .NET build does not run npm: `SharedBackofficeAssets.targets`
  mirrors the shared `wwwroot` into each wrapper project's own (git-ignored) `wwwroot`, because the
  static web assets pipeline records assets against the project's own folder. Files with an older
  hash are removed from the mirror, so only the current script ships.

Rebuild the client and commit its output whenever `windows-authentication.ts` changes.

## What differs between the two variants

**The package: nothing.** The server middleware, the composer and the client script are shared
without any `#if`. The `UMBRACO_18` symbol is defined but currently unused in package code.

**The Playwright tests:** the Management API OpenAPI document moved between majors, and the sweep
test reads it:

| Umbraco | OpenAPI document URL |
| ------- | -------------------- |
| 17 | `/umbraco/swagger/management/swagger.json` |
| 18 | `/umbraco/openapi/management.json` |

**At runtime: the backoffice boot order.** The client script must be installed before the
backoffice sends its first authenticated request. It is, because the backoffice waits for public
`appEntryPoint`s to load before it starts routing. That wait is present in every release checked
except three, which is why the Umbraco 17 floor is `17.5.0`:

| Umbraco backoffice | Waits for app entry points |
| ------------------ | -------------------------- |
| 17.0.0 – 17.3.5 | ✅ |
| **17.4.0 – 17.4.2** | ❌ the router starts without waiting |
| 17.5.0 – 17.6.2 | ✅ |
| 18.0.0 – 18.1.1 | ✅ |

On 17.4.0–17.4.2, the first authenticated requests can race the client script. Behind Windows
Authentication a request that loses the race is refused by IIS, and the client script logs a
`[WindowsAuthentication] … started before the client script was installed` console warning. The
releases before 17.4.0 also wait, but a NuGet range can't skip versions in the middle, so the `.v17`
range starts at 17.5.0. Re-check this table on every new Umbraco minor.

## Tests

The test suites mirror the package layout. `src/Umbraco.Community.Security.WindowsAuthentication.Tests`
and `src/Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests` are **shared source
folders, not projects**; wrapper projects compile the same test files once per major:

| Project | Runs against | Symbol |
| ------- | ------------ | ------ |
| `src/Umbraco.Community.Security.WindowsAuthentication.Tests.v17` | `Umbraco.Community.Security.WindowsAuthentication.v17` | — |
| `src/Umbraco.Community.Security.WindowsAuthentication.Tests.v18` | `Umbraco.Community.Security.WindowsAuthentication.v18` | `UMBRACO_18` |
| `src/Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.v17` | `TestSite.v17` (latest Umbraco 17) | — |
| `src/Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.v18` | `TestSite.v18` (latest Umbraco 18) | `UMBRACO_18` |

Between them the suites cover both ends of each supported range: the unit test projects reference
the package projects, so they compile and run against each major's floor (17.5.0 and 18.0.0), while
the Playwright tests run the package inside the latest release of each major (17.6.2 and 18.1.1).

**Unit tests** cover the server rules (including the failed sign-in rule), the middleware and composer, a real ASP.NET Core
stack with other authentication schemes run with and without the middleware, and the built client script
loaded into a real browser. The client script run fails if any block of the script never executed.

**Playwright tests** start the built test site themselves, on a fresh SQLite database, under two
hosting models: Kestrel without Windows Authentication, and IIS Express with anonymous
authentication **off** and Windows Authentication **on**. They sign in to the real backoffice and
check the transport, every parameterless Management API GET, create/publish/delete workflows, the
UI, session loss, a failed sign-in, sign-out, a negative control with the client script blocked, and front-end,
member and custom authentication.

```bash
cd src
dotnet build Umbraco.Community.Security.WindowsAuthentication.sln

dotnet test Umbraco.Community.Security.WindowsAuthentication.Tests.v17             # unit, Umbraco 17
dotnet test Umbraco.Community.Security.WindowsAuthentication.Tests.v18             # unit, Umbraco 18
dotnet test Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.v17   # browser, Umbraco 17 site
dotnet test Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.v18   # browser, Umbraco 18 site
```

The browser tests use the installed Microsoft Edge through Playwright for .NET, so no browser
download is needed locally. Environment variables:

| Variable | Effect |
| -------- | ------ |
| `PLAYWRIGHT_HOSTING=Kestrel` or `IisExpressWindowsAuth` | Run one hosting model only. Running one at a time also halves memory use. |
| `IISEXPRESS_HTTPS_PORT` | Override the IIS Express port (44300–44399). Defaults: 44391 for v17, 44392 for v18. |
| `BROWSER_CHANNEL` | Browser channel, default `msedge`. CI uses `chromium`. |
| `HEADED=1` | Show the browser (Playwright tests). |

Each Playwright host writes its database, logs and IIS logs to a run folder under
`%TEMP%\windows-authentication-tests`.

## Test sites

Both test sites are in the solution and can run at the same time:

| Site | Umbraco | IIS Express (Windows Auth) | Kestrel (no Windows Auth) | References |
| ---- | ------- | -------------------------- | ------------------------- | ---------- |
| `TestSite.v17` | 17.6.2 | `https://localhost:44317` | `https://localhost:44417` | `Umbraco.Community.Security.WindowsAuthentication.v17` |
| `TestSite.v18` | 18.1.1 | `https://localhost:44318` | `https://localhost:44418` | `Umbraco.Community.Security.WindowsAuthentication.v18` |

In Visual Studio, pick the `IIS Express (Windows Auth)` or `Kestrel (no Windows Auth)` profile.
Without Visual Studio:

```powershell
.\scripts\Start-IISExpress.ps1                      # TestSite.v17 under IIS Express with Windows Authentication
.\scripts\Start-IISExpress.ps1 -UmbracoVersion 18   # TestSite.v18
dotnet run --project src/Umbraco.Community.Security.WindowsAuthentication.TestSite.v17 --launch-profile "Kestrel (no Windows Auth)"
```

Both sites install unattended into SQLite with the backoffice login `admin@example.com` /
`WindowsAuth-Test-1234`. Five failed sign-ins lock that account for 5 minutes
(`Umbraco:CMS:Security:UserDefaultLockoutTimeInMinutes`; Umbraco's default is 30 days). IIS signs you in with your Windows account
first; the Umbraco login comes after. Edge does the Windows sign-in to `localhost` automatically. Firefox only does so once
`localhost` is in its `network.negotiate-auth.trusted-uris` and `network.automatic-ntlm-auth.trusted-uris`
preferences, and otherwise prompts repeatedly (see [CONTRIBUTING.md](../.github/CONTRIBUTING.md)). The test-only pieces (seeded content, a member and public access, custom JWT, API key and
Basic schemes, and a diagnostics dashboard under **Settings → Windows Authentication**) are switched
on by `appsettings.Development.json`.
