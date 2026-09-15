<#
.SYNOPSIS
    Runs a test site under IIS Express with Windows Authentication on and anonymous authentication off, without Visual Studio.

.DESCRIPTION
    Generates an applicationhost.config in .iisexpress/v<major>/ from the IIS Express template, registers the ASP.NET Core
    Module V2, points a site at the test site project (in-process hosting, like Visual Studio does) and starts IIS Express in
    the foreground. Press Q to stop it.

    The Umbraco backoffice requires HTTPS. The HTTPS port must be in 44300-44399, the range the IIS Express development
    certificate is bound to. The defaults match each test site's launchSettings.json, so both can run at the same time.

.EXAMPLE
    .\scripts\Start-IISExpress.ps1                      # Umbraco 17 test site on https://localhost:44317
    .\scripts\Start-IISExpress.ps1 -UmbracoVersion 18   # Umbraco 18 test site on https://localhost:44318
#>
param(
    [ValidateSet(17, 18)]
    [int] $UmbracoVersion = 17,
    [int] $HttpsPort = 0,
    [int] $HttpPort = 0,
    [string] $Configuration = 'Debug',
    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'

if ($HttpsPort -eq 0) { $HttpsPort = 44300 + $UmbracoVersion }
if ($HttpPort -eq 0) { $HttpPort = 58100 + $UmbracoVersion }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$siteName = "Umbraco.Community.Security.WindowsAuthentication.TestSite.v$UmbracoVersion"
$sitePath = Join-Path $repoRoot "src\$siteName"
$iisExpressPath = Join-Path $env:ProgramFiles 'IIS Express'
$workPath = Join-Path $repoRoot ".iisexpress\v$UmbracoVersion"
$configPath = Join-Path $workPath 'applicationhost.config'

if (-not (Test-Path (Join-Path $iisExpressPath 'Asp.Net Core Module\V2\aspnetcorev2.dll'))) {
    throw 'The ASP.NET Core Module V2 for IIS Express was not found. Install the .NET Hosting Bundle or Visual Studio.'
}

if (-not $NoBuild) {
    dotnet build (Join-Path $sitePath "$siteName.csproj") -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

New-Item -ItemType Directory -Force (Join-Path $workPath 'logs') | Out-Null

$config = Get-Content (Join-Path $iisExpressPath 'config\templates\PersonalWebServer\applicationhost.config') -Raw

$config = $config -replace '(<section name="asp" overrideModeDefault="Deny" />)', "`$1`r`n                <section name=`"aspNetCore`" overrideModeDefault=`"Allow`" />"
$config = $config -replace '(\s*</globalModules>)', "`r`n            <add name=`"AspNetCoreModuleV2`" image=`"%IIS_BIN%\Asp.Net Core Module\V2\aspnetcorev2.dll`" />`$1"
$config = $config -replace '(\s*</modules>)', "`r`n                <add name=`"AspNetCoreModuleV2`" lockItem=`"true`" />`$1"

$sites = @"
<sites>
            <site name="$siteName" id="1" serverAutoStart="true">
                <application path="/">
                    <virtualDirectory path="/" physicalPath="$sitePath" />
                </application>
                <bindings>
                    <binding protocol="https" bindingInformation=":${HttpsPort}:localhost" />
                    <binding protocol="http" bindingInformation=":${HttpPort}:localhost" />
                </bindings>
            </site>
            <siteDefaults>
                <logFile logFormat="W3C" directory="$workPath\logs" />
                <traceFailedRequestsLogging directory="$workPath\trace" enabled="false" />
            </siteDefaults>
            <applicationDefaults applicationPool="Clr4IntegratedAppPool" />
            <virtualDirectoryDefaults allowSubDirConfig="true" />
        </sites>
"@
$config = [regex]::Replace($config, '(?s)<sites>.*?</sites>', [System.Text.RegularExpressions.MatchEvaluator] { param($m) $sites })

$location = @"
    <location path="$siteName">
        <system.webServer>
            <handlers>
                <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
            </handlers>
            <aspNetCore processPath="$sitePath\bin\$Configuration\net10.0\$siteName.exe" arguments="" hostingModel="InProcess" startupTimeLimit="300">
                <environmentVariables>
                    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Development" />
                </environmentVariables>
            </aspNetCore>
            <security>
                <authentication>
                    <anonymousAuthentication enabled="false" />
                    <windowsAuthentication enabled="true" />
                </authentication>
            </security>
        </system.webServer>
    </location>
</configuration>
"@
$config = $config -replace '</configuration>\s*$', $location

Set-Content -Path $configPath -Value $config -Encoding utf8

Write-Host "Umbraco $UmbracoVersion backoffice: https://localhost:$HttpsPort/umbraco (anonymous off, Windows auth on)"
& (Join-Path $iisExpressPath 'iisexpress.exe') "/config:$configPath" "/site:$siteName"
