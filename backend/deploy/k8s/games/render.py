#!/usr/bin/env python3
"""Render the per-game Kubernetes manifests for a NON-default game (ADR-33 leg 5).

The default game, cuvara, keeps k8s/app/ exactly as it is. Every other game
gets its own namespace, `<stack namespace>-<game>` (ADR-33 D7), holding:

  * a Namespace, a ResourceQuota and a LimitRange (D7: one game's fleet must not
    starve another game's allocations);
  * the `agones-sdk` ServiceAccount + RoleBinding every GameServer namespace
    needs (from k8s/app/05-agones-sdk-rbac.yaml);
  * its own `gameserver-config` ConfigMap (from k8s/app/20-configmaps.yaml) with
    `game-id: <game>`, which is how GAMESERVER_GAME_ID reaches the pods;
  * fleets `<game>-map-servers-dotnet-<env>` and
    `<game>-dungeon-servers-dotnet-<env>` and the dungeon FleetAutoscaler (from
    k8s/app/50-fleet-map.yaml, 60-fleet-dungeon.yaml,
    70-fleetautoscaler-dungeon.yaml). The `<game>-` prefix is ADR-33 D2 S1:
    Agones names every GameServer, and therefore every pod and server id, after
    its fleet, so the prefix is what makes a non-default game's server ids
    carry `<game>-`;
  * the gateway's allocate grant in THIS namespace: a Role with `create` on
    allocation.agones.dev/gameserverallocations only (the rule from
    k8s/app/10-rbac.yaml) bound to ServiceAccount `rpg-gateway` of the STACK
    namespace.

and, as separate outputs that are never part of the manifests stream:

  * a Secret TEMPLATE `rpg-app-secrets` for the game namespace (placeholders
    only, after k8s/app/30-secret-template.yaml, WITHOUT jwt-secret: the client
    auth secret never reaches a game-server pod, ADR-8);
  * the gateway env snippet (ALLOCATOR_FLEET_MAP_<GAME>,
    ALLOCATOR_FLEET_DUNGEON_<GAME>, ALLOCATOR_NAMESPACE_<GAME>,
    JOIN_TOKEN_SECRET_<GAME>) for k8s/app/40-gateway.yaml. GAMES is NOT an
    entry in it: 40-gateway.yaml already reads GAMES from ConfigMap
    gateway-config key `games`, so the snippet's header carries the
    `kubectl patch configmap` that sets that key instead (a second GAMES env
    entry would shadow or duplicate the ConfigMap one).

The k8s/app files are the source of truth: this script loads them and renames,
re-namespaces and re-images what it finds, so a game cannot drift from cuvara's
fleet shape. It never talks to a cluster and never reads or writes a secret
value.

Usage:
    python render.py --game ember                      # manifests on stdout
    python render.py --game ember --print secret-template
    python render.py --game ember --print gateway-env
    python render.py --game ember --out /tmp/ember     # all three as files
        [--env k8s] [--stack-namespace rpg-k8s-realtime]
        [--image rpg-mmo/gameserver-dotnet:ember-develop]

Exit codes: 0 rendered, 2 refused (invalid game id, env, namespace or a name
over its length budget).
"""

from __future__ import annotations

import argparse
import copy
import pathlib
import re
import sys

try:
    import yaml
except ImportError:  # pragma: no cover
    sys.exit("PyYAML required: pip install pyyaml")

GAMES_DIR = pathlib.Path(__file__).resolve().parent
APP_DIR = GAMES_DIR.parent / "app"

DEFAULT_GAME_ID = "cuvara"
# Same rule as shared/gameid.ValidGameID (Go) and db/provision-gamestate.sh.
GAME_ID_RE = re.compile(r"^[a-z][a-z0-9]{1,23}$")
RESERVED_GAME_IDS = ("gs", "map", "dungeon", "other")
DNS_LABEL_RE = re.compile(r"^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")

