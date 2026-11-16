#!/usr/bin/env python3
"""Generate the `alloy-config` ConfigMap of the k8s log collector.

The collector config has two parts:

  * the k8s-specific half, k8s/logging/config-k8s.alloy (discovery, relabel,
    source, writer), and
  * the `level` stage, which is NOT written twice: it lives in the compose
    collector, monitoring/alloy/config.alloy, between the lines
    `// BEGIN shared level pipeline` and `// END shared level pipeline`, and is
    copied here verbatim (markers included) at the placeholder line
    `// @@SHARED_LEVEL_PIPELINE@@`.

The result is written to k8s/logging/40-alloy-config.yaml as ConfigMap
`alloy-config`, key `config.alloy`. That file is committed and must not be edited
by hand.

Usage:
    python generate-config.py            # (re)write 40-alloy-config.yaml
    python generate-config.py --check    # exit 1 when the committed file is stale

Exit codes: 0 ok / in sync, 1 drift (--check), 2 an input is missing or malformed.
Standard library only.
"""

from __future__ import annotations

import argparse
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
DEPLOY = HERE.parent.parent
K8S_PART = HERE / "config-k8s.alloy"
COMPOSE_CONFIG = DEPLOY / "monitoring" / "alloy" / "config.alloy"
OUTPUT = HERE / "40-alloy-config.yaml"

BEGIN = "// BEGIN shared level pipeline"
END = "// END shared level pipeline"
PLACEHOLDER = "// @@SHARED_LEVEL_PIPELINE@@"

HEADER = """\
# GENERATED FILE - do not edit by hand.
#
# Built by k8s/logging/generate-config.py from
#   k8s/logging/config-k8s.alloy            the k8s-specific half
#   monitoring/alloy/config.alloy           the `level` stage, copied verbatim
#                                           between its BEGIN/END shared level
#                                           pipeline markers (single source)
# Regenerate after changing either:
#   python3 backend/deploy/k8s/logging/generate-config.py
# CI and the offline tests run it with --check, which exits 1 on drift.
"""


class InputError(Exception):
    pass


def read(path: pathlib.Path) -> str:
    # Text mode: a checkout with CRLF endings reads the same as one with LF.
    try:
        return path.read_text(encoding="utf-8")
    except OSError as exc:
        raise InputError(f"cannot read {path}: {exc}") from exc


def shared_block(compose_text: str) -> str:
    """The marker lines and everything between them, without a trailing newline."""
    lines = compose_text.split("\n")
    begins = [i for i, line in enumerate(lines) if line.rstrip() == BEGIN]
    ends = [i for i, line in enumerate(lines) if line.rstrip() == END]
    if len(begins) != 1 or len(ends) != 1 or begins[0] >= ends[0]:
        raise InputError(
            f"{COMPOSE_CONFIG} must hold exactly one '{BEGIN}' line followed by "
            f"one '{END}' line"
        )
    return "\n".join(lines[begins[0]:ends[0] + 1])


def build_config() -> str:
    k8s_text = read(K8S_PART)
    lines = k8s_text.split("\n")
    where = [i for i, line in enumerate(lines) if line.strip() == PLACEHOLDER]
    if len(where) != 1:
        raise InputError(f"{K8S_PART} must hold exactly one '{PLACEHOLDER}' line")
    block = shared_block(read(COMPOSE_CONFIG))
    lines[where[0]] = block
    config = "\n".join(lines)
    if not config.endswith("\n"):
        config += "\n"
    return config


def render_manifest(config: str) -> str:
    out = [HEADER.rstrip("\n"), "apiVersion: v1", "kind: ConfigMap", "metadata:",
           "  name: alloy-config", "  namespace: rpg-k8s-logging", "  labels:",
           "    app.kubernetes.io/part-of: rpg-mmo",
           "    app.kubernetes.io/name: alloy",
           "data:", "  config.alloy: |"]
    for line in config.rstrip("\n").split("\n"):
        out.append(("    " + line) if line else "")
    return "\n".join(out) + "\n"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true",
                        help="do not write; exit 1 when 40-alloy-config.yaml is stale")
    args = parser.parse_args(argv)
    try:
        manifest = render_manifest(build_config())
    except InputError as exc:
        print(f"generate-config: {exc}", file=sys.stderr)
        return 2
    if args.check:
        current = OUTPUT.read_text(encoding="utf-8") if OUTPUT.exists() else None
        if current != manifest:
            print(f"generate-config: {OUTPUT.name} is stale or missing; run "
                  f"python3 backend/deploy/k8s/logging/generate-config.py", file=sys.stderr)
            return 1
        print(f"generate-config: {OUTPUT.name} is in sync")
        return 0
    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(manifest)
    print(f"generate-config: wrote {OUTPUT.name}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
