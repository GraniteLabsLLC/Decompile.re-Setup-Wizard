# Decompile.re Setup Wizard

Official cross-platform installer for the Decompile.re clients for IDA Pro,
Binary Ninja, and Ghidra.

## Download

Download the latest installer for your operating system and architecture from
[GitHub Releases](https://github.com/GraniteLabsLLC/Decompile.re-Setup-Wizard/releases/latest).

| Platform | Asset |
| --- | --- |
| Windows x64 | `Decompile.re-Setup-Wizard-win-x64.zip` |
| Windows ARM64 | `Decompile.re-Setup-Wizard-win-arm64.zip` |
| macOS Intel | `Decompile.re-Setup-Wizard-osx-x64.tar.gz` |
| macOS Apple Silicon | `Decompile.re-Setup-Wizard-osx-arm64.tar.gz` |
| Linux x64 | `Decompile.re-Setup-Wizard-linux-x64.tar.gz` |
| Linux ARM64 | `Decompile.re-Setup-Wizard-linux-arm64.tar.gz` |

Verify the downloaded file against `SHA256SUMS.txt` from the same release before running it.

## Installation

1. Extract the downloaded archive.
2. Run `Decompile.re-Setup-Wizard`.
3. Select the detected IDA Pro, Binary Ninja, or Ghidra installation.
4. Review the installation and continue.
5. Restart the selected application after installation completes.

The wizard installs the appropriate Decompile.re client into the current user's
plugin directory and does not require administrator access.

## Requirements

- IDA Pro 8.x or 9.x, Binary Ninja, or Ghidra.
- The scripting runtime required by the selected application. IDA Pro requires
  IDAPython installed for the selected IDA version.
- An internet connection for retrieving the signed Decompile.re client release.

IDAPython is tied to the installed IDA version. If it is missing, repair the IDA installation using the matching Hex-Rays installer before running the wizard again.

## Security

The wizard accepts only plugin releases covered by the embedded Decompile.re release-signing key. It verifies signed manifests, asset sizes, and SHA-256 hashes before installing files. Dependencies are installed from platform-specific, hash-locked offline bundles without a package-index fallback.
