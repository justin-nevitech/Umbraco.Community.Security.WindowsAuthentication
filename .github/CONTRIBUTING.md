# Contributing Guidelines

Contributions to this package are most welcome!

## Getting Started

There are test sites in the solution to make working with this repository easier. They are configured to do an unattended install; check `appsettings.Development.json` for the login details.

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (LTS version recommended), only needed when changing the client script
- IIS Express with the ASP.NET Core Module V2 (installed with Visual Studio or the .NET Hosting Bundle), to run the sites behind Windows Authentication
- Microsoft Edge, for the browser tests

### Running the Test Site

1. Clone the repository
2. Open `src/Umbraco.Community.Security.WindowsAuthentication.sln` in your IDE
3. Set `Umbraco.Community.Security.WindowsAuthentication.TestSite.v17` (or `.v18`) as the startup project
4. Pick the `IIS Express (Windows Auth)` launch profile to run behind Windows Authentication with anonymous authentication off, or `Kestrel (no Windows Auth)` for an ordinary site
5. Run the project — it will perform an unattended Umbraco install on first run
6. Log in with the credentials from `appsettings.Development.json`
7. Open **Settings → Windows Authentication** for the diagnostics dashboard

Without Visual Studio, run `.\scripts\Start-IISExpress.ps1` (add `-UmbracoVersion 18` for the Umbraco 18 site).

> The package supports both Umbraco 17 and 18 from one set of sources, via two wrapper package projects that compile the same files (`Umbraco.Community.Security.WindowsAuthentication.v17` and `Umbraco.Community.Security.WindowsAuthentication.v18`). The sources themselves live in `Umbraco.Community.Security.WindowsAuthentication`, which is a shared source folder rather than a project — add new files there and both variants pick them up automatically. Both test sites are in the solution and can run at the same time (v17 on `https://localhost:44317`, v18 on `https://localhost:44318` under IIS Express). See [docs/BUILDING.md](../docs/BUILDING.md) for the dual-major build details.

### Building the Frontend

The client script is in `src/Umbraco.Community.Security.WindowsAuthentication/Client/`. To build:

```bash
cd src/Umbraco.Community.Security.WindowsAuthentication/Client
npm install
npm run build
```

The built output goes to `src/Umbraco.Community.Security.WindowsAuthentication/wwwroot/App_Plugins/WindowsAuthentication/` and is committed. Rebuild the solution afterwards so the package projects pick up the new file.

### Running the Tests

```bash
cd src
dotnet build Umbraco.Community.Security.WindowsAuthentication.sln
dotnet test Umbraco.Community.Security.WindowsAuthentication.Tests.v17             # unit tests, Umbraco 17
dotnet test Umbraco.Community.Security.WindowsAuthentication.Tests.v18             # unit tests, Umbraco 18
dotnet test Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.v17   # browser tests, Umbraco 17 site
dotnet test Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.v18   # browser tests, Umbraco 18 site
```

The test sources live once in `src/Umbraco.Community.Security.WindowsAuthentication.Tests` and `src/Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests` (shared folders, not projects) and are compiled by the wrapper test projects, so every test runs against both Umbraco majors. Add new tests there and both pick them up.

The Playwright tests start each test site under Kestrel and under IIS Express with Windows Authentication. Set `PLAYWRIGHT_HOSTING=Kestrel` or `PLAYWRIGHT_HOSTING=IisExpressWindowsAuth` to run one hosting model at a time.

## Project Structure

```
scripts/
  Start-IISExpress.ps1                                                   # Run a test site under IIS Express with Windows Authentication
src/
  Umbraco.Community.Security.WindowsAuthentication/                      # Shared sources (NOT a project - no .csproj)
    Client/                                                              # Client script (TypeScript, built with Vite)
      public/umbraco-package.json                                        # Package manifest (appEntryPoint, allowPublicAccess)
      src/windows-authentication.ts                                      # Patches fetch and XMLHttpRequest
    WindowsAuthenticationHeaders.cs                                      # The server rules
    WindowsAuthenticationMiddleware.cs                                   # Applies the rules before routing and authentication
    WindowsAuthenticationComposer.cs                                     # Registers the middleware as an Umbraco PrePipeline filter
    wwwroot/                                                             # Built client script and manifest
  Umbraco.Community.Security.WindowsAuthentication.v17/                  # Umbraco 17 package variant (wrapper, no sources)
  Umbraco.Community.Security.WindowsAuthentication.v18/                  # Umbraco 18 package variant (wrapper, no sources)
  Umbraco.Community.Security.WindowsAuthentication.Tests/                # Shared unit test sources (NOT a project)
  Umbraco.Community.Security.WindowsAuthentication.Tests.v17/            # Runs the unit tests against the Umbraco 17 package
  Umbraco.Community.Security.WindowsAuthentication.Tests.v18/            # Runs the unit tests against the Umbraco 18 package
  Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests/      # Shared Playwright test sources (NOT a project)
  Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.v17/  # Runs the Playwright tests against TestSite.v17
  Umbraco.Community.Security.WindowsAuthentication.PlaywrightTests.v18/  # Runs the Playwright tests against TestSite.v18
  Umbraco.Community.Security.WindowsAuthentication.TestSite.v17/         # Umbraco 17 test site (https://localhost:44317)
  Umbraco.Community.Security.WindowsAuthentication.TestSite.v18/         # Umbraco 18 test site (https://localhost:44318)
```

## Guidelines

- The package must only ever affect backoffice requests. Any change to the server rules or the client script needs tests proving that front-end, member, Delivery API and custom authentication are untouched
- Keep the client script free of dependencies and imports: it has to install before the backoffice sends its first authenticated request
- Run the Playwright tests under IIS Express with Windows Authentication for any change, on both Umbraco majors
- Follow the existing code style and patterns
