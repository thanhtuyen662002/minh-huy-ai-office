#Requires -RunAsAdministrator
<#
Host firewall for a single-host deployment: only HTTP/HTTPS are reachable from
the Internet; Remote Desktop is limited to the local subnet. Docker publishes
application ports on 127.0.0.1 only, so they need no inbound rules.
#>
$ErrorActionPreference = "Stop"

$ruleName = "AI Office HTTP/HTTPS (IIS)"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort 80, 443 -Profile Any | Out-Null
}

# Built-in "Remote Desktop" rule group (language-independent resource id).
$rdpRules = Get-NetFirewallRule -Group "@FirewallAPI.dll,-28752" -ErrorAction SilentlyContinue
if ($rdpRules) {
    $rdpRules | Set-NetFirewallRule -RemoteAddress LocalSubnet
    Write-Host "Remote Desktop is limited to the local subnet. Never forward TCP 3389 on the router."
}

Set-NetFirewallProfile -Profile Domain, Private, Public -Enabled True
Write-Host "Firewall configured: inbound TCP 80/443 allowed for IIS."
