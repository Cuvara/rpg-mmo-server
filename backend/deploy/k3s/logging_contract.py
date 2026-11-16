#!/usr/bin/env python3
"""Contract of the k8s log collector (backend/deploy/k8s/logging/) and of the
per-game grant that deploy/k8s/games/render.py renders for it.

Standard library + PyYAML only: no jsonschema, no Agones download, no cluster, so
it runs in the CI job `test-deploy-offline` (which installs PyYAML and nothing
else). validate-manifests.py imports it and runs the same checks as part of its
no-argument run.

The contract (each line is one rule below, each rule has a test in
tests/test_logging_contract.py that shows it rejecting a mutation):

  RBAC
    * no ClusterRole and no ClusterRoleBinding anywhere in k8s/logging
    * the only Role is `rpg-log-collector`, in a namespace called rpg-k8s-*, and it
      has EXACTLY two rules: pods get/list/watch and pods/log get (core group, no
      resourceNames, nothing else)
    * every such Role has a RoleBinding of the same name in the same namespace whose
      only subject is ServiceAccount rpg-log-collector of namespace rpg-k8s-logging
      and whose roleRef is that Role
  DaemonSet
    * a DaemonSet `alloy` in rpg-k8s-logging exists
    * no hostPath volume, no hostNetwork / hostPID / hostIPC, no hostPort, no
      privileged container, no allowPrivilegeEscalation
    * every container has readOnlyRootFilesystem and runs as non-root (runAsNonRoot,
      at the container or the pod)
  Service
    * Service `loki` in rpg-k8s-logging exists, has NO selector (its EndpointSlice is
      written by connect-loki.sh) and no externalIPs
    * no Service in the directory is NodePort or LoadBalancer
  Per game (rendered by render.py)
    * exactly one Role `rpg-log-collector` and one RoleBinding with the same grant
      and subject as above, in the game's namespace; no ClusterRole
    * gameserver-config carries log-level and log-format, and every fleet container
      reads GAMESERVER_LOG_LEVEL / GAMESERVER_LOG_FORMAT from them

Usage:
    python logging_contract.py                  # the committed k8s/logging plus a rendered sample game
    python logging_contract.py --dir DIR        # another directory (used to show a mutation failing)
    python logging_contract.py --no-game        # skip the render.py leg

Exit codes: 0 no problem, 1 at least one problem, 2 an input could not be read.
"""

from __future__ import annotations

import argparse
import importlib.util
import pathlib
import sys

try:
    import yaml
except ImportError:  # pragma: no cover
    sys.exit("PyYAML required: pip install pyyaml")

DEPLOY_DIR = pathlib.Path(__file__).resolve().parent.parent
LOGGING_DIR = DEPLOY_DIR / "k8s" / "logging"
GAME_RENDERER = DEPLOY_DIR / "k8s" / "games" / "render.py"

COLLECTOR_NAMESPACE = "rpg-k8s-logging"
COLLECTOR_ACCOUNT = "rpg-log-collector"
COLLECTOR_ROLE = "rpg-log-collector"
COLLECTOR_DAEMONSET = "alloy"
LOKI_SERVICE = "loki"
COLLECTED_PREFIX = "rpg-k8s-"

# The grant, normalised: (apiGroups, resources, verbs), each sorted.
WANT_RULES = [
    (("",), ("pods",), ("get", "list", "watch")),
    (("",), ("pods/log",), ("get",)),
]
RULE_KEYS = {"apiGroups", "resources", "verbs"}
WANT_SUBJECTS = [{"kind": "ServiceAccount", "name": COLLECTOR_ACCOUNT, "namespace": COLLECTOR_NAMESPACE}]

Docs = list  # list of (label, document)


def dig(doc, dotted: str):
    node = doc
    for part in dotted.split("."):
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def _name(doc) -> str:
    return dig(doc, "metadata.name") or "?"


def _ns(doc) -> str:
    return dig(doc, "metadata.namespace") or ""