DEFAULT_ENV = "k8s"
DEFAULT_STACK_NAMESPACE = "rpg-k8s-realtime"
IMAGE_REPO = "rpg-mmo/gameserver-dotnet"
SOURCE_FLEET_SUFFIX = "-k8s"

# RFC 1123 label limit: namespace, pod hostname, label values.
NAME_LIMIT = 63
# Agones 1.59.0 (pinned in k3s/setup-dev.sh and validate-manifests.py):
#   pkg/apis/agones/v1/fleet.go:128          GameServerSet GenerateName = fleet + "-"
#   pkg/apis/agones/v1/gameserverset.go:156  GameServer GenerateName = gss + "-"
#   pkg/apis/agones/v1/gameserver.go:871     pod name = GameServer name
# kube-apiserver's names.SimpleNameGenerator appends 5 random characters and
# truncates the base to 58. So GameServer = pod = fleet + "-" + 5 + "-" + 5,
# i.e. fleet + 12, and a fleet of at most 51 characters keeps the pod at 63 or
# less with no truncation (truncation would cut into the `<game>-` prefix only
# for absurd lengths, but it would also make two fleets' pods indistinguishable
# by name, so it is refused rather than tolerated).
AGONES_SUFFIX_BUDGET = 12
FLEET_NAME_LIMIT = NAME_LIMIT - AGONES_SUFFIX_BUDGET  # 51

# The Agones 1.59.0 install.yaml injects the SDK sidecar with SIDECAR_CPU_REQUEST
# 30m and NO memory request and no limits. A ResourceQuota on requests.memory
# refuses any pod with a container that declares none, so the LimitRange below
# fills the sidecar's missing requests (and only those: it sets no default
# limit, so it cannot OOM-kill a sidecar nobody measured).
SIDECAR_REQUEST = {"cpu": "30m", "memory": "32Mi"}
# Quota = 2 x (map replicas + dungeon autoscaler maxReplicas) pods' requests.
# The factor is headroom for a fleet rolling update, where old Allocated
# GameServers and new Ready ones coexist. ARITHMETIC ON THE MANIFESTS, NOT A
# CAPACITY MEASUREMENT (ADR-7: the per-server ceiling is unknown).
QUOTA_HEADROOM = 2

# Gateway side of the contract (gateway/cmd/gateway/main.go, registry.FleetEnvName).
GATEWAY_SERVICE_ACCOUNT = "rpg-gateway"
GATEWAY_SECRET = "rpg-app-secrets"
ALLOCATOR_ROLE = "rpg-gateway-allocator"

PART_OF = {"app.kubernetes.io/part-of": "rpg-mmo"}


class RenderError(ValueError):
    """A refused input: the message says which rule and why."""


# --------------------------------------------------------------------------
# Validation
# --------------------------------------------------------------------------


def validate_game_id(game: str) -> None:
    if game == DEFAULT_GAME_ID:
        raise RenderError(
            f"game id {game!r} is the default game; it keeps k8s/app/ (namespace "
            f"{DEFAULT_STACK_NAMESPACE}, unprefixed fleets) and is never rendered here"
        )
    if game in RESERVED_GAME_IDS:
        raise RenderError(
            f"game id {game!r} is reserved ({', '.join(RESERVED_GAME_IDS)}): it would "
            f"collide with default-game server ids, fleet-derived pod names or the "
            f"gateway's metrics bucket for unknown games (ADR-33)"
        )
    if not GAME_ID_RE.fullmatch(game or ""):
        raise RenderError(
            f"invalid game id {game!r}: expected ^[a-z][a-z0-9]{{1,23}}$ "
            f"(2-24 chars, lowercase letter then lowercase letters/digits)"
        )


def validate_label(kind: str, value: str) -> None:
    if not DNS_LABEL_RE.fullmatch(value or "") or len(value) > NAME_LIMIT:
        raise RenderError(
            f"invalid {kind} {value!r}: must be an RFC 1123 label "
            f"(lowercase alphanumerics and '-', at most {NAME_LIMIT} chars)"
        )


