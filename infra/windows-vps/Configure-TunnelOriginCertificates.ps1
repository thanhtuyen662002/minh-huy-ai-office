<#
Origin certificates for a Cloudflare Tunnel deployment (compose.tunnel.yaml).
Cloudflare terminates public TLS; cloudflared connects to
https://host.docker.internal:443 (No TLS Verify + Origin Server Name), so IIS
only needs a self-signed SNI binding per host. Replaces the win-acme step when
inbound 80/443 is unavailable.
#>
param(
    [Parameter(Mandatory = $true)][string[]]$HostNames
)

$ErrorActionPreference = "Stop"
Import-Module WebAdministration

# powershell.exe -File passes "a,b,c" as one string.
foreach ($name in @($HostNames -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })) {
    $cert = Get-ChildItem Cert:\LocalMachine\My |
        Where-Object { $_.Subject -eq "CN=$name" -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -DnsName $name -CertStoreLocation Cert:\LocalMachine\My `
            -NotAfter (Get-Date).AddYears(5) -KeyExportPolicy NonExportable -FriendlyName "AI Office tunnel origin $name"
    }
    if (-not (Get-WebBinding -Name $name -Protocol https -Port 443 -HostHeader $name)) {
        New-WebBinding -Name $name -Protocol https -Port 443 -HostHeader $name -SslFlags 1
    }
    $binding = Get-WebBinding -Name $name -Protocol https -Port 443 -HostHeader $name
    if ($binding.certificateHash -ne $cert.Thumbprint) { $binding.AddSslCertificate($cert.Thumbprint, "My") }
    Write-Host "HTTPS origin binding for $name -> $($cert.Thumbprint)"
}
