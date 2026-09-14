#!/usr/bin/env python3
"""Offline validation of the rpg-mmo Kubernetes manifests.

Why this exists: `kubectl apply --dry-run=client` needs a live API server to
discover CRD types, so on a machine without a cluster it cannot check a Fleet at
all. This script instead pulls the pinned Agones release's install.yaml, lifts
the openAPIV3Schema out of each CustomResourceDefinition and validates our
custom resources against it with jsonschema — the same schema the API server
would use.

Schema validity is the cheap half. A Fleet can be perfectly schema-valid and
still be wrong in ways that only show up as a client that cannot connect, so the
CONTRACT checks below (check_fleet / check_autoscaler / check_allocation) assert
the project-specific invariants no schema knows about: the port named "game"
that the gateway's allocator selects by, secrets that must not be literals, the
GAMESERVER_PUBLIC_ADDR that must stay absent under portPolicy: Dynamic, and the
autoscaler policy ADR-14 decision 5 restricts to Buffer.

Usage:
    python3 validate-manifests.py [--agones-version 1.59.0] [file ...]
    python3 validate-manifests.py --check-image rpg-mmo/gameserver-dotnet:dev \
                                  --expect-revision "$(git rev-parse HEAD)"

With no files it validates deploy/agones/*.yaml and deploy/k3s/*.yaml.
The Agones install.yaml is cached in ~/.cache/rpg-mmo/agones-<version>.yaml.

This does NOT replace `kubectl apply --dry-run=server -f <file>`, which
validates against the real Agones CRDs and admission webhooks on the target
cluster. Run both; this one works without a cluster, that one is stronger.
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


# --------------------------------------------------------------------------
# Contract checks — the invariants the CRD schema cannot express
# --------------------------------------------------------------------------

# Must match gateway/registry/agones_allocator.go. Kept as constants here rather
# than parsed out of the Go source so the check works with no Go module present;
# check_gateway_constants() below reads the Go file when it is there and reports
# any drift between the two.
GATEWAY_NAMESPACE = "rpg-realtime"
GATEWAY_FLEET_LABEL = "agones.dev/fleet"
GATEWAY_GAME_PORT_NAME = "game"
GATEWAY_ALLOCATOR_GO = "gateway/registry/agones_allocator.go"

# Env vars that carry a secret. A literal `value:` for any of these puts the
# secret in git; they must come from a secretKeyRef.
SECRET_ENV = {"JWT_SECRET", "JOIN_TOKEN_SECRET", "TRANSPORT_KEY", "REDIS_PASSWORD", "GAME_DB_URL"}


def _containers(fleet: dict) -> list:
    return (
        dig(fleet, "spec.template.spec.template.spec.containers") or []
    )


def check_fleet(doc: dict, label: str) -> list[str]:
    """Project invariants for an Agones Fleet. Returns a list of problems."""
    problems: list[str] = []
    name = dig(doc, "metadata.name") or "?"

    if dig(doc, "metadata.namespace") != GATEWAY_NAMESPACE:
        problems.append(
            f"namespace is {dig(doc, 'metadata.namespace')!r}, but the gateway "
            f"allocator defaults to {GATEWAY_NAMESPACE!r} (DefaultNamespace)"
        )

    ports = dig(doc, "spec.template.spec.ports") or []
    named = [p.get("name") for p in ports]
    if GATEWAY_GAME_PORT_NAME not in named:
        problems.append(
            f"no port named {GATEWAY_GAME_PORT_NAME!r} (found {named}); the gateway "
            f"selects the client-facing port BY NAME and allocation fails with "
            f'\'no "game" port in allocation status\''
        )
    for p in ports:
        if p.get("portPolicy") not in (None, "Dynamic"):
            problems.append(
                f"port {p.get('name')!r} has portPolicy {p.get('portPolicy')!r}; ADR-15 decision 2 "
                f"keeps Dynamic (Static collides under scheduling: Packed)"
            )

    for c in _containers(doc):
        env = c.get("env") or []
        pod_name = next((e for e in env if e.get("name") == "POD_NAME"), None)
        if pod_name is None:
            problems.append(
                f"container {c.get('name')!r} does not set POD_NAME; the server id "
                f"falls back to a random guid, which cannot match the `sid` the "
                f"gateway signs into the join token"
            )
        elif dig(pod_name, "valueFrom.fieldRef.fieldPath") != "metadata.name":
            problems.append(
                f"container {c.get('name')!r} sets POD_NAME from something other than "
                f"fieldRef metadata.name; Agones names the pod after the GameServer, "
                f"and that name is the server id the join token is pinned to"
            )
        for e in env:
            if e.get("name") in SECRET_ENV and "value" in e:
                problems.append(
                    f"container {c.get('name')!r} sets {e['name']} as a LITERAL value; "
                    f"use valueFrom.secretKeyRef (see agones/secret-example.yaml)"
                )
            if e.get("name") == "GAMESERVER_ID":
                problems.append(
                    "GAMESERVER_ID is set, and it WINS over POD_NAME in "
                    "Program.cs — every pod in the fleet would claim the same "
                    "server id while the allocator hands the client the real "
                    "GameServer name, so every join is rejected with 'Token is "
                    "for a different server'. Use POD_NAME via fieldRef only."
                )
            if e.get("name") == "GAMESERVER_PUBLIC_ADDR":
                problems.append(
                    "GAMESERVER_PUBLIC_ADDR is set, but under portPolicy: Dynamic no "
                    "static value can be correct — the port is assigned at scheduling "
                    "time. The server reads its address from the Agones sidecar "
                    "(ADR-15 decision 2 option A)."
                )
        if c.get("imagePullPolicy") != "IfNotPresent" and ":dev" in str(c.get("image", "")):
            problems.append(
                f"container {c.get('name')!r} uses a :dev tag with imagePullPolicy "
                f"{c.get('imagePullPolicy')!r}; local dev tags need IfNotPresent"
            )
        if "rpg-mmo/gameserver:" in str(c.get("image", "")):
            problems.append(
                f"container {c.get('name')!r} references the DELETED Go game server image "
                f"{c.get('image')!r} (removed in 670a803, cannot be rebuilt)"
            )

    replicas = dig(doc, "spec.replicas")
    if isinstance(replicas, int) and replicas > 1:
        for c in _containers(doc):
            for e in c.get("env") or []:
                if e.get("name") == "GAMESERVER_MAP_ID" and "value" in e:
                    problems.append(
                        f"replicas={replicas} with a fixed GAMESERVER_MAP_ID="
                        f"{e['value']!r}: every pod self-registers under the same map id, "
                        f"which breaks ADR-2's one-live-server-per-map_id invariant"
                    )
    del name
    return problems


def check_autoscaler(doc: dict, fleet_names: set[str]) -> list[str]:
    problems: list[str] = []
    policy_type = dig(doc, "spec.policy.type")
    if policy_type != "Buffer":
        problems.append(
            f"policy.type is {policy_type!r}; ADR-14 decision 5 restricts fleet "
            f"autoscaling to Buffer (server count). Any policy keyed on players "
            f"per server is invalid while ADR-7's ceiling is unknown."
        )
    target = dig(doc, "spec.fleetName")
    if target and fleet_names and target not in fleet_names:
        problems.append(
            f"fleetName {target!r} matches no Fleet manifest in this directory "
            f"(have: {sorted(fleet_names) or 'none'})"
        )
    return problems


def check_allocation(doc: dict, fleet_names: set[str]) -> list[str]:
    problems: list[str] = []
    for sel in dig(doc, "spec.selectors") or []:
        target = (sel.get("matchLabels") or {}).get(GATEWAY_FLEET_LABEL)
        if target and fleet_names and target not in fleet_names:
            problems.append(
                f"selector {GATEWAY_FLEET_LABEL}={target!r} matches no Fleet manifest "
                f"in this directory (have: {sorted(fleet_names) or 'none'})"
            )
    return problems


def check_gateway_constants(fleet_names: set[str]) -> list[str]:
    """Warn when the gateway's compiled-in defaults do not name a real fleet.

    Not a failure: main.go lets ALLOCATOR_FLEET_MAP / ALLOCATOR_NAMESPACE
    override them. It is still the single most likely way for ADR-14 stage 5 to
    fail — the allocator POSTs happily against a fleet that does not exist.
    """
    go_file = DEPLOY_DIR.parent / GATEWAY_ALLOCATOR_GO
    if not go_file.exists():
        return []
    text = go_file.read_text(encoding="utf-8")
    warnings: list[str] = []
    import re

    for const, env in (("DefaultFleetMap", "ALLOCATOR_FLEET_MAP"),
                       ("DefaultFleetDungeon", "ALLOCATOR_FLEET_DUNGEON")):
        m = re.search(rf'{const}\s*=\s*"([^"]+)"', text)
        if m and fleet_names and m.group(1) not in fleet_names:
            warnings.append(
                f"gateway {const} = {m.group(1)!r}, which is not a fleet in this "
                f"directory. Set {env} to a real fleet name before ALLOCATOR=agones, "
                f"or allocation POSTs against a fleet that does not exist."
            )
    m = re.search(r'DefaultNamespace\s*=\s*"([^"]+)"', text)
    if m and m.group(1) != GATEWAY_NAMESPACE:
        warnings.append(
            f"gateway DefaultNamespace = {m.group(1)!r}, manifests use "
            f"{GATEWAY_NAMESPACE!r}"
        )
    m = re.search(r'gamePortName\s*=\s*"([^"]+)"', text)
    if m and m.group(1) != GATEWAY_GAME_PORT_NAME:
        warnings.append(
            f"gateway gamePortName = {m.group(1)!r}, manifests name the port "
            f"{GATEWAY_GAME_PORT_NAME!r}"
        )
    return warnings


def check_image_revision(image: str, expected: str) -> int:
    """Assert the local image was built from `expected` (a git revision).

    A mutable tag is a claim about content, and on a shared image store an old
    `:dev` runs silently in place of the code under test. The Dockerfile stamps
    org.opencontainers.image.revision from the GIT_REVISION build arg; this
    compares it. Returns a process exit code.
    """
    import json
    import subprocess

    print(f"\n==> image provenance: {image}")
    try:
        out = subprocess.run(
            ["docker", "image", "inspect", image, "--format", "{{json .Config.Labels}}"],
            capture_output=True, text=True, check=True,
        ).stdout.strip()
    except FileNotFoundError:
        print("[FAIL] docker not on PATH")
        return 1
    except subprocess.CalledProcessError as exc:
        print(f"[FAIL] {image} not in the local image store: {exc.stderr.strip()}")
        return 1

    labels = json.loads(out) or {}
    actual = labels.get("org.opencontainers.image.revision")
    if actual is None:
        print(f"[FAIL] {image} carries no org.opencontainers.image.revision label. "
              f"It predates the label, or was built without "
              f"--build-arg GIT_REVISION. Rebuild before trusting it.")
        return 1
    if actual != expected:
        print(f"[FAIL] {image} was built from {actual}, expected {expected}. "
              f"The tag is stale — rebuild it from the branch under test.")
        return 1
    print(f"[ ok ] {image} built from {actual}")
    return 0


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
    parser.add_argument("--check-image", metavar="IMAGE",
                        help="also assert IMAGE was built from --expect-revision")
    parser.add_argument("--expect-revision", metavar="SHA",
                        help="git revision --check-image must have been built from")
    parser.add_argument("files", nargs="*")
    args = parser.parse_args()

    if bool(args.check_image) != bool(args.expect_revision):
        parser.error("--check-image and --expect-revision must be given together")

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

    # First pass: which Fleets do these manifests actually define? The
    # autoscaler / allocation cross-checks need it, and a dangling fleetName is
    # the failure mode that survives every schema check.
    parsed: list[tuple[pathlib.Path, list]] = []
    fleet_names: set[str] = set()
    for path in files:
        try:
            docs = list(yaml.safe_load_all(path.read_text(encoding="utf-8")))
        except yaml.YAMLError as exc:
            print(f"[FAIL] {path.name}: YAML parse error: {exc}")
            failures += 1
            continue
        parsed.append((path, docs))
        for doc in docs:
            if doc and doc.get("kind") == "Fleet":
                name = dig(doc, "metadata.name")
                if name:
                    fleet_names.add(name)

    for path, docs in parsed:
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
                contract: list[str] = []
                if doc.get("kind") == "Fleet":
                    contract = check_fleet(doc, label)
                elif doc.get("kind") == "FleetAutoscaler":
                    contract = check_autoscaler(doc, fleet_names)
                for problem in contract:
                    print(f"[FAIL] {label}: {problem}")
                failures += len(contract)
            elif doc.get("kind") == "GameServerAllocation":
                # Create-only aggregated-API resource: no CRD, so no schema. The
                # contract check is all there is.
                contract = check_allocation(doc, fleet_names)
                for problem in contract:
                    print(f"[FAIL] {label}: {problem}")
                failures += len(contract)
                if not contract:
                    print(f"[ ok ] {label} (contract check; no CRD schema exists)")
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

    for warning in check_gateway_constants(fleet_names):
        print(f"[warn] {warning}")

    print(f"\n{checked} document(s) validated, {failures} failure(s)")

    rc = 1 if failures else 0
    if args.check_image:
        rc |= check_image_revision(args.check_image, args.expect_revision)
    return rc


if __name__ == "__main__":
    sys.exit(main())