def check_name(kind: str, name: str, limit: int = NAME_LIMIT) -> None:
    if len(name) > limit:
        extra = ""
        if limit == FLEET_NAME_LIMIT:
            extra = (
                f"; Agones 1.59.0 names GameServers and pods fleet + "
                f"{AGONES_SUFFIX_BUDGET} chars and they must stay within {NAME_LIMIT}. "
                f"Use a shorter game id or --env"
            )
        raise RenderError(
            f"{kind} name {name!r} is {len(name)} chars, over the {limit}-char budget{extra}"
        )


def game_namespace(stack_namespace: str, game: str) -> str:
    return f"{stack_namespace}-{game}"


def fleet_name(source_name: str, game: str, env: str) -> str:
    if not source_name.endswith(SOURCE_FLEET_SUFFIX):
        raise RenderError(
            f"source fleet {source_name!r} does not end with {SOURCE_FLEET_SUFFIX!r}; "
            f"k8s/app changed shape, update render.py"
        )
    return f"{game}-{source_name[: -len(SOURCE_FLEET_SUFFIX)]}-{env}"


# --------------------------------------------------------------------------
# Source loading
# --------------------------------------------------------------------------


def _load(name: str) -> list[dict]:
    return [d for d in yaml.safe_load_all((APP_DIR / name).read_text(encoding="utf-8")) if d]


def _one(docs: list[dict], kind: str, name: str | None = None) -> dict:
    found = [
        d for d in docs
        if d.get("kind") == kind and (name is None or d.get("metadata", {}).get("name") == name)
    ]
    if len(found) != 1:
        raise RenderError(f"expected exactly one {kind} {name or ''} in k8s/app, found {len(found)}")
    return copy.deepcopy(found[0])


def _containers(fleet: dict) -> list[dict]:
    return fleet["spec"]["template"]["spec"]["template"]["spec"]["containers"]


def _quantity_milli(q: str) -> int:
    q = str(q)
    return int(q[:-1]) if q.endswith("m") else int(float(q) * 1000)


def _quantity_mi(q: str) -> int:
    q = str(q)
    if q.endswith("Mi"):
        return int(q[:-2])
    if q.endswith("Gi"):
        return int(q[:-2]) * 1024
    raise RenderError(f"unsupported memory quantity {q!r} in a source fleet")


# --------------------------------------------------------------------------
# Render
# --------------------------------------------------------------------------


