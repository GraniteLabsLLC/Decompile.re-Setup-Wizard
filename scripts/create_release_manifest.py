#!/usr/bin/env python3
"""Create and sign a Decompile.re client release manifest."""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import pathlib
import subprocess
import tempfile


def asset(path: pathlib.Path) -> dict[str, object]:
    digest = hashlib.sha256()
    size = 0
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
            size += len(chunk)
    return {"name": path.name, "sha256": digest.hexdigest(), "size": size}


def dependency(value: str) -> tuple[str, str, pathlib.Path]:
    platform, separator, path_value = value.partition("=")
    if not separator or ":" not in platform:
        raise argparse.ArgumentTypeError("expected RUNTIME:PYTHON_TAG=PATH")
    runtime_identifier, python_tag = platform.split(":", 1)
    path = pathlib.Path(path_value).resolve(strict=True)
    return runtime_identifier, python_tag, path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--plugin", type=lambda value: pathlib.Path(value).resolve(strict=True), required=True)
    parser.add_argument("--dependency", action="append", type=dependency, default=[])
    parser.add_argument("--private-key", type=lambda value: pathlib.Path(value).resolve(strict=True), required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    parser.add_argument("--minimum-ida-major", type=int, default=8)
    parser.add_argument("--maximum-ida-major", type=int, default=9)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    if args.minimum_ida_major < 8 or args.maximum_ida_major < args.minimum_ida_major:
        raise SystemExit("invalid IDA compatibility range")

    dependencies = []
    for runtime_identifier, python_tag, path in args.dependency:
        descriptor = asset(path)
        descriptor["runtime_identifier"] = runtime_identifier
        descriptor["python_tag"] = python_tag
        dependencies.append(descriptor)

    manifest = {
        "schema_version": 1,
        "version": args.version,
        "minimum_ida_major": args.minimum_ida_major,
        "maximum_ida_major": args.maximum_ida_major,
        "plugin": asset(args.plugin),
        "python_dependencies": dependencies,
    }

    args.output.mkdir(parents=True, exist_ok=True)
    manifest_path = args.output / "release-manifest.json"
    signature_path = args.output / "release-manifest.sig"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8", newline="\n")

    with tempfile.NamedTemporaryFile(delete=False) as temporary_signature:
        temporary_path = pathlib.Path(temporary_signature.name)
    try:
        subprocess.run(
            [
                "openssl",
                "dgst",
                "-sha256",
                "-sign",
                str(args.private_key),
                "-out",
                str(temporary_path),
                str(manifest_path),
            ],
            check=True,
        )
        signature_path.write_bytes(base64.b64encode(temporary_path.read_bytes()) + b"\n")
    finally:
        temporary_path.unlink(missing_ok=True)


if __name__ == "__main__":
    main()
