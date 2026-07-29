# Release and code-signing policy

## Official source

The official repository is
[Gloynus/WallpaperMatrix](https://github.com/Gloynus/WallpaperMatrix).

Every public release must:

- come from an immutable version tag on `main`;
- be built by `.github/workflows/release-build.yml`;
- use dependencies pinned by `packages.lock.json`;
- contain matching product, file and assembly versions;
- publish the portable archive and its SHA-256 checksum together;
- publish release notes matching the version.

The SHA-256 checksum verifies that a downloaded archive matches the release
asset. It does not prove the identity of the publisher.

## Current signature status

An executable is digitally signed only when Windows reports a valid
Authenticode signature for that exact file. A GitHub Release, version tag or
SHA-256 checksum must never be described as a digital signature.

Until a signing provider has issued and applied a certificate, public
Wallpaper Matrix binaries are unsigned. Windows 11 Smart App Control can block
an unsigned executable without offering a per-file bypass.

The intended future provider is:

Free code signing provided by
[SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

This statement records the planned release process; it does not claim that an
existing binary is signed.

## Verification

Checksum:

```powershell
Get-FileHash .\WallpaperMatrix-3.6.5-portable-win-x64.zip -Algorithm SHA256
```

Authenticode:

```powershell
Get-AuthenticodeSignature .\WallpaperMatrix.exe |
    Format-List Status, StatusMessage, SignerCertificate
```

Only `Status: Valid` confirms a valid signature. A missing or invalid signature
must be reported as unsigned.

## Project controls

- Committer, reviewer and signing approver:
  [Gloynus](https://github.com/Gloynus)
- Repository and signing accounts should use multi-factor authentication.
- Third-party changes require review before merge.
- Signing approval, when available, must be a separate explicit action.

Wallpaper Matrix installs no service, driver or system library. Optional
autostart is visible in the interface and belongs only to the current user.