def render(
    game: str,
    env: str = DEFAULT_ENV,
    stack_namespace: str = DEFAULT_STACK_NAMESPACE,
    image: str | None = None,
) -> dict[str, list[dict]]:
    """Return {"manifests": [...], "secret-template": [...], "gateway-env": [...],
    "gateway-games": "cuvara,<game>"}.

    Raises RenderError on any refused input. Pure: reads k8s/app, writes nothing.
    """
    validate_game_id(game)
    validate_label("env", env)
    validate_label("stack namespace", stack_namespace)
    if stack_namespace.endswith(f"-{game}"):
        raise RenderError(
            f"stack namespace {stack_namespace!r} already ends with '-{game}'; pass the "
            f"DEFAULT game's namespace (e.g. {DEFAULT_STACK_NAMESPACE}), not the game's"
        )
    ns = game_namespace(stack_namespace, game)
    check_name("namespace", ns)
    image = image or f"{IMAGE_REPO}:{game}-develop"
    if not image or any(c.isspace() for c in image):
        raise RenderError(f"invalid image {image!r}")

    game_labels = {**PART_OF, "rpg-mmo/game": game}

    def meta(name: str, extra: dict | None = None) -> dict:
        check_name("object", name)
        return {"name": name, "namespace": ns, "labels": {**game_labels, **(extra or {})}}

    # --- sources ---------------------------------------------------------
    sdk_docs = _load("05-agones-sdk-rbac.yaml")
    rbac_docs = _load("10-rbac.yaml")
    cm_docs = _load("20-configmaps.yaml")
    secret_src = _one(_load("30-secret-template.yaml"), "Secret")
    map_src = _one(_load("50-fleet-map.yaml"), "Fleet")
    dungeon_src = _one(_load("60-fleet-dungeon.yaml"), "Fleet")
    fas_src = _one(_load("70-fleetautoscaler-dungeon.yaml"), "FleetAutoscaler")
    role_src = _one(rbac_docs, "Role", ALLOCATOR_ROLE)

    # --- fleets ------------------------------------------------------------
    fleets: list[dict] = []
    renamed: dict[str, str] = {}
    for src in (map_src, dungeon_src):
        new_name = fleet_name(src["metadata"]["name"], game, env)
        check_name("fleet", new_name, FLEET_NAME_LIMIT)
        renamed[src["metadata"]["name"]] = new_name
        fleet = copy.deepcopy(src)
        fleet["metadata"]["name"] = new_name
        fleet["metadata"]["namespace"] = ns
        fleet["metadata"].setdefault("labels", {})["rpg-mmo/game"] = game
        fleet["spec"]["template"].setdefault("metadata", {}).setdefault("labels", {})["rpg-mmo/game"] = game
        for c in _containers(fleet):
            c["image"] = image
            env_list = c.get("env") or []
            if any(e.get("name") == "JWT_SECRET" for e in env_list):
                raise RenderError(f"source fleet {src['metadata']['name']} sets JWT_SECRET; refusing to copy it")
            gid = [e for e in env_list if e.get("name") == "GAMESERVER_GAME_ID"]
            if len(gid) != 1:
                raise RenderError(
                    f"source fleet {src['metadata']['name']} must set GAMESERVER_GAME_ID exactly once"
                )
            # REQUIRED here (optional in k8s/app, where absent means cuvara): a
            # non-default game's pod without its game id would run as cuvara.
            gid[0]["valueFrom"] = {
                "configMapKeyRef": {"name": "gameserver-config", "key": "game-id"}
            }
        fleets.append(fleet)

    fas = copy.deepcopy(fas_src)
    src_target = fas["spec"]["fleetName"]
    if src_target not in renamed:
        raise RenderError(f"source autoscaler targets {src_target!r}, which is not a source fleet")
    fas["spec"]["fleetName"] = renamed[src_target]
    fas_name = f"{renamed[src_target]}-buffer"
    fas["metadata"] = {**meta(fas_name), "labels": {**fas_src["metadata"].get("labels", {}), "rpg-mmo/game": game}}

    # --- quota --------------------------------------------------------------
    def pod_requests(fleet: dict) -> tuple[int, int]:
        cpu = _quantity_milli(SIDECAR_REQUEST["cpu"])
        mem = _quantity_mi(SIDECAR_REQUEST["memory"])
        for c in _containers(fleet):
            req = (c.get("resources") or {}).get("requests") or {}
            cpu += _quantity_milli(req.get("cpu", SIDECAR_REQUEST["cpu"]))
            mem += _quantity_mi(req.get("memory", SIDECAR_REQUEST["memory"]))
        return cpu, mem

    map_fleet, dungeon_fleet = fleets
    map_pods = int(map_fleet["spec"].get("replicas") or 0)
    dungeon_pods = int(fas["spec"]["policy"]["buffer"]["maxReplicas"])
    map_cpu, map_mem = pod_requests(map_fleet)
    dun_cpu, dun_mem = pod_requests(dungeon_fleet)
    pods = QUOTA_HEADROOM * (map_pods + dungeon_pods)
    quota_cpu = QUOTA_HEADROOM * (map_pods * map_cpu + dungeon_pods * dun_cpu)
    quota_mem = QUOTA_HEADROOM * (map_pods * map_mem + dungeon_pods * dun_mem)

    namespace_doc = {
        "apiVersion": "v1",
        "kind": "Namespace",
        "metadata": {"name": ns, "labels": {**game_labels, "rpg-mmo/tier": "app"}},
    }
    quota = {
        "apiVersion": "v1",
        "kind": "ResourceQuota",
        "metadata": meta("game-quota"),
        "spec": {
            "hard": {
                "pods": str(pods),
                "count/gameservers.agones.dev": str(pods),
                "requests.cpu": f"{quota_cpu}m",
                "requests.memory": f"{quota_mem}Mi",
            }
        },
    }
    limit_range = {
        "apiVersion": "v1",
        "kind": "LimitRange",
        "metadata": meta("sidecar-default-requests"),
        "spec": {"limits": [{"type": "Container", "defaultRequest": dict(SIDECAR_REQUEST)}]},
    }

    # --- agones-sdk RBAC ----------------------------------------------------
    sdk_sa = _one(sdk_docs, "ServiceAccount", "agones-sdk")
    sdk_sa["metadata"] = meta("agones-sdk")
    sdk_rb = _one(sdk_docs, "RoleBinding", "agones-sdk-access")
    sdk_rb["metadata"] = meta("agones-sdk-access")
    for s in sdk_rb["subjects"]:
        s["namespace"] = ns

    # --- gameserver-config --------------------------------------------------
    gs_cm = _one(cm_docs, "ConfigMap", "gameserver-config")
    gs_cm["metadata"] = meta("gameserver-config", {"app.kubernetes.io/name": "gameserver"})
    gs_cm["data"] = {"game-id": game, **{k: v for k, v in gs_cm["data"].items() if k != "game-id"}}

    # --- gateway allocate grant --------------------------------------------
    role = {
        "apiVersion": role_src["apiVersion"],
        "kind": "Role",
        "metadata": meta(ALLOCATOR_ROLE),
        "rules": copy.deepcopy(role_src["rules"]),
    }
    role_binding = {
        "apiVersion": "rbac.authorization.k8s.io/v1",
        "kind": "RoleBinding",
        "metadata": meta(ALLOCATOR_ROLE),
        "subjects": [
            {"kind": "ServiceAccount", "name": GATEWAY_SERVICE_ACCOUNT, "namespace": stack_namespace}
        ],
        "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "Role", "name": ALLOCATOR_ROLE},
    }

    manifests = [
        namespace_doc, quota, limit_range, sdk_sa, sdk_rb, gs_cm, role, role_binding,
        *fleets, fas,
    ]

    # --- Secret template (placeholders only) --------------------------------
    data = {k: v for k, v in (secret_src.get("stringData") or {}).items() if k != "jwt-secret"}
    data["join-token-secret"] = (
        f"REPLACE_ME_join_{game}_SAME_AS_{stack_namespace}/{GATEWAY_SECRET}/join-token-secret-{game}"
    )
    data["game-db-url"] = (
        f"REPLACE_ME_postgres://gamestate_{game}:<password>@postgres-game.rpg-k8s-data"
        f".svc.cluster.local:5432/gamestate_{game}"
    )
    secret = {
        "apiVersion": "v1",
        "kind": "Secret",
        "metadata": meta(secret_src["metadata"]["name"]),
        "type": "Opaque",
        "stringData": data,
    }

    # --- gateway env snippet ------------------------------------------------
    # GAMES is deliberately absent: k8s/app/40-gateway.yaml reads it from
    # ConfigMap gateway-config key `games` (optional) and says not to add a
    # second GAMES variable. The value that key needs is returned separately
    # as "gateway-games" and printed as a patch command in the snippet header.
    up = game.upper()
    gateway_env = [
        {"name": f"ALLOCATOR_FLEET_MAP_{up}", "value": map_fleet["metadata"]["name"]},
        {"name": f"ALLOCATOR_FLEET_DUNGEON_{up}", "value": dungeon_fleet["metadata"]["name"]},
        {"name": f"ALLOCATOR_NAMESPACE_{up}", "value": ns},
        {
            "name": f"JOIN_TOKEN_SECRET_{up}",
            "valueFrom": {"secretKeyRef": {"name": GATEWAY_SECRET, "key": f"join-token-secret-{game}"}},
        },
    ]

    return {
        "manifests": manifests,
        "secret-template": [secret],
        "gateway-env": gateway_env,
        "gateway-games": f"{DEFAULT_GAME_ID},{game}",
    }


