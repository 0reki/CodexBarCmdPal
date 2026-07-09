# CodexToys Command Palette Extension

A small widget for powertoys.

## QuickStart

Run the commands below from the repository root:

Prerequisites:

- PowerToys with Command Palette enabled.
- .NET 9 SDK, or Visual Studio 2022 with the .NET 9 desktop workload.

Build and install:

```powershell
.\scripts\install.ps1
```

Install from a release:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

If .NET SDK or Windows SDK is missing, let the script install what it can through
`winget`:

```powershell
.\scripts\install.ps1 -InstallMissing
```

Manual Visual Studio flow:

1. Open `CodexBarCmdPal.sln` in Visual Studio.
2. Select `x64` as the solution platform.
3. Run `Build > Build Solution`.

Create or reuse a local signing certificate:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My |
  Where-Object { $_.Subject -eq 'CN=CodexToys' } |
  Select-Object -First 1

if (-not $cert) {
  $cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=CodexToys' `
    -KeyUsage DigitalSignature `
    -FriendlyName 'CodexToys Test Certificate' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}
```

Package:

1. In Visual Studio, right-click the `CodexBarCmdPal` project.
2. Choose `Publish` or `Package and Publish`.
3. Create an MSIX package for `x64`.
4. Use the `CN=CodexToys` signing certificate created above.
5. Finish the wizard. The package will be written under
   `CodexBarCmdPal\AppPackages`.

Trust the generated certificate once from an elevated PowerShell:

```powershell
$pkgDir = Get-ChildItem .\CodexBarCmdPal\AppPackages -Directory |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
$cer = Get-ChildItem $pkgDir.FullName -Filter *.cer | Select-Object -First 1

Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath $cer.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Install or update:

```powershell
$pkgDir = Get-ChildItem .\CodexBarCmdPal\AppPackages -Directory |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
$msix = Get-ChildItem $pkgDir.FullName -Filter *.msix | Select-Object -First 1

Get-Process CodexToys, Microsoft.CmdPal.UI -ErrorAction SilentlyContinue |
  Stop-Process -Force
Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
```

Open Command Palette, reload extensions if needed, search for `CodexToys`, then
pin the CodexToys items to the Dock from the command context menu.

## Verify

```powershell
Get-AppxPackage CodexToys | Select-Object Name, Version, PackageFullName
Get-Content "$env:LOCALAPPDATA\CodexToys\extension.log" -Tail 50
```

## Troubleshooting

- `0x800B0109`: the test certificate is not trusted. Run the certificate import
  commands from an elevated PowerShell.
- Package installed but Command Palette does not list the extension: restart
  `Microsoft.CmdPal.UI`, reload extensions, then check `extension.log`.
- No usage appears: confirm Codex has local logs under `%USERPROFILE%\.codex\sessions`,
  or add extra session directories in the extension settings.
