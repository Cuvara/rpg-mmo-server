#!/usr/bin/env python3
"""Fixture writer for hub_rbac_test.sh: the live objects `kubectl get -o json` would
return for the Studio hub, as files in a directory. Standard library only.

    python3 hub_rbac_fixture.py DIR [--games ember,frost] [--node] [--mutate NAME ...]

The exact state is built FROM k3s/hub_contract.py (its tables are the one copy of
the allow-list), so "exact" can never drift from the contract; every mutation below
is a deliberate, named departure from it. A file that is absent is an object the
fake kubectl answers NotFound for.

Files: sa.json, role.<ns>.json, rb.<ns>.json, rball.json (every RoleBinding of every namespace, what
`get rolebindings -A` returns), cr.json, crb.json, vap.json / vapb.json (the admission policy and its
binding as the API server returns them, defaults filled in; vap.error makes the fake answer "resource
type not served"), secrets.tsv (name<TAB>type<TAB>sa-name of the Secrets of rpg-k8s-hub: names and
types only), games.txt (or games.notfound / games.error)
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
    "foreign-rb-user", "foreign-rb-group-ns",
    "foreign-crb-user", "foreign-crb-group-ns",
    # a binding to a group that contains every ServiceAccount / every user is NOT a binding naming the hub
    "broad-rb-sa", "broad-rb-authenticated", "broad-rb-unauthenticated",
    "broad-crb-sa", "broad-crb-authenticated", "broad-crb-unauthenticated",
    # a stock cluster's default bindings are the baseline; this one removes them (a cluster with none)
    "no-stock-defaults",
    # the admission policy (F1)
    "no-policy", "no-policy-binding", "policy-api-unserved", "policy-expression-changed",
    "policy-expression-whitespace", "policy-validation-dropped", "policy-validation-added",
    "policy-any-user", "policy-other-user", "policy-fail-open", "policy-wrong-operation",
    "policy-wrong-resource", "policy-exclude-rules",
    # F2 (round 2): a policy that does not look at the Deployment's own ownerReferences / finalizers / identity
    "policy-no-ownerrefs-validation", "policy-no-finalizers-validation", "policy-no-identity-validation", "binding-warn", "binding-audit-only",
    "binding-namespace-selector", "binding-exclude-rules", "binding-other-policy",
    "binding-param-ref",
    # bindings and token Secrets outside the expected set (F3)
    "foreign-rb-kube-system", "foreign-rb-unlisted-game", "foreign-rb-hub-namespace", "foreign-rb-user-elsewhere",
    "legacy-token-secret", "legacy-token-other-account", "rolebindings-all-error", "secrets-error",
}

# The other spellings of the hub identity (User / Group subjects), see hub_contract.names_the_hub.
# Both contain the hub and nobody else.
ALIAS_SUBJECTS = {
    "user": {"kind": "User", "name": "system:serviceaccount:rpg-k8s-hub:studio-hub",
             "apiGroup": "rbac.authorization.k8s.io"},
    "group-ns": {"kind": "Group", "name": "system:serviceaccounts:rpg-k8s-hub",
                 "apiGroup": "rbac.authorization.k8s.io"},
}

# Groups that contain every ServiceAccount / every user: not "naming the hub" (hub_contract.BROAD_GROUP_NAMES).
BROAD_SUBJECTS = {
    "sa": {"kind": "Group", "name": "system:serviceaccounts", "apiGroup": "rbac.authorization.k8s.io"},
    "authenticated": {"kind": "Group", "name": "system:authenticated", "apiGroup": "rbac.authorization.k8s.io"},
    "unauthenticated": {"kind": "Group", "name": "system:unauthenticated", "apiGroup": "rbac.authorization.k8s.io"},
}


def stock_cluster_bindings():
    """The ClusterRoleBindings every stock cluster carries for a group that contains the hub (and everyone
    else). The first is the object the live QA read on k3s v1.35.5 (the one that made cluster.hub_rbac FAIL
    on a clean cluster); the others are the Kubernetes bootstrap defaults for system:authenticated."""
    def crb(name, *groups):
        return {"apiVersion": RBAC, "kind": "ClusterRoleBinding",
                "metadata": {"name": name, "labels": {"kubernetes.io/bootstrapping": "rbac-defaults"},
                             "annotations": {"rbac.authorization.kubernetes.io/autoupdate": "true"}},
                "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "ClusterRole", "name": name},
                "subjects": [{"apiGroup": "rbac.authorization.k8s.io", "kind": "Group", "name": g} for g in groups]}
    return [crb("system:service-account-issuer-discovery", "system:serviceaccounts"),
            crb("system:basic-user", "system:authenticated"),
            crb("system:discovery", "system:authenticated"),
            crb("system:public-info-viewer", "system:authenticated", "system:unauthenticated")]


def api_policy():
    """The ValidatingAdmissionPolicy as the API server returns it: the repository's spec plus the
    defaults it fills in (empty selectors, scope "*", matchPolicy), plus metadata and status."""
    spec = {
        "failurePolicy": hc.POLICY_SPEC["failurePolicy"],
        "matchConstraints": {
            "matchPolicy": "Equivalent", "namespaceSelector": {}, "objectSelector": {},
            "resourceRules": [{"apiGroups": ["apps"], "apiVersions": ["v1"], "operations": ["UPDATE"],
                               "resources": ["deployments"], "scope": "*"}],
        },
        "matchConditions": copy.deepcopy(hc.POLICY_SPEC["matchConditions"]),
        "variables": copy.deepcopy(hc.POLICY_SPEC["variables"]),
        "validations": [{"expression": v["expression"], "message": v["message"], "reason": v["reason"]}
                        for v in hc.POLICY_SPEC["validations"]],
    }
    return {"apiVersion": hc.ADMISSION_API, "kind": "ValidatingAdmissionPolicy",
            "metadata": {"name": hc.POLICY_NAME, "uid": "u-vap", "generation": 1,
                         "managedFields": [{"manager": "kubectl-client-side-apply"}]},
            "spec": spec,
            "status": {"observedGeneration": 1, "typeChecking": {}}}


def api_policy_binding():
    return {"apiVersion": hc.ADMISSION_API, "kind": "ValidatingAdmissionPolicyBinding",
            "metadata": {"name": hc.POLICY_BINDING_NAME, "uid": "u-vapb"},
            "spec": {"policyName": hc.POLICY_NAME, "validationActions": ["Deny"],
                     "matchResources": {"matchPolicy": "Equivalent", "namespaceSelector": {}, "objectSelector": {}}}}


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
    for key, subject in BROAD_SUBJECTS.items():
        if f"broad-rb-{key}" in mut:
            bindings["rpg-k8s-realtime"].append(binding(
                "everyone", "rpg-k8s-realtime", "ClusterRole", "view", subjects=[subject]))

    for ns, doc in roles.items():
        put(f"role.{ns}.json", doc)
    for ns, items in bindings.items():
        put(f"rb.{ns}.json", listing(items))

    # ---- the admission policy and its binding
    vap, vapb = api_policy(), api_policy_binding()
    vspec = vap["spec"]
    if "policy-expression-changed" in mut:
        vspec["validations"][0]["expression"] = "true"
    if "policy-expression-whitespace" in mut:
        vspec["validations"][3]["expression"] += " "
    if "policy-validation-dropped" in mut:
        vspec["validations"].pop()
    if "policy-validation-added" in mut:
        vspec["validations"].append({"expression": "true", "message": "x", "reason": "Forbidden"})
    # the validations that close the metadata hole, by position in hub_contract.POLICY_VALIDATIONS
    for flag, expr in (("policy-no-ownerrefs-validation", "object.metadata.ownerReferences"),
                       ("policy-no-finalizers-validation", "object.metadata.finalizers"),
                       ("policy-no-identity-validation", "object.metadata.name ==")):
        if flag in mut:
            vspec["validations"] = [v for v in vspec["validations"] if not v["expression"].startswith(
                f"has({expr})" if "name" not in expr else expr)]
    if "policy-any-user" in mut:
        vspec["matchConditions"] = []
    if "policy-other-user" in mut:
        vspec["matchConditions"][0]["expression"] = "request.userInfo.username == 'system:serviceaccount:rpg-k8s-hub:other'"
    if "policy-fail-open" in mut:
        vspec["failurePolicy"] = "Ignore"
    if "policy-wrong-operation" in mut:
        vspec["matchConstraints"]["resourceRules"][0]["operations"] = ["CREATE"]
    if "policy-wrong-resource" in mut:
        vspec["matchConstraints"]["resourceRules"][0]["resources"] = ["statefulsets"]
    if "policy-exclude-rules" in mut:
        vspec["matchConstraints"]["excludeResourceRules"] = [
            {"apiGroups": ["apps"], "apiVersions": ["v1"], "operations": ["UPDATE"], "resources": ["deployments"]}]
    bspec = vapb["spec"]
    if "binding-warn" in mut:
        bspec["validationActions"] = ["Warn", "Audit"]
    if "binding-audit-only" in mut:
        bspec["validationActions"] = ["Audit"]
    if "binding-namespace-selector" in mut:
        bspec["matchResources"]["namespaceSelector"] = {"matchLabels": {"rpg-mmo/tier": "app"}}
    if "binding-exclude-rules" in mut:
        bspec["matchResources"]["excludeResourceRules"] = [
            {"apiGroups": ["apps"], "apiVersions": ["v1"], "operations": ["*"], "resources": ["deployments"]}]
    if "binding-other-policy" in mut:
        bspec["policyName"] = "something-else"
    if "binding-param-ref" in mut:
        bspec["paramRef"] = {"name": "p", "parameterNotFoundAction": "Allow"}
    if "policy-api-unserved" in mut:
        (out / "vap.error").write_text("", encoding="utf-8")
    elif "no-policy" not in mut:
        put("vap.json", vap)
    if "no-policy-binding" not in mut:
        put("vapb.json", vapb)

    # ---- RoleBindings of every namespace (kubectl get rolebindings -A) and the Secrets of the hub namespace
    everywhere = []
    for ns, items in bindings.items():
        for item in items:
            item = copy.deepcopy(item)
            item["metadata"]["namespace"] = ns
            everywhere.append(item)
    everywhere.append(binding("system:controller:bootstrap-signer", "kube-system", "Role",
                              "system:controller:bootstrap-signer", subjects=[
        {"kind": "ServiceAccount", "name": "bootstrap-signer", "namespace": "kube-system"}]))
    extras = {
        "foreign-rb-kube-system": ("hub-view", "kube-system", [SUBJECT]),
        "foreign-rb-unlisted-game": ("studio-hub", "rpg-k8s-realtime-zephyr", [SUBJECT]),
        "foreign-rb-hub-namespace": ("hub-self", "rpg-k8s-hub", [SUBJECT]),
        "foreign-rb-user-elsewhere": ("sneaky", "agones-system", [ALIAS_SUBJECTS["user"]]),
    }
    for name, (rb_name, ns, subjects) in extras.items():
        if name in mut:
            everywhere.append(binding(rb_name, ns, "ClusterRole", "edit", subjects=subjects))
    if "rolebindings-all-error" not in mut:
        put("rball.json", listing(everywhere))

    # name<TAB>type<TAB>sa-name; the runbook's token binding Secret is Opaque and belongs to no account
    secret_rows = ["studio-hub-token-binding\tOpaque\t"]
    if "legacy-token-secret" in mut:
        secret_rows.append("studio-hub-token\tkubernetes.io/service-account-token\tstudio-hub")
    if "legacy-token-other-account" in mut:
        secret_rows.append("builder-token\tkubernetes.io/service-account-token\tbuilder")
    if "secrets-error" not in mut:
        (out / "secrets.tsv").write_text("\n".join(secret_rows) + "\n", encoding="utf-8")

    node_role = want_node = args.node or {"node-extra-verb", "node-missing-rule", "node-role-no-binding",
                                          "node-binding-cluster-admin"} & mut
    cluster_items = [binding("system:node", None, "ClusterRole", "system:node", kind="ClusterRoleBinding", subjects=[
        {"kind": "Group", "name": "system:nodes", "apiGroup": "rbac.authorization.k8s.io"}])]
    if "no-stock-defaults" not in mut:
        cluster_items += stock_cluster_bindings()
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
    for key, subject in BROAD_SUBJECTS.items():
        if f"broad-crb-{key}" in mut:
            cluster_items.append(binding("everyone", None, "ClusterRole", "view",
                                         kind="ClusterRoleBinding", subjects=[subject]))
    put("crb.json", listing(cluster_items))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