# --------------------------------------------------------------------------
# Output
# --------------------------------------------------------------------------


def _header(game: str, part: str, args: argparse.Namespace) -> str:
    cmd = (
        f"python deploy/k8s/games/render.py --game {game} --env {args.env} "
        f"--stack-namespace {args.stack_namespace}"
        + (f" --image {args.image}" if args.image else "")
    )
    lines = [
        f"# GENERATED by {cmd}",
        "# Source of truth: deploy/k8s/app/. Do not edit; re-render. ADR-33 leg 5.",
    ]
    if part == "manifests":
        lines += [
            "# Before applying: the game's Secret (render --print secret-template, filled",
            "# out of git), registry-creds in this namespace if the image is pulled from",
            "# the registry, and the game's database (db/provision-gamestate.sh, human-run).",
            "# Apply the FleetAutoscaler only after the image is pinned (see k8s/app/70-*).",
        ]
    elif part == "secret-template":
        lines += [
            "# TEMPLATE. Placeholders only - never commit a filled copy. No jwt-secret:",
            "# the client auth secret is never mounted on a game-server pod (ADR-8).",
            "# join-token-secret must equal the gateway's join-token-secret-<game> key in",
            "# the stack namespace's rpg-app-secrets, and differ from every other game's.",
        ]
    else:
        lines += [
            "# Gateway env for this game: merge into the gateway container's env in",
            "# k8s/app/40-gateway.yaml. GAMES is NOT an env entry here: the gateway",
            "# reads it from ConfigMap gateway-config key `games`. Set that key (it lists",
            "# EVERY served game, comma-separated; if it already lists other games,",
            "# append this one rather than replacing the list):",
            f"#   kubectl -n {args.stack_namespace} patch configmap gateway-config \\",
            f"#     --type=merge -p '{{\"data\":{{\"games\":\"{DEFAULT_GAME_ID},{game}\"}}}}'",
            "# Nakama's GAMES (k8s/data/nakama.yaml, nakama-config key `games`) must agree.",
        ]
    return "\n".join(lines) + "\n"


