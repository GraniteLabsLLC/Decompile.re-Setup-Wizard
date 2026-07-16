# Client release contract

The setup wizard treats the GitHub release API as transport metadata and the signed manifest as the authority for compatibility and asset contents.

## Manifest

```json
{
  "schema_version": 1,
  "version": "1.0.0",
  "minimum_ida_major": 8,
  "maximum_ida_major": 9,
  "plugin": {
    "name": "decompile-re-ida-plugin-1.0.0.zip",
    "sha256": "64-lowercase-or-uppercase-hex-characters",
    "size": 123456
  },
  "python_dependencies": [
    {
      "runtime_identifier": "win-x64",
      "python_tag": "cp312",
      "name": "decompile-re-dependencies-win-x64-cp312.zip",
      "sha256": "64-lowercase-or-uppercase-hex-characters",
      "size": 123456
    }
  ]
}
```

Supported runtime identifiers are `win-x64`, `win-arm64`, `osx-x64`, `osx-arm64`, `linux-x64`, and `linux-arm64`.

## Plugin archive

The plugin ZIP contains either these entries at its root or inside one top-level directory:

```text
ida_ai_client.py
ida_ai_client/
```

Do not include credentials, caches, local configuration, test output, or compiled Python bytecode.

## Dependency archive

Each dependency ZIP is specific to one OS/architecture/Python ABI:

```text
requirements.lock
wheels/
  dependency-1.2.3-cp312-...whl
```

Every requirement in `requirements.lock` must be pinned and include hashes. Every package must be available in `wheels/`; dependency installation has no network fallback.

## Signature

`release-manifest.sig` is the Base64 encoding of an ECDSA P-256/SHA-256 signature over the exact bytes of `release-manifest.json`.

Generate both files with:

```bash
python scripts/create_release_manifest.py \
  --version 1.0.0 \
  --plugin dist/decompile-re-ida-plugin-1.0.0.zip \
  --dependency win-x64:cp312=dist/decompile-re-dependencies-win-x64-cp312.zip \
  --private-key /secure/path/release-signing.private.pem \
  --output dist
```

Upload the manifest, signature, plugin archive, and every dependency archive to the same published GitHub Release. Drafts and prereleases are not accepted by the production wizard.

## Rotation

Key rotation requires shipping a setup-wizard update containing the new public key before client releases are signed exclusively with the new private key. For a seamless overlap, extend the verifier to trust both the retiring and replacement keys during a bounded transition window.
