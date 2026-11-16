#!/usr/bin/env python3
"""Fixture writer for hub_rbac_test.sh: the live objects `kubectl get -o json` would
return for the Studio hub, as files in a directory. Standard library only.

    python3 hub_rbac_fixture.py DIR [--games ember,frost] [--node] [--mutate NAME ...]

The exact state is built FROM k3s/hub_contract.py (its tables are the one copy of
the allow-list), so "exact" can never drift from the contract; every mutation below
is a deliberate, named departure from it. A file that is absent is an object the
fake kubectl answers NotFound for.

Files: sa.json, role.<ns>.json, rb.<ns>.json, cr.json, crb.json, games.txt (or games.notfound / games.error)
"""

import argparse
import copy
import importlib.util
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("hub_contract", HERE.parent.parent.parent / "k3s" / "hub_contract.py")
hc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hc)

RBAC = "rbac.authorization.k8s.io/v1"
SUBJECT = {"kind": "ServiceAccount", "name": "studio-hub", "namespace": "rpg-k8s-hub"}


def rules(table):
    return [{"apiGroups": [g], "resources": list(r), "verbs": list(v)} for g, r, v in table]


def role(ns, table):
    return {"apiVersion": RBAC, "kind": "Role", "metadata": {"name": "studio-hub", "namespace": ns, "uid": "u-" + ns},
            "rules": rules(table)}


def binding(name, ns, role_kind="Role", role_name=None, subjects=None, kind="RoleBinding"):
    meta = {"name": name}
    if ns:
        meta["namespace"] = ns
    return {"apiVersion": RBAC, "kind": kind, "metadata": meta,
            "subjects": copy.deepcopy(subjects if subjects is not None else [SUBJECT]),
            "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": role_kind, "name": role_name or name}}


def listing(items):
    return {"apiVersion": "v1", "kind": "List", "items": items}


# a RoleBinding that has nothing to do with the hub, present in every namespace
def noise(ns):
    return binding("agones-sdk", ns, role_name="agones-sdk",
                   subjects=[{"kind": "ServiceAccount", "name": "agones-sdk", "namespace": ns}])


MUTATIONS = {
    "no-sa", "extra-verb", "extra-resource", "missing-rule", "data-gets-agones", "wildcard", "resource-names",
    "game-role-missing", "game-extra-verb", "game-binding-missing", "binding-wrong-subject",
    "binding-cluster-admin", "foreign-rb", "foreign-crb", "node-extra-verb", "node-missing-rule",
    "node-role-no-binding", "node-binding-no-role", "node-binding-cluster-admin", "no-gateway-games",
    "gateway-config-notfound", "gateway-config-error",
    "foreign-rb-user", "foreign-rb-group-ns", "foreign-rb-group-all",
    "foreign-crb-user", "foreign-crb-group-ns", "foreign-crb-group-all",
}

# The other spellings of the hub identity (User / Group subjects), see hub_contract.names_the_hub.
ALIAS_SUBJECTS = {
    "user": {"kind": "User", "name": "system:serviceaccount:rpg-k8s-hub:studio-hub",
             "apiGroup": "rbac.authorization.k8s.io"},
    "group-ns": {"kind": "Group", "name": "system:serviceaccounts:rpg-k8s-hub",
                 "apiGroup": "rbac.authorization.k8s.io"},
    "group-all": {"kind": "Group", "name": "system:serviceaccounts", "apiGroup": "rbac.authorization.k8s.io"},
}