def _norm_rule(rule: dict):
    return (
        tuple(sorted(rule.get("apiGroups") or [])),
        tuple(sorted(rule.get("resources") or [])),
        tuple(sorted(rule.get("verbs") or [])),
    )


def load_dir(path: pathlib.Path) -> tuple[Docs, list[str]]:
    """Every document of every *.yaml in `path`. (docs, problems)."""
    docs: Docs = []
    problems: list[str] = []
    files = sorted(path.glob("*.yaml"))
    if not files:
        problems.append(f"{path}: no *.yaml files")
    for f in files:
        try:
            for doc in yaml.safe_load_all(f.read_text(encoding="utf-8")):
                if doc:
                    docs.append((f"{f.name}:{doc.get('kind')}/{_name(doc)}", doc))
        except yaml.YAMLError as exc:
            problems.append(f"{f.name}: YAML parse error: {exc}")
    return docs, problems


# --------------------------------------------------------------------------
# RBAC (shared by the static directory and by a rendered game)
# --------------------------------------------------------------------------


def check_role(role: dict, label: str) -> list[str]:
    """The Role is exactly the collector's grant."""
    problems: list[str] = []
    rules = role.get("rules")
    if not isinstance(rules, list):
        return [f"{label}: has no rules list"]
    for rule in rules:
        extra = set(rule) - RULE_KEYS
        if extra:
            problems.append(f"{label}: a rule carries {sorted(extra)}; only apiGroups/resources/verbs are part of the grant")
    got = sorted(_norm_rule(r) for r in rules if isinstance(r, dict))
    want = sorted(WANT_RULES)
    if got != want:
        problems.append(
            f"{label}: rules are {got}, but the collector may have exactly {want} "
            f"(pods get/list/watch, pods/log get). Do not widen it to make something work."
        )
    return problems


def check_binding(binding: dict, role_name: str, label: str) -> list[str]:
    problems: list[str] = []
    if binding.get("subjects") != WANT_SUBJECTS:
        problems.append(
            f"{label}: subjects are {binding.get('subjects')}, but the only subject allowed is {WANT_SUBJECTS}"
        )
    ref = binding.get("roleRef") or {}
    if ref.get("kind") != "Role" or ref.get("name") != role_name:
        problems.append(f"{label}: roleRef is {ref}, expected kind Role name {role_name!r}")
    return problems


def _collector_pairs(docs: Docs, expect_ns: str | None) -> list[str]:
    """Roles and RoleBindings of the collector: names, namespaces, pairing."""
    problems: list[str] = []
    roles = {(_ns(d), _name(d)): (lab, d) for lab, d in docs if d.get("kind") == "Role"}
    bindings = {(_ns(d), _name(d)): (lab, d) for lab, d in docs if d.get("kind") == "RoleBinding"}
    for (ns, name), (lab, d) in roles.items():
        if name != COLLECTOR_ROLE:
            problems.append(f"{lab}: a Role named {name!r}; the only Role the collector may have is {COLLECTOR_ROLE!r}")
            continue
        if expect_ns is not None and ns != expect_ns:
            problems.append(f"{lab}: is in {ns!r}, expected {expect_ns!r}")
        if not ns.startswith(COLLECTED_PREFIX):
            problems.append(f"{lab}: namespace {ns!r} is not an {COLLECTED_PREFIX}* namespace; the collector reads only those")
        problems += check_role(d, lab)
        if (ns, COLLECTOR_ROLE) not in bindings:
            problems.append(f"{lab}: has no RoleBinding {COLLECTOR_ROLE!r} in {ns!r}")
    for (ns, name), (lab, d) in bindings.items():
        if name != COLLECTOR_ROLE:
            problems.append(f"{lab}: a RoleBinding named {name!r}; only {COLLECTOR_ROLE!r} may exist")
            continue
        problems += check_binding(d, COLLECTOR_ROLE, lab)
        if (ns, COLLECTOR_ROLE) not in roles:
            problems.append(f"{lab}: binds a Role {COLLECTOR_ROLE!r} that is not in {ns!r}")
    return problems


