# Windows Authentication

[![Downloads](https://img.shields.io/nuget/dt/Umbraco.Community.Security.WindowsAuthentication?color=cc9900)](https://www.nuget.org/packages/Umbraco.Community.Security.WindowsAuthentication/)
[![NuGet](https://img.shields.io/nuget/vpre/Umbraco.Community.Security.WindowsAuthentication?color=0273B3)](https://www.nuget.org/packages/Umbraco.Community.Security.WindowsAuthentication)
[![GitHub license](https://img.shields.io/github/license/justin-nevitech/Umbraco.Community.Security.WindowsAuthentication?color=8AB803)](https://github.com/justin-nevitech/Umbraco.Community.Security.WindowsAuthentication/blob/main/LICENSE)

An Umbraco package that lets the backoffice run behind IIS Windows Authentication with anonymous authentication **disabled**, for intranet and internal sites where IIS must authenticate every request with a Windows account.

The backoffice sends its own `Authorization: Bearer …` header, which IIS needs for the Negotiate/NTLM handshake. Since Umbraco 17 that header only holds a placeholder; the real token lives in an httpOnly cookie. The package moves the placeholder into a separate header in the browser and moves it back on the server before Umbraco authenticates the request. Only requests to Umbraco's own paths under `/umbraco` are touched; front-end pages, members, the Delivery API and custom authentication are left alone.

## Compatibility

Packaging is **version-aligned**: the package major matches your Umbraco major.

| Umbraco | Package version | Status |
|---------|-----------------|--------|
| 17.x    | `17.x`          | ✅ Supported (requires Umbraco 17.5.0+) |
| 18.x    | `18.x`          | ✅ Supported |
| 19.x    | not needed      | Umbraco 19 relies on cookies only, so the problem goes away |

Umbraco 17.4.0 to 17.4.2 start the backoffice router without waiting for app entry points, so the package's client script could lose a race with the first backoffice requests. The package therefore requires Umbraco 17.5.0 or later on the 17.x line. Each major is compiled against its lowest supported Umbraco release (17.5.0 and 18.0.0) and tested end to end on the latest one (17.6.2 and 18.1.1).

> **Pin the major when installing.** Both majors publish under the same package ID, and NuGet resolves the *latest* version rather than the one matching your Umbraco major, so a bare `dotnet add package` on an Umbraco 17 site will pull the `18.x` package and fail with a `NU1107` version conflict.

## Quick Start

Install the package on Umbraco 17.5+ or 18, pinning the major that matches your Umbraco version:

```
# Umbraco 17
dotnet add package Umbraco.Community.Security.WindowsAuthentication --version "17.*"

# Umbraco 18
dotnet add package Umbraco.Community.Security.WindowsAuthentication --version "18.*"
```

Then, in IIS, disable anonymous authentication and enable Windows Authentication for the site, and browse to `/umbraco`. IIS signs you in with your Windows account, then the normal Umbraco login appears.

## Features

- The backoffice works behind IIS with anonymous authentication disabled and Windows Authentication enabled
- Covers core Management API calls, package APIs under `/umbraco`, uploads and SignalR
- Umbraco's own re-login appears when a backoffice session ends, instead of a Windows credentials prompt
- A failed backoffice sign-in shows Umbraco's "couldn't log you in" message, instead of a Windows credentials prompt
- Only requests to Umbraco's own paths under `/umbraco` are affected: front-end, member, Delivery API and custom authentication are untouched
- Harmless on a site without Windows Authentication
- Tested end to end on Umbraco 17 and 18, under IIS Express with Windows Authentication and under Kestrel

## Configuration

There is nothing to configure in Umbraco. In IIS:

```cmd
%windir%\system32\inetsrv\appcmd.exe set config "My Site" -section:system.webServer/security/authentication/anonymousAuthentication /enabled:false /commit:apphost
%windir%\system32\inetsrv\appcmd.exe set config "My Site" -section:system.webServer/security/authentication/windowsAuthentication /enabled:true /commit:apphost
```

Browsers must trust the site for Windows sign-in, or they prompt for credentials:

- **Edge and Chrome:** the Local Intranet zone or the `AuthServerAllowlist` policy.
- **Firefox:** the `Authentication` enterprise policy (`SPNEGO` and `NTLM` lists), or the `network.negotiate-auth.trusted-uris` and `network.automatic-ntlm-auth.trusted-uris` preferences. Without it, Firefox keeps prompting, because Windows sign-in happens per connection.

Any reverse proxy must forward the `X-Umb-Authorization` request header and `X-Umb-Authorization-Status` response header. Set the `Umbraco.Community.Security.WindowsAuthentication` log level to `Debug` to log each decision.

## What changes on the wire

IIS adds its Windows challenge to every 401, so the package sends two kinds of backoffice 401 under a different status code: a 401 to a relayed backoffice request (an expired session, for example) goes out as `403`, and a failed sign-in behind Windows Authentication goes out as `400`. Both carry `X-Umb-Authorization-Status: 401`. Backoffice code still sees the original 401, but IIS logs, APM, WAF rules and browser devtools see the 403 or 400, so filter on that header if you monitor 401s or 403s. Every other response is unchanged.

## Author

Created and maintained by [Justin Neville](https://www.nevitech.co.uk) at
[Nevitech IT Solutions Ltd](https://www.nevitech.co.uk).

## Documentation

Full documentation and source code available on [GitHub](https://github.com/justin-nevitech/Umbraco.Community.Security.WindowsAuthentication), including an alternative two-zone IIS setup for sites with server-level access.

---

[Security icons created by Magnific - Flaticon](https://www.flaticon.com/free-icons/security "security icons")
