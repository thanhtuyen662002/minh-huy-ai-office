#Requires -RunAsAdministrator
<#
Configures IIS as the only public entry point for a single-host deployment:

  https://<WebHost>      -> http://127.0.0.1:<WebPort>       (Next.js web)
  https://<ApiHost>      -> http://127.0.0.1:<ApiPort>       (Core.Api)
  https://<IdentityHost> -> http://127.0.0.1:<IdentityPort>  (Keycloak)

HTTP redirects to HTTPS except ACME challenges. The proxy overwrites the
X-Forwarded-* headers (Keycloak runs with KC_PROXY_HEADERS=xforwarded). The
Keycloak administration console is reachable only from LanPrefix addresses.

With -RequestCertificates, win-acme issues Let's Encrypt certificates, adds the
HTTPS bindings and registers its own renewal task. DNS A records for every host
and router forwarding of TCP 80/443 to this machine must already be in place.
#>
param(
    [Parameter(Mandatory = $true)][string]$WebHost,
    [Parameter(Mandatory = $true)][string]$ApiHost,
    [Parameter(Mandatory = $true)][string]$IdentityHost,
    [int]$WebPort = 3000,
    [int]$ApiPort = 8080,
    [int]$IdentityPort = 8081,
    # Leading octets of the office LAN allowed to open the Keycloak admin console, e.g. "192.168.68."
    [Parameter(Mandatory = $true)][string]$LanPrefix,
    [string]$SitesRoot = "C:\AIOffice\iis",
    [switch]$RequestCertificates,
    [string]$CertificateEmail,
    [string]$WinAcmePath = "C:\AIOffice\tools\win-acme\wacs.exe"
)

$ErrorActionPreference = "Stop"
Import-Module WebAdministration

if (-not (Test-Path -LiteralPath (Join-Path $env:windir "System32\inetsrv\rewrite.dll"))) {
    throw "IIS URL Rewrite is not installed. Run Install-Prerequisites.ps1 first."
}

$appHost = "MACHINE/WEBROOT/APPHOST"

# ARR proxy: keep the public Host, do not buffer streamed responses, and leave
# X-Forwarded-For to the rewrite rule so it is overwritten rather than appended.
Set-WebConfigurationProperty -PSPath $appHost -Filter "system.webServer/proxy" -Name "enabled" -Value $true
Set-WebConfigurationProperty -PSPath $appHost -Filter "system.webServer/proxy" -Name "preserveHostHeader" -Value $true
Set-WebConfigurationProperty -PSPath $appHost -Filter "system.webServer/proxy" -Name "reverseRewriteHostInResponseHeaders" -Value $false
Set-WebConfigurationProperty -PSPath $appHost -Filter "system.webServer/proxy" -Name "responseBufferLimit" -Value 0
Set-WebConfigurationProperty -PSPath $appHost -Filter "system.webServer/proxy" -Name "timeout" -Value "00:02:00"
Set-WebConfigurationProperty -PSPath $appHost -Filter "system.webServer/proxy" -Name "xForwardedForHeaderName" -Value "X-ARR-Client-IP"

$forwardedVariables = @(
    "HTTP_X_FORWARDED_FOR",
    "HTTP_X_FORWARDED_PROTO",
    "HTTP_X_FORWARDED_HOST",
    "HTTP_X_FORWARDED_PORT"
)
foreach ($variable in $forwardedVariables) {
    $existing = Get-WebConfiguration -PSPath $appHost -Filter "system.webServer/rewrite/allowedServerVariables/add[@name='$variable']"
    if (-not $existing) {
        Add-WebConfigurationProperty -PSPath $appHost -Filter "system.webServer/rewrite/allowedServerVariables" -Name "." -Value @{ name = $variable }
    }
}

# The default site answers every host name on port 80; only named sites stay public.
if (Get-Website -Name "Default Web Site" -ErrorAction SilentlyContinue) {
    Stop-Website -Name "Default Web Site"
    Set-ItemProperty "IIS:\Sites\Default Web Site" -Name serverAutoStart -Value $false
}