# --------------------------------------------------------------------------
# k8s/logging as committed
# --------------------------------------------------------------------------


def check_daemonset(doc: dict, label: str) -> list[str]:
    problems: list[str] = []
    pod = dig(doc, "spec.template.spec") or {}
    for flag in ("hostNetwork", "hostPID", "hostIPC"):
        if pod.get(flag):
            problems.append(f"{label}: {flag} is set; the collector reads logs through the API, not the node")
    for vol in pod.get("volumes") or []:
        if "hostPath" in vol:
            problems.append(f"{label}: volume {vol.get('name')!r} is a hostPath; the collector mounts nothing of the node")
    pod_nonroot = dig(pod, "securityContext.runAsNonRoot")
    containers = (pod.get("containers") or []) + (pod.get("initContainers") or [])
    if not containers:
        problems.append(f"{label}: has no containers")
    for c in containers:
        cname = c.get("name")
        sc = c.get("securityContext") or {}
        for port in c.get("ports") or []:
            if port.get("hostPort"):
                problems.append(f"{label}: container {cname!r} publishes hostPort {port['hostPort']}")
        if sc.get("privileged"):
            problems.append(f"{label}: container {cname!r} is privileged")
        if sc.get("allowPrivilegeEscalation"):
            problems.append(f"{label}: container {cname!r} allows privilege escalation")
        if sc.get("readOnlyRootFilesystem") is not True:
            problems.append(f"{label}: container {cname!r} lacks readOnlyRootFilesystem: true")
        nonroot = sc.get("runAsNonRoot", pod_nonroot)
        if nonroot is not True:
            problems.append(f"{label}: container {cname!r} does not run as non-root (runAsNonRoot is not true on it or on the pod)")
    return problems


def check_service(doc: dict, label: str) -> list[str]:
    problems: list[str] = []
    stype = dig(doc, "spec.type") or "ClusterIP"
    if stype in ("NodePort", "LoadBalancer"):
        problems.append(f"{label}: type {stype} exposes it beyond the cluster")
    if _name(doc) == LOKI_SERVICE:
        if dig(doc, "spec.selector"):
            problems.append(
                f"{label}: has a selector; the Service must be selector-less so that only "
                f"connect-loki.sh's EndpointSlice points at Loki"
            )
        if dig(doc, "spec.externalIPs"):
            problems.append(f"{label}: has externalIPs")
    return problems


def check_logging_manifests(docs: Docs) -> list[str]:
    """All the rules of k8s/logging. Returns the problems (empty = contract holds)."""
    problems: list[str] = []
    for lab, d in docs:
        if d.get("kind") in ("ClusterRole", "ClusterRoleBinding"):
            problems.append(f"{lab}: {d['kind']} is not allowed; the collector gets per-namespace Roles only")
    problems += _collector_pairs(docs, None)
    daemonsets = [(lab, d) for lab, d in docs if d.get("kind") == "DaemonSet"]
    if not any(_name(d) == COLLECTOR_DAEMONSET and _ns(d) == COLLECTOR_NAMESPACE for _, d in daemonsets):
        problems.append(f"no DaemonSet {COLLECTOR_DAEMONSET!r} in {COLLECTOR_NAMESPACE!r}")
    for lab, d in daemonsets:
        problems += check_daemonset(d, lab)
    services = [(lab, d) for lab, d in docs if d.get("kind") == "Service"]
    if not any(_name(d) == LOKI_SERVICE and _ns(d) == COLLECTOR_NAMESPACE for _, d in services):
        problems.append(f"no Service {LOKI_SERVICE!r} in {COLLECTOR_NAMESPACE!r}")
    for lab, d in services:
        problems += check_service(d, lab)
    return problems