def main(argv):
    ap = argparse.ArgumentParser()
    ap.add_argument("dir", type=pathlib.Path)
    ap.add_argument("--games", default="")
    ap.add_argument("--node", action="store_true")
    ap.add_argument("--mutate", action="append", default=[])
    args = ap.parse_args(argv)
    for m in args.mutate:
        if m not in MUTATIONS:
            sys.exit(f"unknown mutation {m}")
    mut = set(args.mutate)
    out = args.dir
    out.mkdir(parents=True, exist_ok=True)
    for f in out.glob("*"):
        f.unlink()

    games = [g for g in args.games.split(",") if g]
    game_ns = [f"rpg-k8s-realtime-{g}" for g in games if g != "cuvara"]
    # games.txt: the value of gateway-config's `games` (an empty file is an existing ConfigMap with
    # an empty value). games.notfound / games.error make the fake answer NotFound / Forbidden instead.
    if "gateway-config-notfound" in mut:
        (out / "games.notfound").write_text("", encoding="utf-8")
    elif "gateway-config-error" in mut:
        (out / "games.error").write_text("", encoding="utf-8")
    else:
        (out / "games.txt").write_text("" if "no-gateway-games" in mut else args.games, encoding="utf-8")

    def put(name, doc):
        (out / name).write_text(json.dumps(doc), encoding="utf-8")

    if "no-sa" not in mut:
        put("sa.json", {"apiVersion": "v1", "kind": "ServiceAccount",
                        "metadata": {"name": "studio-hub", "namespace": "rpg-k8s-hub"},
                        "automountServiceAccountToken": False})

    roles = {"rpg-k8s-realtime": role("rpg-k8s-realtime", hc.REALTIME_RULES),
             "rpg-k8s-data": role("rpg-k8s-data", hc.DATA_RULES)}
    for ns in game_ns:
        roles[ns] = role(ns, hc.REALTIME_RULES)
    bindings = {ns: [noise(ns), binding("studio-hub", ns)] for ns in roles}

    if "extra-verb" in mut:
        roles["rpg-k8s-realtime"]["rules"][0]["verbs"].append("create")        # pods create
    if "extra-resource" in mut:
        roles["rpg-k8s-realtime"]["rules"].append({"apiGroups": [""], "resources": ["secrets"], "verbs": ["get"]})
    if "missing-rule" in mut:
        roles["rpg-k8s-realtime"]["rules"] = [r for r in roles["rpg-k8s-realtime"]["rules"]
                                              if r["resources"] != ["pods/log"]]
    if "data-gets-agones" in mut:
        roles["rpg-k8s-data"]["rules"] += rules(hc.REALTIME_RULES[-5:])          # the Agones rows
    if "wildcard" in mut:
        roles["rpg-k8s-data"]["rules"].append({"apiGroups": [""], "resources": ["*"], "verbs": ["get"]})
    if "resource-names" in mut:
        roles["rpg-k8s-data"]["rules"][0]["resourceNames"] = ["redis-0"]
    if game_ns and "game-role-missing" in mut:
        del roles[game_ns[0]]
    if game_ns and "game-extra-verb" in mut:
        roles[game_ns[0]]["rules"][0]["verbs"].append("patch")                   # pods patch
    if game_ns and "game-binding-missing" in mut:
        bindings[game_ns[0]] = [noise(game_ns[0])]
    if "binding-wrong-subject" in mut:
        bindings["rpg-k8s-data"][1] = binding("studio-hub", "rpg-k8s-data", subjects=[
            {"kind": "ServiceAccount", "name": "studio-hub", "namespace": "rpg-k8s-realtime"}])
    if "binding-cluster-admin" in mut:
        bindings["rpg-k8s-data"][1] = binding("studio-hub", "rpg-k8s-data", "ClusterRole", "cluster-admin")
    if "foreign-rb" in mut:
        bindings["rpg-k8s-realtime"].append(binding("hub-edit", "rpg-k8s-realtime", "ClusterRole", "edit"))
    for key, subject in ALIAS_SUBJECTS.items():
        if f"foreign-rb-{key}" in mut:
            bindings["rpg-k8s-realtime"].append(binding(
                "sneaky", "rpg-k8s-realtime", "ClusterRole", "cluster-admin", subjects=[subject]))

    for ns, doc in roles.items():
        put(f"role.{ns}.json", doc)
    for ns, items in bindings.items():
        put(f"rb.{ns}.json", listing(items))

    node_role = want_node = args.node or {"node-extra-verb", "node-missing-rule", "node-role-no-binding",
                                          "node-binding-cluster-admin"} & mut
    cluster_items = [binding("system:node", None, "ClusterRole", "system:node", kind="ClusterRoleBinding", subjects=[
        {"kind": "Group", "name": "system:nodes", "apiGroup": "rbac.authorization.k8s.io"}])]
    if node_role:
        table = list(hc.NODE_RULES)
        cr = {"apiVersion": RBAC, "kind": "ClusterRole", "metadata": {"name": "studio-hub-nodes"}, "rules": rules(table)}
        if "node-extra-verb" in mut:
            cr["rules"][0]["verbs"].append("delete")                             # nodes delete
        if "node-missing-rule" in mut:
            cr["rules"].pop()
        put("cr.json", cr)
        if "node-role-no-binding" not in mut:
            cluster_items.append(binding("studio-hub-nodes", None, "ClusterRole", kind="ClusterRoleBinding"))
        if "node-binding-cluster-admin" in mut:
            cluster_items[-1] = binding("studio-hub-nodes", None, "ClusterRole", "cluster-admin",
                                        kind="ClusterRoleBinding")
    if "node-binding-no-role" in mut:
        cluster_items.append(binding("studio-hub-nodes", None, "ClusterRole", kind="ClusterRoleBinding"))
    if "foreign-crb" in mut:
        cluster_items.append(binding("hub-admin", None, "ClusterRole", "cluster-admin", kind="ClusterRoleBinding"))
    for key, subject in ALIAS_SUBJECTS.items():
        if f"foreign-crb-{key}" in mut:
            cluster_items.append(binding("sneaky", None, "ClusterRole", "cluster-admin",
                                         kind="ClusterRoleBinding", subjects=[subject]))
    put("crb.json", listing(cluster_items))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
