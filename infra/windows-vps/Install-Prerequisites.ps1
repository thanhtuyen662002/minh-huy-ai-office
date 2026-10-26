#Requires -RunAsAdministrator
<#
Installs the host software for a single Windows 11/Server AI Office host:
WSL2 platform features, IIS (+ URL Rewrite and ARR), Git, PowerShell 7,
.NET SDK (for versioned migrations), Docker Desktop and win-acme.

Safe to re-run: installed components are skipped. A reboot is required after
the first run so WSL2 and IIS features take effect.
#>
param(
    [string]$ToolsDirectory = "C:\AIOffice\tools"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Enable-Feature {
    param([string[]]$Names)
    $restart = $false
    foreach ($name in $Names) {
        $feature = Get-WindowsOptionalFeature -Online -FeatureName $name
        if ($feature.State -ne "Enabled") {
            Write-Host "Enabling Windows feature $name"
            $result = Enable-WindowsOptionalFeature -Online -FeatureName $name -All -NoRestart
            if ($result.RestartNeeded) { $restart = $true }
        }
    }
    return $restart
}

function Install-WingetPackage {
    param([string]$Id)
    & winget list --exact --id $Id --accept-source-agreements *> $null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "$Id already installed"
        return
    }
    Write-Host "Installing $Id"
    & winget install --exact --id $Id --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install $Id (exit code $LASTEXITCODE)."
    }
}

function Install-Msi {
    param([string]$Name, [string]$Url, [string]$InstalledMarker)
    if (Test-Path -LiteralPath $InstalledMarker) {
        Write-Host "$Name already installed"
        return
    }
    $msi = Join-Path $env:TEMP ([IO.Path]::GetFileName($Url))
    Write-Host "Downloading $Name"
    Invoke-WebRequest -Uri $Url -OutFile $msi -UseBasicParsing
    $process = Start-Process msiexec.exe -ArgumentList @("/i", "`"$msi`"", "/qn", "/norestart") -Wait -PassThru
    if ($process.ExitCode -notin @(0, 3010)) {
        throw "$Name installer failed with exit code $($process.ExitCode)."
    }
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw "winget is missing. Install 'App Installer' from the Microsoft Store, then re-run."
}

$restartNeeded = Enable-Feature @(
    "Microsoft-Windows-Subsystem-Linux",
    "VirtualMachinePlatform",
    "IIS-WebServerRole",
    "IIS-WebServer",
    "IIS-CommonHttpFeatures",
    "IIS-StaticContent",
    "IIS-DefaultDocument",
    "IIS-HttpErrors",
    "IIS-HttpLogging",
    "IIS-RequestFiltering",
    "IIS-IPSecurity",
    "IIS-WebSockets",
    "IIS-ManagementConsole"
)

# IIS reverse-proxy modules (Microsoft download center, x64).
Install-Msi -Name "IIS URL Rewrite 2.1" `
    -Url "https://download.microsoft.com/download/1/2/8/128E2E22-C1B9-44A4-BE2A-5859ED1D4592/rewrite_amd64_en-US.msi" `
    -InstalledMarker (Join-Path $env:windir "System32\inetsrv\rewrite.dll")
Install-Msi -Name "IIS Application Request Routing 3.0" `
    -Url "https://download.microsoft.com/download/E/9/8/E9849D6A-020E-47E4-9FD0-A023E99B54EB/requestRouter_amd64.msi" `
    -InstalledMarker (Join-Path $env:ProgramFiles "IIS\Application Request Routing\requestRouter.dll")

Install-WingetPackage "Git.Git"
Install-WingetPackage "Microsoft.PowerShell"
Install-WingetPackage "Microsoft.DotNet.SDK.10"
Install-WingetPackage "Docker.DockerDesktop"

# win-acme issues and renews Let's Encrypt certificates for IIS bindings.
$wacsDirectory = Join-Path $ToolsDirectory "win-acme"
if (-not (Test-Path -LiteralPath (Join-Path $wacsDirectory "wacs.exe"))) {
    Write-Host "Downloading win-acme"
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/win-acme/win-acme/releases/latest" -UseBasicParsing
    $asset = $release.assets | Where-Object { $_.name -match '^win-acme\.v[\d.]+\.x64\.pluggable\.zip$' } | Select-Object -First 1
    if (-not $asset) {
        throw "Unable to find the win-acme x64 pluggable package in the latest release."
    }
    $zip = Join-Path $env:TEMP $asset.name
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zip -UseBasicParsing
    New-Item -ItemType Directory -Force -Path $wacsDirectory | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $wacsDirectory -Force
}

Write-Host ""
Write-Host "Prerequisites are installed."
if ($restartNeeded) {
    Write-Host "Restart Windows now so WSL2 and IIS features take effect."
}
Write-Host "After restart: open Docker Desktop once, enable 'Start Docker Desktop when you sign in',"
Write-Host "then verify virtualization with: docker run --rm hello-world"
