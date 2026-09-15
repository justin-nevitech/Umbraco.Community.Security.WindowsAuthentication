# Windows Authentication

[![Downloads](https://img.shields.io/nuget/dt/Umbraco.Community.Security.WindowsAuthentication?color=cc9900)](https://www.nuget.org/packages/Umbraco.Community.Security.WindowsAuthentication/)
[![NuGet](https://img.shields.io/nuget/vpre/Umbraco.Community.Security.WindowsAuthentication?color=0273B3)](https://www.nuget.org/packages/Umbraco.Community.Security.WindowsAuthentication)
[![GitHub license](https://img.shields.io/github/license/justin-nevitech/Umbraco.Community.Security.WindowsAuthentication?color=8AB803)](../LICENSE)

An Umbraco package that lets the backoffice run behind IIS Windows Authentication with anonymous authentication **disabled**, for intranet and internal sites where IIS must authenticate every request with a Windows account.

Without it the backoffice cannot load. It sends its own `Authorization: Bearer …` header on every Management API call, and IIS needs the `Authorization` header to itself for the Negotiate/NTLM handshake. The package moves the backoffice token into a separate header in the browser and moves it back on the server before Umbraco authenticates the request. Front-end pages, members, the Delivery API and any custom authentication are left untouched.

## Compatibility

The package builds from a single codebase against both Umbraco 17 and 18. Packaging is **version-aligned**: the package major matches your Umbraco major.

| Umbraco | Package version | Status |
|---------|-----------------|--------|
| 17.x    | `17.x`          | ✅ Supported (requires Umbraco 17.5.0+, see [Why the ordering holds](#why-the-ordering-holds)) |
| 18.x    | `18.x`          | ✅ Supported |

There is no version-specific code in the package. Each major is compiled against its lowest supported Umbraco release (17.5.0 and 18.0.0) and tested end to end on the latest one (17.6.2 and 18.1.1). See [docs/BUILDING.md](../docs/BUILDING.md) for the technical details.

> **Pin the major when installing.** Both majors publish under the same package ID, and NuGet resolves the *latest* version rather than the one matching your Umbraco major — so a bare `dotnet add package` on an Umbraco 17 site will pull the `18.x` package and fail with a `NU1107` version conflict. Specify the major you need (see [Installation](#installation)).

## Features

- The backoffice works behind IIS with anonymous authentication disabled and Windows Authentication enabled
- Covers every backoffice request that carries a bearer token: core Management API calls, package API clients, uploads over `XMLHttpRequest`, and SignalR
- When a backoffice session ends, Umbraco's own re-login appears, instead of a browser prompt for Windows credentials
- Only backoffice requests are affected: front-end pages, members, public access, the Delivery API and custom JWT, API key or Basic authentication are untouched
- Harmless on a site without Windows Authentication, such as Kestrel in local development
- Nothing to configure in Umbraco: install the package, then configure IIS
- Tested end to end on Umbraco 17 and 18, under IIS Express with Windows Authentication and under Kestrel

## Installation

Add the package to an existing Umbraco website (v17.5+ — see [Compatibility](#compatibility)) from NuGet, pinning the major that matches your Umbraco version:

```
# Umbraco 17
dotnet add package Umbraco.Community.Security.WindowsAuthentication --version "17.*"

# Umbraco 18
dotnet add package Umbraco.Community.Security.WindowsAuthentication --version "18.*"
```

The `"17.*"` / `"18.*"` floating version keeps you on the line built for your Umbraco major while still picking up its latest patch. Omitting `--version` resolves to the highest version published overall, which will be the wrong major for Umbraco 17 sites.

## Usage

1. Install the package and deploy the site
2. In IIS, disable anonymous authentication and enable Windows Authentication for the site (see [Configuration](#configuration))
3. Make sure browsers sign in to the site automatically, via the Local Intranet zone or the `AuthServerAllowlist` policy
4. Browse to `/umbraco`: IIS signs you in with your Windows account, then the normal Umbraco login appears

The package does not sign people in to the backoffice with their Windows account. It keeps the backoffice working behind Windows Authentication; backoffice users still sign in with their Umbraco account or any external login provider you have configured.

## Configuration

The package has no settings. The configuration is in IIS.

**Authentication.** Disable anonymous authentication and enable Windows Authentication for the site. Both sections are locked at server level by default, so either commit the change to `applicationHost.config`:

```cmd
%windir%\system32\inetsrv\appcmd.exe set config "My Site" -section:system.webServer/security/authentication/anonymousAuthentication /enabled:false /commit:apphost
%windir%\system32\inetsrv\appcmd.exe set config "My Site" -section:system.webServer/security/authentication/windowsAuthentication /enabled:true /commit:apphost
```

or unlock them and set them in the site's `web.config`:

```xml
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <security>
        <authentication>
          <anonymousAuthentication enabled="false" />
          <windowsAuthentication enabled="true" />
        </authentication>
      </security>
    </system.webServer>
  </location>
</configuration>
```

**Browsers.** Browsers only send Windows credentials automatically to trusted hosts: add the site to the Local Intranet zone, or set the `AuthServerAllowlist` policy for Edge and Chrome. Otherwise users get a credentials prompt whether or not the package is installed.

**Reverse proxies and load balancers.** Anything between the browser and IIS must forward the `X-Umb-Authorization` request header and the `X-Umb-Authorization-Status` response header.

**Backoffice on another origin.** If `server-url` points the backoffice at a different origin, that server's CORS policy must allow the `X-Umb-Authorization` request header and expose `X-Umb-Authorization-Status`.

**Logging.** Set the `Umbraco.Community.Security.WindowsAuthentication` log level to `Debug` to log the decision for every request that carries the backoffice header:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Override": {
        "Umbraco.Community.Security.WindowsAuthentication": "Debug"
      }
    }
  }
}
```

## How it works

There are two small parts. Neither needs Umbraco core or other packages to cooperate.

| Side | What |
| --- | --- |
| Browser | An `appEntryPoint` in a package with `allowPublicAccess: true` patches `window.fetch` and `XMLHttpRequest` as soon as the module loads. For requests to the backoffice origin (the page origin, or the origin in `umb-app`'s `server-url`) it moves `Authorization: Bearer <token>` into `X-Umb-Authorization`, and turns a `403` carrying `X-Umb-Authorization-Status: 401` back into the original `401`. |
| Server | A composer adds an Umbraco `PrePipeline` filter. Its middleware moves the value back into `Authorization` before routing and authentication, so OpenIddict and every `[Authorize]` controller see a normal request. For those requests it sends a `401` response as `403` with `X-Umb-Authorization-Status: 401`, because IIS adds `WWW-Authenticate: Negotiate, NTLM` to **every** 401 the application returns, and the browser would answer that challenge with a Windows credentials prompt instead of letting Umbraco show its re-login. |

IIS never sees a bearer header, so its Windows Authentication completes as normal. Backoffice code always gets the real 401, so Umbraco's own re-authentication runs when a session ends.

### Only backoffice requests are affected

The middleware sees every request, but it changes a request only when **all** of these are true. Every other request passes through untouched.

1. **The request carries `X-Umb-Authorization`.** Only the client script sends that header. Front-end pages, members, the Delivery API and custom API clients never do.
2. **The header holds exactly one value, and it's a Bearer token.** `Basic …`, a bare `Bearer` with no token, or two header values are all left alone.
3. **`Authorization` is safe to set.** Either the request has no `Authorization` header, or it has a `Negotiate`/`NTLM` one that IIS or HTTP.sys has already used to authenticate the request. A `Bearer`, `Basic` or custom-scheme header is never replaced, and nor is a `Negotiate` header that the ASP.NET Core Negotiate handler still has to read.

A 401 to any request the middleware didn't change, such as a front-end page, a member API or the Delivery API, stays a 401.

In the browser, the client script loads only in the backoffice app (including `/umbraco/preview`), never on front-end pages. It rewrites only backoffice-origin requests with a `Bearer <token>` header; every other `fetch` receives the exact arguments it was called with, and a real 403 is never changed.

### Does all backoffice communication go through the package?

Every place the backoffice sets an `Authorization` header goes through the package. The other channels don't carry one, so the browser handles Windows Authentication for them natively.

| Channel | How it authenticates | Covered |
| --- | --- | --- |
| Core Management API calls (`umbHttpClient` and the generated SDK) | hey-api client sets `Authorization`; `fetch` is resolved from `globalThis.fetch` on each request | ✅ relayed |
| Package clients generated with hey-api and configured with the auth context | Same client code as core | ✅ relayed |
| Package code calling `fetch` with a backoffice token | `fetch` | ✅ relayed |
| Core uploads (`tryXhrRequest`), axios, other XHR code | `XMLHttpRequest.setRequestHeader` | ✅ relayed |
| SignalR negotiate and long polling | SignalR's fetch client | ✅ relayed |
| SignalR WebSocket / Server-Sent Events | `access_token` query string plus cookie | ✅ not needed |
| Login, `authorize`, `token`, `revoke` | Forms, redirects and cookies | ✅ not needed |
| Page loads, static assets, media, preview iframe, downloads | Navigation and cookies | ✅ not needed |

### Why the ordering holds

The client script has to be installed before the backoffice sends its first authenticated request. When the backoffice boots, it:

1. calls `server/status` and `server/configuration` (anonymous);
2. calls `manifest/public` (anonymous), which registers `appEntryPoint`s from packages with `allowPublicAccess`;
3. imports those entry points and **waits for them** before starting the router;
4. only then calls `manifest/private`, `user/current`, the SignalR hub and everything else that sends a bearer header.

A `backofficeEntryPoint` would lose this race: it is delivered by `manifest/private`, which already needs the bearer header.

The wait in step 3 is missing from **Umbraco 17.4.0–17.4.2**, so on those versions the first authenticated requests could race the client script, and behind Windows Authentication a request that lost the race would be refused by IIS. That is why the package requires Umbraco 17.5.0 or later. Every later 17.x release and every 18.x release checked waits for app entry points.

### Known limitations

- **Workers and iframes.** A Web Worker, Service Worker or iframe with its own `window` that calls the API with a bearer header uses its own `fetch`, which is not patched. Core does not do this; a package could.
- **Captured `fetch` references.** Code that stored a reference to `fetch` before the client script ran bypasses it. Only another public `appEntryPoint` can load that early.
- **Same-origin paths served by another application.** A backoffice call to a path on the same host that is served by a different application (a separate IIS application or a reverse-proxied service) has its bearer header moved, and nothing on that application moves it back.
- **Not yet tested:** full IIS (tested on IIS Express, in-process), Kerberos (tested with NTLM on localhost), Chrome and Firefox (tested with Edge), reverse proxies and load balancers, and a backoffice on a separate origin.

## Performance

- **Server.** One header lookup per request. Requests without `X-Umb-Authorization` pass straight through; backoffice requests get one header copy and a response callback that only acts on a 401.
- **Browser.** The client script is 3.5 kB (1.5 kB gzipped), loaded once with the backoffice. Each backoffice request gets one header rename and no extra requests.

## Architecture

```
Browser (Backoffice)                    IIS                          Umbraco (ASP.NET Core)
+----------------------------+          +----------------------+     +-----------------------------------+
| windows-authentication.js  |          | Windows              |     | WindowsAuthenticationMiddleware   |
|  (public appEntryPoint)    |  request | Authentication       |     |  (Umbraco PrePipeline filter)     |
|  - patches fetch and XHR   | -------> |  - Negotiate / NTLM  | --> |  - X-Umb-Authorization            |
|  - Authorization: Bearer   |          |  - never sees the    |     |      -> Authorization             |
|      -> X-Umb-Authorization|          |    bearer header     |     |  - 401 -> 403 + X-Umb-            |
|                            | response |  - adds a Windows    |     |      Authorization-Status: 401    |
|  - 403 + status marker     | <------- |    challenge to 401s | <-- |                                   |
|      -> 401                |          |    only              |     | OpenIddict and [Authorize] see a  |
+----------------------------+          +----------------------+     | normal backoffice request         |
                                                                     +-----------------------------------+
```

## Troubleshooting

**Installing the package fails with a version conflict (`NU1107` or `NU1605`)**
- The `17.x` package requires Umbraco 17.5.0 or later. Upgrade the site's Umbraco packages first
- Check you pinned the package major that matches your Umbraco major (see [Installation](#installation))

**The backoffice doesn't load, or its requests fail with 401**
- Check the browser console for `[WindowsAuthentication] installed after … ms`. If it's missing, the client script didn't load: check that `/App_Plugins/WindowsAuthentication/` is served and that the package appears in the `manifest/public` response
- A `[WindowsAuthentication] … started before the client script was installed` warning means a backoffice request raced the script. The package relies on the boot order above, so check the site runs a [supported Umbraco version](#compatibility)
- Enable `Debug` logging (see [Configuration](#configuration)). `InvalidBackOfficeHeader` or `AuthorizationHeaderInUse` means the request was deliberately left alone
- Check that any reverse proxy, WAF or load balancer forwards the `X-Umb-Authorization` header

**The browser asks for Windows credentials**
- Add the site to the Local Intranet zone or the `AuthServerAllowlist` policy
- A front-end page that returns 401 still gets IIS's Windows challenge. That's IIS behaviour for any site with Windows Authentication; the package deliberately doesn't change front-end responses

**A package's backoffice API calls fail**
- Check whether the package calls the API from a Web Worker, Service Worker or its own iframe, or captured `fetch` before the client script ran (see [Known limitations](#known-limitations))

**Every backoffice page load shows one `400` from `security/back-office/token`**
- That is the backoffice trying to refresh a session that doesn't exist yet. It happens with or without the package

## Author

Created and maintained by [Justin Neville](https://www.nevitech.co.uk) at
[Nevitech IT Solutions Ltd](https://www.nevitech.co.uk).

## Contributing

Contributions to this package are most welcome! Please read the [Contributing Guidelines](CONTRIBUTING.md).

## Acknowledgments

- Structured following the [Opinionated Package Starter](https://github.com/LottePitcher/opinionated-package-starter)
- Tested with [Playwright for .NET](https://playwright.dev/dotnet/) and [NUnit](https://nunit.org/)
