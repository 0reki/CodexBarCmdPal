param(
  [switch]$InstallMissing,
  [switch]$SkipVersionBump,
  [string]$Configuration = "Release",
  [string]$Platform = "x64",
  [string]$RuntimeIdentifier = "win-x64"
)

$ErrorActionPreference = "Stop"

function Write-Step {
  param([string]$Message)
  Write-Host ""
  Write-Host "==> $Message" -ForegroundColor Cyan
}

function Find-DotNet {
  $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($dotnet) {
    return $dotnet.Source
  }

  $defaultPath = "C:\Program Files\dotnet\dotnet.exe"
  if (Test-Path $defaultPath) {
    return $defaultPath
  }

  if (-not $InstallMissing) {
    throw "dotnet was not found. Install .NET 9 SDK, or rerun this script with -InstallMissing."
  }

  Write-Step "Installing .NET 9 SDK"
  winget install --id Microsoft.DotNet.SDK.9 --source winget --accept-package-agreements --accept-source-agreements

  if (Test-Path $defaultPath) {
    return $defaultPath
  }

  $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($dotnet) {
    return $dotnet.Source
  }

  throw "Installed .NET SDK, but dotnet still was not found. Open a new terminal and rerun this script."
}

function Find-WindowsSdkTargetVersion {
  $uapRoot = "C:\Program Files (x86)\Windows Kits\10\Platforms\UAP"
  if (-not (Test-Path $uapRoot)) {
    if (-not $InstallMissing) {
      throw "Windows SDK UAP platforms were not found. Install Windows SDK, or rerun with -InstallMissing."
    }

    Write-Step "Installing Windows SDK"
    winget install --id Microsoft.WindowsSDK --source winget --accept-package-agreements --accept-source-agreements
  }

  if (-not (Test-Path $uapRoot)) {
    throw "Windows SDK UAP platform folder still was not found after install."
  }

  $sdk = Get-ChildItem $uapRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "Platform.xml") } |
    Sort-Object {
      try { [version]$_.Name } catch { [version]"0.0.0.0" }
    } -Descending |
    Select-Object -First 1

  if (-not $sdk) {
    throw "No Windows SDK UAP platform with Platform.xml was found under $uapRoot."
  }

  return $sdk.Name
}

function Ensure-SigningCertificate {
  $cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq "CN=CodexBarCmdPal" } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

  if ($cert) {
    return $cert
  }

  Write-Step "Creating local signing certificate"
  New-SelfSignedCertificate `
    -Type Custom `
    -Subject "CN=CodexBarCmdPal" `
    -KeyUsage DigitalSignature `
    -FriendlyName "CodexBarCmdPal Test Certificate" `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
}

function Bump-PackageVersion {
  param([string]$ManifestPath)

  [xml]$manifest = Get-Content $ManifestPath
  $identity = $manifest.Package.Identity
  $current = [version]$identity.Version
  $next = "{0}.{1}.{2}.{3}" -f $current.Major, $current.Minor, ($current.Build + 1), 0
  $identity.Version = $next
  $manifest.Save($ManifestPath)
  return $next
}

function Test-Admin {
  $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
  $principal = [Security.Principal.WindowsPrincipal]::new($identity)
  return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Trust-CertificateIfPossible {
  param(
    [System.Security.Cryptography.X509Certificates.X509Certificate2]$Cert,
    [string]$CerPath
  )

  Export-Certificate -Cert $Cert -FilePath $CerPath -Force | Out-Null

  $currentUserRoot = Get-ChildItem Cert:\CurrentUser\Root |
    Where-Object { $_.Thumbprint -eq $Cert.Thumbprint } |
    Select-Object -First 1

  if (-not $currentUserRoot) {
    Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
  }

  $currentUserTrustedPeople = Get-ChildItem Cert:\CurrentUser\TrustedPeople |
    Where-Object { $_.Thumbprint -eq $Cert.Thumbprint } |
    Select-Object -First 1

  if (-not $currentUserTrustedPeople) {
    Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null
  }

  if (Test-Admin) {
    $machineRoot = Get-ChildItem Cert:\LocalMachine\Root |
      Where-Object { $_.Thumbprint -eq $Cert.Thumbprint } |
      Select-Object -First 1

    if (-not $machineRoot) {
      Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    }

    $machineTrustedPeople = Get-ChildItem Cert:\LocalMachine\TrustedPeople |
      Where-Object { $_.Thumbprint -eq $Cert.Thumbprint } |
      Select-Object -First 1

    if (-not $machineTrustedPeople) {
      Import-Certificate -FilePath $CerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    }
  } else {
    Write-Host "Not running as administrator; trusted the cert for CurrentUser only." -ForegroundColor Yellow
    Write-Host "If Add-AppxPackage fails with 0x800B0109, rerun PowerShell as administrator." -ForegroundColor Yellow
  }
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "CodexBarCmdPal\CodexBarCmdPal.csproj"
$manifestPath = Join-Path $repoRoot "CodexBarCmdPal\Package.appxmanifest"
$packageRoot = Join-Path $repoRoot "CodexBarCmdPal\AppPackages"
$cerPath = Join-Path $env:TEMP "CodexBarCmdPal.cer"

Write-Step "Checking tools"
$dotnet = Find-DotNet
$windowsTargetVersion = Find-WindowsSdkTargetVersion
Write-Host "dotnet: $dotnet"
Write-Host "Windows SDK target: $windowsTargetVersion"

Write-Step "Preparing signing certificate"
$cert = Ensure-SigningCertificate
Trust-CertificateIfPossible -Cert $cert -CerPath $cerPath
Write-Host "Certificate: $($cert.Thumbprint)"

if (-not $SkipVersionBump) {
  Write-Step "Bumping MSIX package version"
  $nextVersion = Bump-PackageVersion -ManifestPath $manifestPath
  Write-Host "Package version: $nextVersion"
}

Write-Step "Publishing MSIX"
& $dotnet publish $projectPath `
  -c $Configuration `
  -p:Platform=$Platform `
  -p:RuntimeIdentifier=$RuntimeIdentifier `
  -p:CodexBarWindowsTargetVersion=$windowsTargetVersion `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=true `
  -p:PackageCertificateThumbprint=$($cert.Thumbprint)

Write-Step "Installing MSIX"
$pkgDir = Get-ChildItem $packageRoot -Directory |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

if (-not $pkgDir) {
  throw "No package directory was generated under $packageRoot."
}

$msix = Get-ChildItem $pkgDir.FullName -Filter *.msix |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1

if (-not $msix) {
  throw "No .msix package was found under $($pkgDir.FullName)."
}

Get-Process CodexBarCmdPal, Microsoft.CmdPal.UI -ErrorAction SilentlyContinue |
  Stop-Process -Force

Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown

Write-Step "Installed package"
Get-AppxPackage CodexBarCmdPal | Select-Object Name, Version, PackageFullName

Write-Host ""
Write-Host "Open Command Palette and reload extensions if the Dock does not update immediately." -ForegroundColor Green
