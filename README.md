# CodexToys

A PowerToys Command Palette extension for checking local Codex usage at a glance.

## Requirements

- Windows with PowerToys Command Palette enabled.
- .NET 9 SDK.
- Windows SDK UAP platform files.

The install script can install missing prerequisites when run with `-InstallMissing`.

## Build And Install

Run:

```powershell
.\scripts\install.ps1
```

## Troubleshooting

- `0x800B0109`: the signing certificate is not trusted. Rerun `scripts\install.ps1`
  from an elevated PowerShell, or import the generated certificate into trusted stores.
- Package installed but Command Palette does not list the extension: restart
  `Microsoft.CmdPal.UI` and reload extensions.
- No usage appears: confirm Codex logs exist under `%USERPROFILE%\.codex\sessions`,
  or add extra session directories in the extension settings.
