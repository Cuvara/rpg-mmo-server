#!/usr/bin/env python3
"""Offline validation of the rpg-mmo Kubernetes manifests.

Why this exists: `kubectl apply --dry-run=client` needs a live API server to
discover CRD types, so on a machine without a cluster it cannot check a Fleet at
all. This script instead pulls the pinned Agones release's install.yaml, lifts
the openAPIV3Schema out of each CustomResourceDefinition and validates our
custom resources against it with jsonschema — the same schema the API server
would use.

Usage:
    python3 validate-manifests.py [--agones-version 1.59.0] [file ...]

With no files it validates deploy/agones/*.yaml and deploy/k3s/*.yaml.
The Agones install.yaml is cached in ~/.cache/rpg-mmo/agones-<version>.yaml.
"""

from __future__ import annotations

import argparse
import os
import pathlib
import sys
import urllib.request

try:
    import yaml
except ImportError:  # pragma: no cover
    sys.exit("PyYAML required: pip install pyyaml")

try:
    import jsonschema
except ImportError:  # pragma: no cover
    sys.exit("jsonschema required: pip install jsonschema")

DEPLOY_DIR = pathlib.Path(__file__).resolve().parent.parent
DEFAULT_AGONES_VERSION = "1.59.0"
INSTALL_URL = (
    "https://raw.githubusercontent.com/googleforgames/agones/"
    "release-{version}/install/yaml/install.yaml"
)

# Native kinds we sanity-check structurally (no cluster => no OpenAPI schema).
REQUIRED_NATIVE_FIELDS = {
    "Namespace": ["metadata.name"],
    "Secret": ["metadata.name"],
    "ConfigMap": ["metadata.name"],
}


def fetch_install_yaml(version: str) -> str:
    cache_dir = pathlib.Path(
        os.environ.get("XDG_CACHE_HOME", pathlib.Path.home() / ".cache")
    ) / "rpg-mmo"
    cache_dir.mkdir(parents=True, exist_ok=True)
    cached = cache_dir / f"agones-{version}.yaml"
    if cached.exists() and cached.stat().st_size > 0:
        return cached.read_text(encoding="utf-8")

    url = INSTALL_URL.format(version=version)
    print(f"    fetching {url}")
    with urllib.request.urlopen(url, timeout=120) as resp:  # noqa: S310
        body = resp.read().decode("utf-8")
    cached.write_text(body, encoding="utf-8")
    return body


def load_crd_schemas(install_yaml: str) -> dict[tuple[str, str], dict]:
    """Return {(apiVersion, kind): jsonschema} for every CRD version served."""
    schemas: dict[tuple[str, str], dict] = {}
    for doc in yaml.safe_load_all(install_yaml):
        if not doc or doc.get("kind") != "CustomResourceDefinition":
            continue
        group = doc["spec"]["group"]
        kind = doc["spec"]["names"]["kind"]
        for ver in doc["spec"].get("versions", []):
            schema = ver.get("schema", {}).get("openAPIV3Schema")
            if schema:
                schemas[(f"{group}/{ver['name']}", kind)] = schema
    return schemas


def sanitize(node):
    """Rewrite an OpenAPI v3.0 schema into something jsonschema accepts.

    Two incompatibilities matter here:
      * x-kubernetes-* / nullable are OpenAPI-only vocabulary.
      * OpenAPI 3.0 keeps draft-4's BOOLEAN exclusiveMinimum/Maximum modifier on
        minimum/maximum; modern JSON Schema wants a number. Agones' CRDs use it
        (e.g. FleetAutoscaler sync.fixedInterval.seconds), so translate it.
    """
    if isinstance(node, dict):
        out = {}
        for key, value in node.items():
            if key.startswith("x-kubernetes-"):
                continue
            # OpenAPI v3 spells nullability differently from JSON Schema.
            if key == "nullable":
                continue
            if key in ("exclusiveMinimum", "exclusiveMaximum") and isinstance(value, bool):
                bound = "minimum" if key == "exclusiveMinimum" else "maximum"
                if value and bound in node:
                    out[key] = node[bound]
                continue
            if key in ("minimum", "maximum"):
                flag = "exclusiveMinimum" if key == "minimum" else "exclusiveMaximum"
                if node.get(flag) is True:
                    continue  # superseded by the exclusive form above
            out[key] = sanitize(value)
        return out
    if isinstance(node, list):
        return [sanitize(v) for v in node]
    return node


def dig(doc: dict, dotted: str):
    node = doc
    for part in dotted.split("."):
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--agones-version", default=DEFAULT_AGONES_VERSION)
    parser.add_argument("files", nargs="*")
    args = parser.parse_args()

    if args.files:
        files = [pathlib.Path(f) for f in args.files]
    else:
        files = sorted((DEPLOY_DIR / "agones").glob("*.yaml")) + sorted(
            (DEPLOY_DIR / "k3s").glob("*.yaml")
        )

    print(f"==> Agones {args.agones_version} CRD schemas")
    schemas = load_crd_schemas(fetch_install_yaml(args.agones_version))
    print(f"    loaded {len(schemas)} CRD versions: "
          + ", ".join(sorted({k for _, k in schemas})))

    failures = 0
    checked = 0
    skipped: list[str] = []

    for path in files:
        try:
            docs = list(yaml.safe_load_all(path.read_text(encoding="utf-8")))
        except yaml.YAMLError as exc:
            print(f"[FAIL] {path.name}: YAML parse error: {exc}")
            failures += 1
            continue

        for doc in docs:
            if not doc:
                continue
            key = (doc.get("apiVersion"), doc.get("kind"))
            label = f"{path.name}:{doc.get('kind')}/{dig(doc, 'metadata.name') or '?'}"

            if key in schemas:
                try:
                    jsonschema.validate(doc, sanitize(schemas[key]))
                except jsonschema.ValidationError as exc:
                    loc = "/".join(str(p) for p in exc.absolute_path) or "<root>"
                    print(f"[FAIL] {label}: {loc}: {exc.message}")
                    failures += 1
                    continue
                print(f"[ ok ] {label} (validated against Agones CRD schema)")
                checked += 1
            elif doc.get("kind") in REQUIRED_NATIVE_FIELDS:
                missing = [
                    f for f in REQUIRED_NATIVE_FIELDS[doc["kind"]] if dig(doc, f) is None
                ]
                if missing:
                    print(f"[FAIL] {label}: missing {', '.join(missing)}")
                    failures += 1
                    continue
                print(f"[ ok ] {label} (structural check)")
                checked += 1
            else:
                skipped.append(label)

    if skipped:
        print(f"\n    skipped (no schema available): {', '.join(skipped)}")

    print(f"\n{checked} document(s) validated, {failures} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