# --------------------------------------------------------------------------
# one rendered game
# --------------------------------------------------------------------------

LOG_KNOBS = (("GAMESERVER_LOG_LEVEL", "log-level"), ("GAMESERVER_LOG_FORMAT", "log-format"))


def check_game_logging(manifests: list[dict], game: str, ns: str) -> list[str]:
    """The collector's grant and the log knobs in the manifests render.py emits for one game."""
    problems: list[str] = []
    tag = f"render[{game}]"
    docs: Docs = [(f"{tag}:{d.get('kind')}/{_name(d)}", d) for d in manifests]
    for lab, d in docs:
        if d.get("kind") in ("ClusterRole", "ClusterRoleBinding"):
            problems.append(f"{lab}: {d['kind']} is not allowed")
    mine_roles = [d for _, d in docs if d.get("kind") == "Role" and _name(d) == COLLECTOR_ROLE]
    mine_bindings = [d for _, d in docs if d.get("kind") == "RoleBinding" and _name(d) == COLLECTOR_ROLE]
    if len(mine_roles) != 1 or len(mine_bindings) != 1:
        problems.append(
            f"{tag}: expected exactly one Role and one RoleBinding named {COLLECTOR_ROLE!r}, "
            f"got {len(mine_roles)} and {len(mine_bindings)}"
        )
    # Reuse the static rules on just the collector's pair, in the game's namespace.
    pair = [(lab, d) for lab, d in docs if _name(d) == COLLECTOR_ROLE and d.get("kind") in ("Role", "RoleBinding")]
    problems += _collector_pairs(pair, ns)

    cms = [d for _, d in docs if d.get("kind") == "ConfigMap" and _name(d) == "gameserver-config"]
    data = (cms[0].get("data") or {}) if len(cms) == 1 else {}
    for _, key in LOG_KNOBS:
        if not isinstance(data.get(key), str) or not data.get(key):
            problems.append(f"{tag}: gameserver-config has no non-empty {key!r}")
    for _, d in docs:
        if d.get("kind") != "Fleet":
            continue
        for c in dig(d, "spec.template.spec.template.spec.containers") or []:
            env = c.get("env") or []
            for var, key in LOG_KNOBS:
                hits = [e for e in env if e.get("name") == var]
                ref = dig(hits[0] if hits else {}, "valueFrom.configMapKeyRef") or {}
                if len(hits) != 1 or ref.get("name") != "gameserver-config" or ref.get("key") != key:
                    problems.append(
                        f"{tag}: Fleet {_name(d)!r} container {c.get('name')!r} must read {var} "
                        f"from gameserver-config key {key!r} exactly once"
                    )
    return problems


def load_renderer():
    spec = importlib.util.spec_from_file_location("rpg_mmo_games_render", GAME_RENDERER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)  # type: ignore[union-attr]
    return module


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--dir", type=pathlib.Path, default=LOGGING_DIR)
    parser.add_argument("--no-game", action="store_true", help="skip rendering a sample game")
    args = parser.parse_args(argv)

    docs, problems = load_dir(args.dir)
    if problems:
        for p in problems:
            print(f"[FAIL] {p}")
        return 2
    problems = check_logging_manifests(docs)
    for p in problems:
        print(f"[FAIL] {p}")
    if not problems:
        print(f"[ ok ] {args.dir}: {len(docs)} document(s), logging contract holds")
    failures = len(problems)
    if not args.no_game:
        renderer = load_renderer()
        for game in ("ember", "g" * 24):
            rendered = renderer.render(game, nakama_tls=False)
            ns = renderer.game_namespace(renderer.DEFAULT_STACK_NAMESPACE, game)
            game_problems = check_game_logging(rendered["manifests"], game, ns)
            for p in game_problems:
                print(f"[FAIL] {p}")
            failures += len(game_problems)
            if not game_problems:
                print(f"[ ok ] render[{game}]: collector Role + RoleBinding and the log knobs")
    print(f"\n{failures} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
