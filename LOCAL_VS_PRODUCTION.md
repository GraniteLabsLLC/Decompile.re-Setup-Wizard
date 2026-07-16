# Local vs production

## Release signing

- Local development may use the ignored `release-signing.private.pem` generated with this repository.
- Production signing must use the same key from an organization secret manager or a hardware-backed signing service. The private key must never be present in the repository, application bundle, CI logs, or GitHub Release.

## Application signing

- Local `dotnet publish` output is unsigned and intended only for development.
- Production Windows builds require Authenticode signing.
- Production macOS builds require Developer ID signing, hardened runtime, and Apple notarization.
- Production Linux packages should publish detached signatures alongside checksums.

## Release source

Both local and production builds read public releases from `AI-Reversal/IDA-Pro-Client`. Do not add a local unsigned-release bypass; use a separately signed development release if end-to-end testing is required.
