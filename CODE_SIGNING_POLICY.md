# Code signing policy

## Official source and releases

The official source repository is
[Gloynus/WallpaperMatrix](https://github.com/Gloynus/WallpaperMatrix).
An official binary release must:

- be built from an immutable version tag on the protected `main` branch;
- be produced by the repository's `release-build` GitHub Actions workflow;
- use dependencies pinned by `packages.lock.json`;
- contain the product name `Wallpaper Matrix` and one consistent product,
  file and assembly version;
- be approved for signing by the project approver;
- be published through the repository's GitHub Releases page together with a
  SHA-256 checksum.

Unsigned local builds and historical unsigned releases are development or
legacy artifacts. They are not represented as signed official releases.

Free code signing provided by
[SignPath.io](https://about.signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

This statement describes the selected signing provider and becomes applicable
to a binary only when Windows reports a valid Authenticode signature for that
specific file.

## Team roles

- Committer and reviewer: [Gloynus](https://github.com/Gloynus)
- Signing approver: [Gloynus](https://github.com/Gloynus)

Changes proposed by anyone other than the committer require review before they
are merged. Signing requests require a separate manual approval by the signing
approver. Multi-factor authentication is required for repository and signing
service access.

## Privacy and system changes

The project privacy policy is published in [PRIVACY.md](PRIVACY.md).
Wallpaper Matrix announces and exposes controls for its optional autostart
registry entry. It installs no service, driver or system library and provides
portable removal instructions in the README.

## Signature verification

For a downloaded release, Windows PowerShell should report `Valid`:

```powershell
Get-AuthenticodeSignature .\WallpaperMatrix.exe |
    Format-List Status, StatusMessage, SignerCertificate
```

The file properties dialog must also show a valid signature on the
**Digital Signatures** tab. A missing or invalid signature must never be
described as an official signed release.