function New-ProxyWebConfig {
    param([int]$Port, [bool]$RestrictKeycloakAdmin)

    $lanPattern = "^" + [regex]::Escape($LanPrefix)
    $adminRule = ""
    if ($RestrictKeycloakAdmin) {
        $adminRule = @"
        <rule name="Keycloak admin from LAN only" stopProcessing="true">
          <match url="^admin(/.*)?$" />
          <conditions>
            <add input="{REMOTE_ADDR}" pattern="$lanPattern" negate="true" />
          </conditions>
          <action type="CustomResponse" statusCode="403" statusReason="Forbidden" statusDescription="Administration is available on the office network only." />
        </rule>
"@
    }

    return @"
<?xml version="1.0" encoding="UTF-8"?>
<configuration>
  <system.webServer>
    <rewrite>
      <rules>
        <rule name="ACME challenge" stopProcessing="true">
          <match url="^\.well-known/acme-challenge/" />
          <action type="None" />
        </rule>
        <rule name="HTTPS only" stopProcessing="true">
          <match url="(.*)" />
          <conditions>
            <add input="{HTTPS}" pattern="off" />
          </conditions>
          <action type="Redirect" url="https://{HTTP_HOST}/{R:1}" redirectType="Permanent" />
        </rule>
$adminRule        <rule name="Proxy to loopback service" stopProcessing="true">
          <match url="(.*)" />
          <serverVariables>
            <set name="HTTP_X_FORWARDED_FOR" value="{REMOTE_ADDR}" />
            <set name="HTTP_X_FORWARDED_PROTO" value="https" />
            <set name="HTTP_X_FORWARDED_HOST" value="{HTTP_HOST}" />
            <set name="HTTP_X_FORWARDED_PORT" value="443" />
          </serverVariables>
          <action type="Rewrite" url="http://127.0.0.1:$Port/{R:1}" />
        </rule>
      </rules>
    </rewrite>
    <security>
      <requestFiltering allowDoubleEscaping="true">
        <requestLimits maxAllowedContentLength="52428800" />
      </requestFiltering>
    </security>
    <httpProtocol>
      <customHeaders>
        <add name="Strict-Transport-Security" value="max-age=31536000" />
      </customHeaders>
    </httpProtocol>
  </system.webServer>
</configuration>
"@
}

$sites = @(
    @{ Host = $WebHost; Port = $WebPort; Keycloak = $false },
    @{ Host = $ApiHost; Port = $ApiPort; Keycloak = $false },
    @{ Host = $IdentityHost; Port = $IdentityPort; Keycloak = $true }
)

foreach ($site in $sites) {
    $name = $site.Host
    $path = Join-Path $SitesRoot $name
    New-Item -ItemType Directory -Force -Path $path | Out-Null
    Set-Content -LiteralPath (Join-Path $path "web.config") -Value (New-ProxyWebConfig -Port $site.Port -RestrictKeycloakAdmin $site.Keycloak) -Encoding UTF8

    if (-not (Test-Path "IIS:\AppPools\$name")) {
        New-WebAppPool -Name $name | Out-Null
    }
    Set-ItemProperty "IIS:\AppPools\$name" -Name managedRuntimeVersion -Value ""

    if (-not (Get-Website -Name $name -ErrorAction SilentlyContinue)) {
        New-Website -Name $name -PhysicalPath $path -HostHeader $name -Port 80 -ApplicationPool $name | Out-Null
    }
    Start-Website -Name $name
    Write-Host "IIS site $name -> http://127.0.0.1:$($site.Port)"
}

if ($RequestCertificates) {
    if (-not $CertificateEmail) {
        throw "-CertificateEmail is required with -RequestCertificates (Let's Encrypt expiry notices)."
    }
    if (-not (Test-Path -LiteralPath $WinAcmePath)) {
        throw "win-acme not found at $WinAcmePath. Run Install-Prerequisites.ps1 first."
    }
    foreach ($site in $sites) {
        $siteId = (Get-Website -Name $site.Host).id
        & $WinAcmePath --source iis --siteid $siteId --host $site.Host --installation iis `
            --emailaddress $CertificateEmail --accepttos --closeonfinish
        if ($LASTEXITCODE -ne 0) {
            throw "win-acme failed for $($site.Host). Check DNS and that TCP 80 reaches this machine from the Internet."
        }
    }
}

Write-Host ""
Write-Host "IIS reverse proxy is configured."
if (-not $RequestCertificates) {
    Write-Host "Re-run with -RequestCertificates -CertificateEmail <address> once DNS and port forwarding are live."
}
