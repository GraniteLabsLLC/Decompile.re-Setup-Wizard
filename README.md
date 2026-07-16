# Decompile.re Setup Wizard

Cross-platform installer for the Decompile.re IDA Pro client. The desktop UI is built with Avalonia and the installation engine is isolated in a testable .NET library.

## Current baseline

- Detects IDA Pro on Windows, macOS, and Linux, with manual folder selection for custom installations.
- Detects a compatible Python 3 runtime and whether IDAPython is present.
- Reads the latest public release from `AI-Reversal/IDA-Pro-Client`.
- Requires an ECDSA P-256 signed release manifest.
- Verifies manifest SHA-256 metadata against GitHub release metadata and downloaded bytes.
- Installs platform/Python-specific dependencies from an offline, hash-locked wheel bundle.
- Installs into the user's IDA plugin directory without administrator access.
- Stages updates, backs up an existing installation, and rolls back failed activation.
- Rejects untrusted redirects, oversized downloads, ZIP traversal, and ZIP symlinks.

IDAPython is version-coupled to IDA. The wizard detects it but does not download an arbitrary IDAPython build. A missing IDAPython installation must be repaired through the matching Hex-Rays installer until a version-authenticated Hex-Rays package source is integrated.

## Build

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), then run:

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --no-restore
```

Run the wizard during development:

```powershell
dotnet run --project src/DecompileRe.SetupWizard
```

Create a self-contained Windows build:

```powershell
dotnet publish src/DecompileRe.SetupWizard `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts/win-x64
```

Use `osx-x64`, `osx-arm64`, `linux-x64`, or `linux-arm64` for the other targets. Production Windows and macOS artifacts must be code-signed; macOS artifacts must also be notarized.

Pushing a `v*` tag builds all configured targets into a **draft** GitHub Release. The workflow intentionally does not publish unsigned binaries. Add the platform signing/notarization credentials and steps before promoting a draft to a public release.

## Release input

The client repository's latest GitHub Release must contain:

- `release-manifest.json`
- `release-manifest.sig`
- The plugin ZIP named by the manifest
- A dependency bundle for each supported runtime/Python ABI named by the manifest

Dependency bundles contain `requirements.lock` and a `wheels/` directory. Pip is executed with `--no-index`, `--require-hashes`, and `--only-binary=:all:` so installation cannot resolve mutable packages from the network.

See [docs/RELEASE_FORMAT.md](docs/RELEASE_FORMAT.md) for the schema and signing process.

## Signing key

The repository embeds only `src/DecompileRe.SetupWizard/Assets/release-signing-public-key.pem`. A matching private key was generated locally as `release-signing.private.pem`; `.gitignore` excludes it.

Before creating a release:

1. Move the private key into an organization-controlled secret manager.
2. Add it to the client release workflow as a protected environment secret.
3. Securely remove the local plaintext copy.
4. Keep an offline recovery copy and document key rotation.

Never commit or upload the private key as a release artifact.

## Repository layout

```text
src/DecompileRe.SetupWizard.Core/   Release verification and installation engine
src/DecompileRe.SetupWizard/        Avalonia desktop wizard
tests/                              Security and installer tests
scripts/                            Release-manifest tooling
docs/                               Release contract and operations notes
.github/workflows/                  Locked build and publish automation
```
