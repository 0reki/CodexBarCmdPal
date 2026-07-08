# CodexBar Command Palette Extension

A small widget for powertoys.

## QuickStart

Run the commands below from the repository root:

Prerequisites:

- PowerToys with Command Palette enabled.
- .NET 9 SDK, or Visual Studio 2022 with the .NET 9 desktop workload.
- CodexBar Desktop running, otherwise the dock bands will show an offline state.

Build:

1. Open `CodexBarCmdPal.sln` in Visual Studio.
2. Select `x64` as the solution platform.
3. Run `Build > Build Solution`.

Create or reuse a local signing certificate:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My |
  Where-Object { $_.Subject -eq 'CN=CodexBarCmdPal' } |
  Select-Object -First 1

if (-not $cert) {
  $cert = New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=CodexBarCmdPal' `
    -KeyUsage DigitalSignature `
    -FriendlyName 'CodexBarCmdPal Test Certificate' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}
```

Package:

1. In Visual Studio, right-click the `CodexBarCmdPal` project.
2. Choose `Publish` or `Package and Publish`.
3. Create an MSIX package for `x64`.
4. Use the `CN=CodexBarCmdPal` signing certificate created above.
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

Get-Process CodexBarCmdPal, Microsoft.CmdPal.UI -ErrorAction SilentlyContinue |
  Stop-Process -Force
Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown
```

Open Command Palette, reload extensions if needed, search for `CodexBar`, then
pin the CodexBar items to the Dock from the command context menu.

## Verify

```powershell
Get-AppxPackage CodexBarCmdPal | Select-Object Name, Version, PackageFullName
Get-Content "$env:LOCALAPPDATA\CodexBarCmdPal\extension.log" -Tail 50
```

## Troubleshooting

- `0x800B0109`: the test certificate is not trusted. Run the certificate import
  commands from an elevated PowerShell.
- Package installed but Command Palette does not list the extension: restart
  `Microsoft.CmdPal.UI`, reload extensions, then check `extension.log`.
- Dock item shows offline: start CodexBar Desktop and confirm the named pipe
  server is running.
