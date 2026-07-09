$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$msix = Get-ChildItem $root -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$cer = Get-ChildItem $root -Filter *.cer | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $msix) {
  throw "No .msix package was found next to this script."
}

if (-not $cer) {
  throw "No .cer certificate was found next to this script."
}

Write-Host "Trusting certificate for CurrentUser..."
Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\CurrentUser\TrustedPeople | Out-Null

Write-Host "Stopping Command Palette..."
Get-Process CodexToys, Microsoft.CmdPal.UI -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Installing $($msix.Name)..."
Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown

Write-Host ""
Get-AppxPackage CodexToys | Select-Object Name, Version, PackageFullName
Write-Host ""
Write-Host "Installed. Open Command Palette and reload extensions if CodexToys does not appear immediately."