def dump(part: str, docs: list) -> str:
    if part == "gateway-env":
        return yaml.safe_dump(docs, sort_keys=False, default_flow_style=False)
    return yaml.safe_dump_all(docs, sort_keys=False, default_flow_style=False)


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--game", required=True, help="non-default game id, ^[a-z][a-z0-9]{1,23}$")
    p.add_argument("--env", default=DEFAULT_ENV, help=f"fleet name suffix (default {DEFAULT_ENV})")
    p.add_argument("--stack-namespace", default=DEFAULT_STACK_NAMESPACE,
                   help=f"the DEFAULT game's namespace (default {DEFAULT_STACK_NAMESPACE})")
    p.add_argument("--image", default=None, help=f"default {IMAGE_REPO}:<game>-develop")
    p.add_argument("--print", dest="part", default="manifests",
                   choices=("manifests", "secret-template", "gateway-env"))
    p.add_argument("--out", type=pathlib.Path, default=None,
                   help="write <game>.yaml, <game>.secret-template.yaml and "
                        "<game>.gateway-env.yaml here instead of printing (keep it out of git)")
    args = p.parse_args(argv)

    try:
        out = render(args.game, args.env, args.stack_namespace, args.image)
    except RenderError as exc:
        print(f"render: refused: {exc}", file=sys.stderr)
        return 2

    if args.out is None:
        sys.stdout.write(_header(args.game, args.part, args) + dump(args.part, out[args.part]))
        return 0

    args.out.mkdir(parents=True, exist_ok=True)
    names = {
        "manifests": f"{args.game}.yaml",
        "secret-template": f"{args.game}.secret-template.yaml",
        "gateway-env": f"{args.game}.gateway-env.yaml",
    }
    for part, fname in names.items():
        path = args.out / fname
        path.write_text(_header(args.game, part, args) + dump(part, out[part]), encoding="utf-8")
        print(f"wrote {path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
