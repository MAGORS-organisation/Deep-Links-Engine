#!/usr/bin/env python3
"""Validate CycloneDX JSON documents against the official 1.6 schema.

Uses cyclonedx-python-lib's bundled schemas (strict JSON validation), so no schema download and no
network access is needed at validation time. Every file given on the command line is validated; the
exit code is non-zero if any of them fails.

Usage:
  pip install "cyclonedx-python-lib[json-validation]==<pinned>"
  validate-cdx.py sbom/*.cdx.json
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

from cyclonedx.schema import SchemaVersion
from cyclonedx.validation.json import JsonStrictValidator

VERSIONS = {
    "1.6": SchemaVersion.V1_6,
    "1.5": SchemaVersion.V1_5,
    "1.4": SchemaVersion.V1_4,
}


def main(paths: list[str]) -> int:
    if not paths:
        print("usage: validate-cdx.py <file.cdx.json> [...]", file=sys.stderr)
        return 2

    failed = 0
    for raw in paths:
        path = Path(raw)
        text = path.read_text(encoding="utf-8")
        try:
            spec = json.loads(text).get("specVersion", "1.6")
        except json.JSONDecodeError as error:
            print(f"::error file={path}::not JSON: {error}")
            failed += 1
            continue
        version = VERSIONS.get(spec)
        if version is None:
            print(f"::error file={path}::unsupported specVersion {spec!r}")
            failed += 1
            continue
        error = JsonStrictValidator(version).validate_str(text)
        if error is None:
            document = json.loads(text)
            components = len(document.get("components", []))
            print(f"ok   {path} (CycloneDX {spec}, {components} components)")
        else:
            print(f"::error file={path}::CycloneDX {spec} validation failed: {error}")
            failed += 1
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
