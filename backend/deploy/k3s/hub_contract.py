#!/usr/bin/env python3
"""Contract of the Studio hub's Kubernetes identity (backend/deploy/k8s/hub/) and
of the per-game Role that deploy/k8s/games/render.py renders for it.

THIS FILE IS THE SINGLE COPY OF THE ALLOW-LIST IN CODE. The manifests in
k8s/hub/20-rbac.yaml and k8s/hub/optional/node-access.yaml repeat it as YAML
(that is what the cluster needs); every check, test and the `cluster.hub_rbac`
verify check compares against REALTIME / DATA / NODE below and nothing else.
Widening the hub therefore takes one reviewed change HERE, and a manifest that
differs from this file fails CI.

Standard library + PyYAML only: no jsonschema, no Agones download, no cluster, so
it runs in the CI job `test-deploy-offline`. validate-manifests.py imports it and
runs the same checks as part of its no-argument run.

Comparison is by (apiGroup, resource, verb) TRIPLES, never by rule order, by how
the triples are grouped into rules, or by names. Two Roles with the same triples
are equal; one extra verb, one extra resource or one missing triple is a
difference, in either direction.

The contract (each line is one rule below, each rule has a test in
tests/test_hub_contract.py that shows it rejecting a mutation):

  Directory k8s/hub/ (the part dev-up.sh applies)
    * the only kinds are Namespace, ServiceAccount, Role, RoleBinding and the two
      cluster-scoped admission objects below: NO ClusterRole and NO
      ClusterRoleBinding, no Secret, no ConfigMap, no workload
    * exactly one ValidatingAdmissionPolicy studio-hub-restart-only and one
      ValidatingAdmissionPolicyBinding of the same name, equal (after defaults) to
      POLICY_SPEC / BINDING_SPEC below: the policy is what narrows `patch
      deployments` to a rollout restart, and only for the hub's username
    * exactly one Namespace rpg-k8s-hub
    * exactly one ServiceAccount studio-hub in it, automountServiceAccountToken
      explicitly false, no `secrets` / `imagePullSecrets` on it
    * Roles named studio-hub in exactly rpg-k8s-realtime (REALTIME triples) and
      rpg-k8s-data (DATA triples); no Role anywhere else (not rpg-k8s-hub, not
      rpg-k8s-logging, not agones-system, not kube-system)
    * a rule carries only apiGroups / resources / verbs: no resourceNames, no
      nonResourceURLs, no `*` in any of the three
    * every Role has a RoleBinding named studio-hub in the same namespace whose
      only subject is ServiceAccount studio-hub of rpg-k8s-hub and whose roleRef
      is that Role (apiGroup rbac.authorization.k8s.io)
  Directory k8s/hub/optional/ (opt-in, never applied by dev-up.sh)
    * exactly a ClusterRole studio-hub-nodes (NODE triples, no aggregationRule) and
      a ClusterRoleBinding studio-hub-nodes to the same ServiceAccount; nothing else
  Per game (rendered by render.py), check_game_hub()
    * in the game's namespace exactly one Role studio-hub with the REALTIME
      triples and one RoleBinding with the exact subject and roleRef
    * no ClusterRole and no ClusterRoleBinding in the render
    * no other RoleBinding names the hub's ServiceAccount
  'Names the hub' (every binding check below) is decided BY SUBJECT, never by the name of
  the binding: a subject of kind ServiceAccount studio-hub, kind User
  system:serviceaccount:rpg-k8s-hub:studio-hub, or kind Group
  system:serviceaccounts:rpg-k8s-hub (the three spellings of an identity that contains the
  hub and nobody else), kind and name compared exactly. A Group that contains EVERY
  ServiceAccount or every user (system:serviceaccounts, system:authenticated,
  system:unauthenticated: BROAD_GROUP_NAMES) does not name the hub: Kubernetes binds its own
  defaults to them (system:basic-user, system:discovery, system:public-info-viewer,
  system:service-account-issuer-discovery), so a check that counted them would FAIL on every
  cluster. What that leaves unseen is written in k8s/verify/README.md.

Usage:
    python hub_contract.py policy --kind K         # compare a live policy / binding (JSON on stdin)
    python hub_contract.py secrets                 # check name<TAB>type<TAB>sa-name lines of the hub namespace
    python hub_contract.py                         # the committed k8s/hub (+ optional/) plus a rendered sample game
    python hub_contract.py --no-game               # skip the render.py leg
    python hub_contract.py --dir DIR             # another directory (shows a mutation failing)
    python hub_contract.py compare --shape S       # compare rules read as JSON on stdin
    python hub_contract.py bindings --scope S      # check live (Cluster)RoleBindings read as JSON on stdin

`compare` is the entry point of the verify check `cluster.hub_rbac` (called through
k8s/verify/lib/python_lf.sh): it reads the live Role or ClusterRole as JSON on
stdin and compares it with the named allow-list. Accepted input is either the
object itself (`kubectl get role studio-hub -n NS -o json`: its "rules" are read)
or a bare list of rules. `--shape` is `realtime`, `data` or `node`.

    kubectl get role studio-hub -n rpg-k8s-data -o json | python hub_contract.py compare --shape data

Output of `compare`: nothing when the rules are equal. Otherwise one line per
difference on stdout, in a stable sorted order:

    extra: apps/statefulsets patch        granted but not in the allow-list
    missing: core/pods delete             in the allow-list but not granted
    invalid: rule 3 carries 'resourceNames' ...   a rule shape the contract forbids

(`core` is the API group ""). Exit codes: 0 equal, 1 different, 2 the input is not
readable (not JSON, not a Role/ClusterRole/list of rules, unknown shape).

`bindings` is the second entry point of the same verify check. It reads the list
`kubectl get rolebindings -n NS -o json` (`--scope namespaced --namespace NS`) or
`kubectl get clusterrolebindings -o json` (`--scope cluster --node present|absent`)
and prints one line per problem, exit 0 when there is none:

    missing: RoleBinding NS/studio-hub        the hub's binding is not there
    binding: RoleBinding NS/studio-hub: ...   wrong subjects or roleRef
    foreign: RoleBinding NS/x names ...       another binding names the hub's ServiceAccount

Namespaced: `studio-hub` must exist with the exact subject and roleRef, and no other
RoleBinding may name the ServiceAccount. Cluster: no ClusterRoleBinding may name the
ServiceAccount except `studio-hub-nodes`, which must then be exact (`--node present`)
and must not exist at all when the node ClusterRole is absent (`--node absent`).
All-namespaces (`--scope all-namespaces --expected NS,NS,...`, input from
`kubectl get rolebindings -A -o json`): no RoleBinding in a namespace OUTSIDE the
expected list may name the ServiceAccount, whatever it is called.
Exit codes as for `compare`: 0 none, 1 problems, 2 unreadable input.

`policy --kind policy|binding` reads one live ValidatingAdmissionPolicy or
ValidatingAdmissionPolicyBinding as JSON and prints one `differs:` line per departure
from POLICY_SPEC / BINDING_SPEC (API-server defaults filled in on both sides). Exit
codes as for `compare`.

`secrets` reads `name<TAB>type<TAB>kubernetes.io/service-account.name` lines (the
output of a jsonpath over the Secrets of rpg-k8s-hub: names and types only, the data is
never requested) and prints a `legacy token:` line for every Secret of type
kubernetes.io/service-account-token that belongs to the hub ServiceAccount and is not
in DOCUMENTED_TOKEN_SECRETS (none: the runbook mints bound tokens, never a Secret).

Exit codes of the no-argument run: 0 no problem, 1 at least one problem, 2 an input
could not be read.
"""

from __future__ import annotations

import argparse
import importlib.util
import json
import pathlib
import sys

try:
    import yaml
except ImportError:  # pragma: no cover
    # Only the manifest checks need it. `compare` and `bindings` (the verify check's
    # entry points, run on the deploy host) read JSON and must not require it.
    yaml = None

DEPLOY_DIR = pathlib.Path(__file__).resolve().parent.parent
HUB_DIR = DEPLOY_DIR / "k8s" / "hub"
GAME_RENDERER = DEPLOY_DIR / "k8s" / "games" / "render.py"
OPTIONAL_SUBDIR = "optional"

HUB_NAMESPACE = "rpg-k8s-hub"
HUB_ACCOUNT = "studio-hub"
HUB_ROLE = "studio-hub"
NODE_ROLE = "studio-hub-nodes"
RBAC_GROUP = "rbac.authorization.k8s.io"

# The namespaces whose Role is committed in k8s/hub/20-rbac.yaml, and the shape
# each one gets. Every rpg-k8s-realtime-<game> is rendered by render.py with the
# REALTIME shape.
STATIC_ROLE_SHAPES = {
    "rpg-k8s-realtime": "realtime",
    "rpg-k8s-data": "data",
}

WANT_SUBJECTS = [{"kind": "ServiceAccount", "name": HUB_ACCOUNT, "namespace": HUB_NAMESPACE}]
RULE_KEYS = {"apiGroups", "resources", "verbs"}

# --------------------------------------------------------------------------
# THE ALLOW-LIST. (apiGroup, resources, verbs); "" is the core group.
# --------------------------------------------------------------------------

REALTIME_RULES = (
    ("", ("pods",), ("get", "list", "watch", "delete")),
    ("", ("pods/log",), ("get",)),
    ("", ("events", "services"), ("get", "list", "watch")),
    ("events.k8s.io", ("events",), ("get", "list", "watch")),
    ("apps", ("deployments",), ("get", "list", "watch", "patch")),
    ("apps", ("statefulsets", "replicasets"), ("get", "list", "watch")),
    # ---- the Agones rows: only where Agones objects live ----
    ("agones.dev", ("fleets", "gameserversets"), ("get", "list", "watch")),
    ("agones.dev", ("fleets/scale",), ("get", "update", "patch")),
    ("agones.dev", ("gameservers",), ("get", "list", "watch", "delete")),
    ("autoscaling.agones.dev", ("fleetautoscalers",), ("get", "list", "watch", "patch", "update")),
    ("allocation.agones.dev", ("gameserverallocations",), ("create",)),
)

# rpg-k8s-data holds Nakama, PostgreSQL and Redis and no Agones object: the first
# six REALTIME rows, nothing from agones.dev / autoscaling.agones.dev /
# allocation.agones.dev. Its only writes are `delete pods` and `patch deployments`.
_AGONES_GROUPS = ("agones.dev", "autoscaling.agones.dev", "allocation.agones.dev")
DATA_RULES = tuple(r for r in REALTIME_RULES if r[0] not in _AGONES_GROUPS)

# Opt-in, cluster-scoped (k8s/hub/optional/node-access.yaml).
NODE_RULES = (
    ("", ("nodes",), ("get", "list", "watch", "patch")),
    ("", ("pods",), ("list",)),
    ("", ("pods/eviction",), ("create",)),
)

Triple = tuple  # (apiGroup, resource, verb)


def expand(rules) -> frozenset:
    """{(apiGroup, resource, verb)} of a rule table in the (group, resources, verbs) form above."""
    return frozenset((g, r, v) for g, resources, verbs in rules for r in resources for v in verbs)


SHAPES: dict[str, frozenset] = {
    "realtime": expand(REALTIME_RULES),
    "data": expand(DATA_RULES),
    "node": expand(NODE_RULES),
}

Docs = list  # list of (label, document)


def fmt(triple: Triple) -> str:
    group, resource, verb = triple
    return f"{group or 'core'}/{resource} {verb}"


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


# --------------------------------------------------------------------------
# Rule comparison (static manifests, rendered games, and live JSON)
# --------------------------------------------------------------------------


def _as_list(value) -> list:
    return value if isinstance(value, list) else []


def rules_to_triples(rules) -> tuple[set, list[str]]:
    """(triples, invalid) of a list of RBAC rules in manifest / API JSON form.

    `invalid` holds a line for every rule shape the contract forbids: a key other
    than apiGroups/resources/verbs (resourceNames, nonResourceURLs), a wildcard,
    a rule that is not an object.
    """
    triples: set = set()
    invalid: list[str] = []
    if not isinstance(rules, list):
        return triples, ["invalid: has no rules list"]
    for i, rule in enumerate(rules, start=1):
        if not isinstance(rule, dict):
            invalid.append(f"invalid: rule {i} is not an object")
            continue
        for key in sorted(set(rule) - RULE_KEYS):
            invalid.append(
                f"invalid: rule {i} carries {key!r}; only apiGroups/resources/verbs are part of the grant"
            )
        groups = _as_list(rule.get("apiGroups"))
        resources = _as_list(rule.get("resources"))
        verbs = _as_list(rule.get("verbs"))
        for field, values in (("apiGroups", groups), ("resources", resources), ("verbs", verbs)):
            if any(v == "*" for v in values):
                invalid.append(f"invalid: rule {i} has a wildcard '*' in {field}")
        for g in groups:
            for r in resources:
                for v in verbs:
                    triples.add((g, r, v))
    return triples, invalid


def compare_rules(rules, shape: str) -> list[str]:
    """Lines describing how `rules` differ from the named allow-list ([] = equal).

    Reads rules, not names: the grouping of triples into rules and their order do
    not matter. Extra triples, missing triples and forbidden rule shapes are all
    reported.
    """
    want = SHAPES[shape]
    got, invalid = rules_to_triples(rules)
    lines = list(invalid)
    lines += [f"extra: {fmt(t)}" for t in sorted(got - want)]
    lines += [f"missing: {fmt(t)}" for t in sorted(want - got)]
    return lines


def _role_problems(role: dict, shape: str, label: str) -> list[str]:
    problems = [f"{label}: {line}" for line in compare_rules(role.get("rules"), shape)]
    if role.get("aggregationRule"):
        problems.append(f"{label}: carries an aggregationRule; the rules must be written out here")
    return problems


def _binding_problems(binding: dict, role_kind: str, role_name: str, label: str) -> list[str]:
    problems: list[str] = []
    if binding.get("subjects") != WANT_SUBJECTS:
        problems.append(
            f"{label}: subjects are {binding.get('subjects')}, but the only subject allowed is {WANT_SUBJECTS}"
        )
    ref = binding.get("roleRef") or {}
    want = {"apiGroup": RBAC_GROUP, "kind": role_kind, "name": role_name}
    if ref != want:
        problems.append(f"{label}: roleRef is {ref}, expected {want}")
    return problems


# Other spellings of the same identity. Kubernetes authenticates the hub's token as
# User `system:serviceaccount:<ns>:<name>` in the Groups `system:serviceaccounts`,
# `system:serviceaccounts:<ns>` and `system:authenticated`. The User and the namespace Group
# contain the hub and nobody else, so a binding naming either grants the hub its rights
# without a ServiceAccount subject. Kind and name are compared EXACTLY (case-sensitive, no
# prefix match).
HUB_USER_NAME = f"system:serviceaccount:{HUB_NAMESPACE}:{HUB_ACCOUNT}"
HUB_GROUP_NAMES = (f"system:serviceaccounts:{HUB_NAMESPACE}",)

# The groups that contain every ServiceAccount (or every user). A binding to one of them
# reaches the hub and everyone else alike, so it is not "a binding naming the hub": it is
# how Kubernetes' own defaults are bound (system:service-account-issuer-discovery to
# system:serviceaccounts; system:basic-user, system:discovery and system:public-info-viewer
# to system:authenticated / system:unauthenticated). Classifying them as the hub made
# cluster.hub_rbac FAIL on a stock k3s (live QA, round 2). They are listed so that the
# decision is one named constant and a test, not a gap.
BROAD_GROUP_NAMES = ("system:serviceaccounts", "system:authenticated", "system:unauthenticated")


def names_the_hub(binding: dict) -> bool:
    """True when a RoleBinding / ClusterRoleBinding reaches the hub identity through its subjects.

    Decided by SUBJECT, never by the binding's name. Matches: kind ServiceAccount
    `studio-hub` (namespace rpg-k8s-hub, or empty/missing); kind User
    `system:serviceaccount:rpg-k8s-hub:studio-hub`; kind Group
    `system:serviceaccounts:rpg-k8s-hub`. One such subject is enough, whatever else the
    binding lists.

    Not matched, on purpose: the groups that contain EVERY ServiceAccount or every user
    (BROAD_GROUP_NAMES: `system:serviceaccounts`, `system:authenticated`,
    `system:unauthenticated`). Kubernetes binds its own defaults to them, so counting them
    would fail every cluster. The cost, stated in k8s/verify/README.md: a custom binding
    that gives a broad role to one of those groups raises the rights of the hub together
    with every other ServiceAccount, and this check does not see it.

    A ServiceAccount subject with an empty or missing namespace inside a RoleBinding of
    rpg-k8s-hub does not arise: that namespace is not inspected (no hub binding lives
    there), and an empty namespace is counted as naming the hub wherever it is found.
    """
    for s in _as_list(binding.get("subjects")):
        if not isinstance(s, dict):
            continue
        kind, name = s.get("kind"), s.get("name")
        if kind == "ServiceAccount" and name == HUB_ACCOUNT and s.get("namespace") in (HUB_NAMESPACE, None, ""):
            return True
        if kind == "User" and name == HUB_USER_NAME:
            return True
        if kind == "Group" and name in HUB_GROUP_NAMES:
            return True
    return False


# --------------------------------------------------------------------------
# The admission policy that makes `patch deployments` a rollout restart
# --------------------------------------------------------------------------
#
# RBAC cannot narrow `patch` on a Deployment: the verb also rewrites the pod
# template (image, env, securityContext, hostPath volumes), and a Deployment's pods
# are created by the Deployment controller, not by the caller, so the hub would need
# no `create pods` to run anything on a node. A ValidatingAdmissionPolicy can: it sees
# the old and the new object. THE POLICY IS WHAT MAKES "RESTART ONLY" TRUE; without
# it the token is node-root-equivalent in every namespace where the hub has a Role
# and no Pod Security label stops a privileged or hostPath pod.
#
# The two objects are CLUSTER-SCOPED (the API has no namespaced policy). That is the
# one exception to "no cluster-scoped object by default": no ClusterRole and no
# ClusterRoleBinding, and one policy that can only RESTRICT the hub (it matches one
# username and can only deny).
#
# What of a Deployment's OBJECT (not spec) the hub can change, field by field (the same table is
# pinned in tests/test_hub_contract.py, METADATA_FIELDS): labels, annotations, ownerReferences,
# finalizers, name, namespace and generateName are compared (validations 8-11). uid,
# creationTimestamp, deletionTimestamp, deletionGracePeriodSeconds, generation, resourceVersion and
# selfLink are decided by the API server on every update (immutable, reset or only a lock), and
# `status` is replaced by the stored one on an update of the main resource: none is compared here.
# managedFields is deliberately NOT compared: the field manager rewrites it on every patch
# (including the legitimate restart), so an equality check would deny the restart itself. What
# the hub can do with it is bookkeeping of which manager owns which field; it carries no owner
# edge, no finalizer and no pod field, and it can neither start a deletion nor change a spec.
#
# The expected spec lives here, in code, like the Role allow-list: k8s/hub/
# 15-admission-policy.yaml must equal it (check_hub_manifests) and the verify check
# `cluster.hub_rbac` compares the LIVE objects with it (`hub_contract.py policy`).
# Each expression is explained in the YAML, next to the expression.
#
# NOT PROVEN OFFLINE: no CEL evaluator is available to this repository's toolchain,
# so what is pinned is the TEXT of the expressions (and the tests that the text
# covers each field a restart must leave alone), not their evaluation by an API
# server. The live check in docs/RUNBOOK.md ("Studio hub token", live check) is the
# proof: a restart patch is allowed; image, env, privileged, hostPath, replicas and
# label patches are denied.

POLICY_NAME = "studio-hub-restart-only"
POLICY_BINDING_NAME = POLICY_NAME
ADMISSION_API = "admissionregistration.k8s.io/v1"
POLICY_KIND = "ValidatingAdmissionPolicy"
POLICY_BINDING_KIND = "ValidatingAdmissionPolicyBinding"
RESTART_ANNOTATION = "kubectl.kubernetes.io/restartedAt"
# Kubernetes serves admissionregistration.k8s.io/v1 ValidatingAdmissionPolicy (with
# matchConditions and variables) from 1.30.
MIN_KUBERNETES_FOR_POLICY = "1.30"
# A Secret of type kubernetes.io/service-account-token for the hub that the runbook
# documents. There is none: the runbook mints bound, expiring tokens with `kubectl
# create token` and rejects the never-expiring Secret.
DOCUMENTED_TOKEN_SECRETS: tuple = ()


def _opt_equal_pair(new: str, old: str) -> str:
    """CEL: the field is present on both objects with an equal value, or absent on both.

    `has()` first, because reading an absent field of an object is an error in CEL and
    an error under failurePolicy Fail denies the request (here that would deny a
    restart, never allow a change)."""
    return f"has({new}) == has({old}) && (!has({new}) || {new} == {old})"


_NEW_T, _OLD_T = "variables.newTemplate", "variables.oldTemplate"
_NEW_A, _OLD_A = f"{_NEW_T}.metadata.annotations", f"{_OLD_T}.metadata.annotations"

POLICY_MATCH_USER_EXPRESSION = f"request.userInfo.username == '{HUB_USER_NAME}'"

POLICY_VALIDATIONS = (
    # 1. The whole pod spec (image, env, command, securityContext, volumes, hostPID,
    #    hostNetwork, serviceAccountName, every container, every future field) is equal.
    (
        f"{_NEW_T}.spec == {_OLD_T}.spec",
        "the Studio hub may only restart a Deployment: spec.template.spec (the pod spec) must not change",
    ),
    # 2. Template labels equal (they are the pod labels: selectors, Services, NetworkPolicy).
    (
        _opt_equal_pair(f"{_NEW_T}.metadata.labels", f"{_OLD_T}.metadata.labels"),
        "the Studio hub may only restart a Deployment: spec.template.metadata.labels must not change",
    ),
    # 3. Template finalizers equal (a finalizer on every pod would make pods undeletable).
    (
        _opt_equal_pair(f"{_NEW_T}.metadata.finalizers", f"{_OLD_T}.metadata.finalizers"),
        "the Studio hub may only restart a Deployment: spec.template.metadata.finalizers must not change",
    ),
    # 4. In the template annotations no key other than restartedAt is added or changed.
    (
        f"!has({_NEW_A}) || {_NEW_A}.all(k, k == '{RESTART_ANNOTATION}' || "
        f"(has({_OLD_A}) && k in {_OLD_A} && {_OLD_A}[k] == {_NEW_A}[k]))",
        f"the Studio hub may only restart a Deployment: the only pod template annotation it may set is {RESTART_ANNOTATION}",
    ),
    # 5. ... and none other is removed.
    (
        f"!has({_OLD_A}) || {_OLD_A}.all(k, k == '{RESTART_ANNOTATION}' || "
        f"(has({_NEW_A}) && k in {_NEW_A}))",
        f"the Studio hub may only restart a Deployment: it may not remove a pod template annotation other than {RESTART_ANNOTATION}",
    ),
    # 6. The sibling spec fields Kubernetes always defaults (so always present).
    (
        "object.spec.replicas == oldObject.spec.replicas && object.spec.selector == oldObject.spec.selector && "
        "object.spec.strategy == oldObject.spec.strategy && "
        "object.spec.revisionHistoryLimit == oldObject.spec.revisionHistoryLimit && "
        "object.spec.progressDeadlineSeconds == oldObject.spec.progressDeadlineSeconds",
        "the Studio hub may only restart a Deployment: replicas, selector, strategy, revisionHistoryLimit and progressDeadlineSeconds must not change",
    ),
    # 7. The sibling spec fields that have no default (absent when zero / false).
    (
        _opt_equal_pair("object.spec.minReadySeconds", "oldObject.spec.minReadySeconds")
        + " && "
        + _opt_equal_pair("object.spec.paused", "oldObject.spec.paused"),
        "the Studio hub may only restart a Deployment: minReadySeconds and paused must not change",
    ),
    # 8. The Deployment's own labels and annotations equal.
    (
        _opt_equal_pair("object.metadata.labels", "oldObject.metadata.labels"),
        "the Studio hub may only restart a Deployment: its metadata.labels must not change",
    ),
    (
        _opt_equal_pair("object.metadata.annotations", "oldObject.metadata.annotations"),
        "the Studio hub may only restart a Deployment: its metadata.annotations must not change",
    ),
    # 9. The Deployment's own ownerReferences equal. A reference to an owner that does not exist
    #    makes the garbage collector delete the Deployment, which the Role (no `delete
    #    deployments`) does not allow. Found by the live QA: validations 1-8 never looked at it.
    (
        _opt_equal_pair("object.metadata.ownerReferences", "oldObject.metadata.ownerReferences"),
        "the Studio hub may only restart a Deployment: its metadata.ownerReferences must not change",
    ),
    # 10. ... and its own finalizers (validation 3 is the TEMPLATE's). A finalizer makes every
    #     delete of the Deployment, and the deletion of its namespace, wait until an admin removes it.
    (
        _opt_equal_pair("object.metadata.finalizers", "oldObject.metadata.finalizers"),
        "the Studio hub may only restart a Deployment: its metadata.finalizers must not change",
    ),
    # 11. The identity: name and namespace are immutable in the API anyway, generateName is not
    #     validated on update; all three equal, so the policy does not rest on that.
    (
        "object.metadata.name == oldObject.metadata.name && "
        "object.metadata.namespace == oldObject.metadata.namespace && "
        + _opt_equal_pair("object.metadata.generateName", "oldObject.metadata.generateName"),
        "the Studio hub may only restart a Deployment: its metadata.name, namespace and generateName must not change",
    ),
)

POLICY_SPEC = {
    "failurePolicy": "Fail",
    "matchConstraints": {
        "matchPolicy": "Equivalent",
        "namespaceSelector": None,
        "objectSelector": None,
        "resourceRules": [
            {
                "apiGroups": ["apps"],
                "apiVersions": ["v1"],
                "operations": ["UPDATE"],
                "resources": ["deployments"],
                "resourceNames": [],
                "scope": "*",
            }
        ],
        "excludeResourceRules": [],
    },
    "matchConditions": [{"name": "only-the-studio-hub", "expression": POLICY_MATCH_USER_EXPRESSION}],
    "variables": [
        {"name": "newTemplate", "expression": "object.spec.template"},
        {"name": "oldTemplate", "expression": "oldObject.spec.template"},
    ],
    "validations": [
        {"expression": e, "message": m, "reason": "Forbidden", "messageExpression": None}
        for e, m in POLICY_VALIDATIONS
    ],
    "auditAnnotations": [],
    "paramKind": None,
}

BINDING_SPEC = {
    "policyName": POLICY_NAME,
    "validationActions": ["Deny"],
    "paramRef": None,
    "matchResources": {
        "matchPolicy": "Equivalent",
        "namespaceSelector": None,
        "objectSelector": None,
        "resourceRules": [],
        "excludeResourceRules": [],
    },
}


def _norm_selector(sel):
    """None for a missing or empty selector ({} and {matchLabels: {}} select everything)."""
    if not isinstance(sel, dict):
        return sel if sel else None
    cleaned = {k: v for k, v in sel.items() if v}
    return cleaned or None


def _norm_rule(rule) -> dict:
    rule = rule if isinstance(rule, dict) else {}
    return {
        "apiGroups": sorted(_as_list(rule.get("apiGroups"))),
        "apiVersions": sorted(_as_list(rule.get("apiVersions"))),
        "operations": sorted(_as_list(rule.get("operations"))),
        "resources": sorted(_as_list(rule.get("resources"))),
        "resourceNames": sorted(_as_list(rule.get("resourceNames"))),
        "scope": rule.get("scope") or "*",
    }


def _norm_match(match) -> dict:
    match = match if isinstance(match, dict) else {}
    out = {
        "matchPolicy": match.get("matchPolicy") or "Equivalent",
        "namespaceSelector": _norm_selector(match.get("namespaceSelector")),
        "objectSelector": _norm_selector(match.get("objectSelector")),
        "resourceRules": sorted((_norm_rule(r) for r in _as_list(match.get("resourceRules"))), key=json.dumps),
        "excludeResourceRules": sorted(
            (_norm_rule(r) for r in _as_list(match.get("excludeResourceRules"))), key=json.dumps
        ),
    }
    for key in sorted(set(match) - set(out)):
        out[key] = match[key]
    return out


def normalize_policy_spec(spec) -> dict:
    """A ValidatingAdmissionPolicy spec with the API server's defaults filled in, so a
    manifest and the object read back from a cluster compare equal when they mean the
    same. Unknown keys are kept: an unexpected field is a difference."""
    spec = spec if isinstance(spec, dict) else {}
    known = {"failurePolicy", "matchConstraints", "matchConditions", "variables", "validations",
             "auditAnnotations", "paramKind"}
    out = {
        "failurePolicy": spec.get("failurePolicy") or "Fail",
        "matchConstraints": _norm_match(spec.get("matchConstraints")),
        "matchConditions": [
            {"name": c.get("name"), "expression": c.get("expression")}
            for c in _as_list(spec.get("matchConditions")) if isinstance(c, dict)
        ],
        "variables": [
            {"name": v.get("name"), "expression": v.get("expression")}
            for v in _as_list(spec.get("variables")) if isinstance(v, dict)
        ],
        "validations": [
            {"expression": v.get("expression"), "message": v.get("message"), "reason": v.get("reason"),
             "messageExpression": v.get("messageExpression")}
            for v in _as_list(spec.get("validations")) if isinstance(v, dict)
        ],
        "auditAnnotations": _as_list(spec.get("auditAnnotations")),
        "paramKind": spec.get("paramKind") or None,
    }
    for key in sorted(set(spec) - known):
        out[key] = spec[key]
    return out


def normalize_binding_spec(spec) -> dict:
    spec = spec if isinstance(spec, dict) else {}
    known = {"policyName", "validationActions", "paramRef", "matchResources"}
    out = {
        "policyName": spec.get("policyName"),
        "validationActions": sorted(_as_list(spec.get("validationActions"))),
        "paramRef": spec.get("paramRef") or None,
        "matchResources": _norm_match(spec.get("matchResources")),
    }
    for key in sorted(set(spec) - known):
        out[key] = spec[key]
    return out


def _short(value) -> str:
    text = json.dumps(value, sort_keys=True)
    return text if len(text) <= 140 else text[:137] + "..."


def _diff(path: str, want, got, out: list[str]) -> None:
    if isinstance(want, dict) and isinstance(got, dict):
        for key in sorted(set(want) | set(got)):
            if key not in want:
                out.append(f"differs: {path}.{key}: not in the repository's policy, got {_short(got[key])}")
            elif key not in got:
                out.append(f"differs: {path}.{key}: missing, want {_short(want[key])}")
            else:
                _diff(f"{path}.{key}", want[key], got[key], out)
    elif isinstance(want, list) and isinstance(got, list):
        if len(want) != len(got):
            out.append(f"differs: {path}: {len(got)} entries, the repository's policy has {len(want)}")
        for i, (w, g) in enumerate(zip(want, got)):
            _diff(f"{path}[{i}]", w, g, out)
    elif want != got:
        out.append(f"differs: {path}: want {_short(want)}, got {_short(got)}")


def policy_problems(obj, kind: str = "policy") -> list[str]:
    """How a ValidatingAdmissionPolicy (kind "policy") or its binding (kind "binding")
    differs from POLICY_SPEC / BINDING_SPEC; [] = equal. Reads spec only (metadata,
    status and the type-checking warnings are not part of the contract)."""
    if not isinstance(obj, dict):
        return ["differs: the object is not readable JSON"]
    problems: list[str] = []
    if kind == "policy":
        _diff("spec", normalize_policy_spec(POLICY_SPEC), normalize_policy_spec(obj.get("spec")), problems)
    else:
        _diff("spec", normalize_binding_spec(BINDING_SPEC), normalize_binding_spec(obj.get("spec")), problems)
    return problems


def check_admission_manifests(docs: Docs) -> list[str]:
    """The policy and its binding in k8s/hub: exactly one of each, named, cluster-scoped, equal to the contract."""
    problems: list[str] = []
    for kind, mode in ((POLICY_KIND, "policy"), (POLICY_BINDING_KIND, "binding")):
        found = [(lab, d) for lab, d in docs if d.get("kind") == kind]
        if len(found) != 1:
            problems.append(f"expected exactly one {kind} {POLICY_NAME!r}, got {len(found)}")
            continue
        lab, d = found[0]
        if d.get("apiVersion") != ADMISSION_API:
            problems.append(f"{lab}: apiVersion is {d.get('apiVersion')!r}, expected {ADMISSION_API!r}")
        if _name(d) != POLICY_NAME:
            problems.append(f"{lab}: named {_name(d)!r}, expected {POLICY_NAME!r}")
        if _ns(d):
            problems.append(f"{lab}: has a namespace; {kind} is cluster-scoped")
        problems += [f"{lab}: {line}" for line in policy_problems(d, mode)]
    return problems


def secrets_problems(text: str) -> list[str]:
    """`name<TAB>type<TAB>service-account-name` lines (names and types only) -> a problem
    for every legacy service-account-token Secret of the hub ServiceAccount that is not documented."""
    problems: list[str] = []
    for line in text.splitlines():
        if not line.strip():
            continue
        name, type_, account = (line.split("\t") + ["", "", ""])[:3]
        if (type_ == "kubernetes.io/service-account-token" and account == HUB_ACCOUNT
                and name not in DOCUMENTED_TOKEN_SECRETS):
            problems.append(
                f"legacy token: Secret {HUB_NAMESPACE}/{name} is a never-expiring "
                f"kubernetes.io/service-account-token for {HUB_ACCOUNT}; the runbook mints bound tokens only. "
                f"Delete it (kubectl -n {HUB_NAMESPACE} delete secret {name})"
            )
    return problems


# --------------------------------------------------------------------------
# Loading
# --------------------------------------------------------------------------


def load_dir(path: pathlib.Path, prefix: str = "") -> tuple[Docs, list[str]]:
    """Every document of every *.yaml DIRECTLY in `path` (no recursion: the opt-in
    file lives in a subdirectory precisely so that this glob never sees it)."""
    docs: Docs = []
    problems: list[str] = []
    files = sorted(path.glob("*.yaml")) if path.is_dir() else []
    if not files:
        problems.append(f"{path}: no *.yaml files")
    for f in files:
        try:
            for doc in yaml.safe_load_all(f.read_text(encoding="utf-8")):
                if doc:
                    docs.append((f"{prefix}{f.name}:{doc.get('kind')}/{_name(doc)}", doc))
        except yaml.YAMLError as exc:
            problems.append(f"{prefix}{f.name}: YAML parse error: {exc}")
    return docs, problems


def load_hub(path: pathlib.Path = HUB_DIR) -> tuple[Docs, Docs, list[str]]:
    """(default docs, optional docs, input problems) of a k8s/hub-shaped directory."""
    docs, problems = load_dir(path)
    optional, opt_problems = load_dir(path / OPTIONAL_SUBDIR, prefix=f"{OPTIONAL_SUBDIR}/")
    return docs, optional, problems + opt_problems


# --------------------------------------------------------------------------
# k8s/hub as committed
# --------------------------------------------------------------------------

DEFAULT_KINDS = ("Namespace", "ServiceAccount", "Role", "RoleBinding", POLICY_KIND, POLICY_BINDING_KIND)


def check_hub_manifests(docs: Docs) -> list[str]:
    """The rules for the part of k8s/hub that dev-up.sh applies. Returns the problems."""
    problems: list[str] = check_admission_manifests(docs)
    for lab, d in docs:
        kind = d.get("kind")
        if kind in ("ClusterRole", "ClusterRoleBinding"):
            problems.append(
                f"{lab}: {kind} is not allowed here; the only cluster-scoped grant is "
                f"{OPTIONAL_SUBDIR}/node-access.yaml, which dev-up.sh does not apply (the admission "
                f"policy below only RESTRICTS the hub)"
            )
        elif kind not in DEFAULT_KINDS:
            problems.append(f"{lab}: kind {kind!r} is not allowed; the hub directory holds {', '.join(DEFAULT_KINDS)} only")

    # Namespace
    namespaces = [(lab, d) for lab, d in docs if d.get("kind") == "Namespace"]
    if [_name(d) for _, d in namespaces] != [HUB_NAMESPACE]:
        problems.append(f"expected exactly one Namespace {HUB_NAMESPACE!r}, got {[_name(d) for _, d in namespaces]}")

    # ServiceAccount
    accounts = [(lab, d) for lab, d in docs if d.get("kind") == "ServiceAccount"]
    mine = [(lab, d) for lab, d in accounts if _name(d) == HUB_ACCOUNT and _ns(d) == HUB_NAMESPACE]
    if len(mine) != 1:
        problems.append(f"expected exactly one ServiceAccount {HUB_ACCOUNT!r} in {HUB_NAMESPACE!r}, got {len(mine)}")
    for lab, d in accounts:
        if (lab, d) not in mine:
            problems.append(f"{lab}: a ServiceAccount other than {HUB_ACCOUNT!r} in {HUB_NAMESPACE!r}")
    for lab, d in mine:
        if d.get("automountServiceAccountToken") is not False:
            problems.append(
                f"{lab}: automountServiceAccountToken must be explicitly false; no pod runs as the hub, "
                f"its credential is a token an operator mints"
            )
        for key in ("secrets", "imagePullSecrets"):
            if d.get(key):
                problems.append(f"{lab}: lists {key}; no Secret is referenced by the hub's identity")

    # Roles and RoleBindings
    roles = [(lab, d) for lab, d in docs if d.get("kind") == "Role"]
    bindings = [(lab, d) for lab, d in docs if d.get("kind") == "RoleBinding"]
    seen_roles: dict[str, int] = {}
    for lab, d in roles:
        ns = _ns(d)
        if _name(d) != HUB_ROLE:
            problems.append(f"{lab}: a Role named {_name(d)!r}; the only Role the hub may have is {HUB_ROLE!r}")
            continue
        shape = STATIC_ROLE_SHAPES.get(ns)
        if shape is None:
            problems.append(
                f"{lab}: is in {ns!r}; the hub has a Role only in {sorted(STATIC_ROLE_SHAPES)} "
                f"(and in rpg-k8s-realtime-<game>, rendered by render.py)"
            )
            continue
        seen_roles[ns] = seen_roles.get(ns, 0) + 1
        problems += _role_problems(d, shape, lab)
    for ns in STATIC_ROLE_SHAPES:
        if seen_roles.get(ns, 0) != 1:
            problems.append(f"expected exactly one Role {HUB_ROLE!r} in {ns!r}, got {seen_roles.get(ns, 0)}")

    seen_bindings: dict[str, int] = {}
    for lab, d in bindings:
        ns = _ns(d)
        if _name(d) != HUB_ROLE:
            problems.append(f"{lab}: a RoleBinding named {_name(d)!r}; only {HUB_ROLE!r} may exist")
            continue
        if ns not in STATIC_ROLE_SHAPES:
            problems.append(f"{lab}: is in {ns!r}, expected one of {sorted(STATIC_ROLE_SHAPES)}")
            continue
        seen_bindings[ns] = seen_bindings.get(ns, 0) + 1
        problems += _binding_problems(d, "Role", HUB_ROLE, lab)
    for ns in STATIC_ROLE_SHAPES:
        if seen_bindings.get(ns, 0) != 1:
            problems.append(f"expected exactly one RoleBinding {HUB_ROLE!r} in {ns!r}, got {seen_bindings.get(ns, 0)}")
    return problems


def check_node_manifests(docs: Docs) -> list[str]:
    """The opt-in file: exactly the node ClusterRole and its binding."""
    problems: list[str] = []
    for lab, d in docs:
        if d.get("kind") not in ("ClusterRole", "ClusterRoleBinding"):
            problems.append(f"{lab}: kind {d.get('kind')!r} is not allowed; only the node ClusterRole and its binding")
    cr = [(lab, d) for lab, d in docs if d.get("kind") == "ClusterRole"]
    crb = [(lab, d) for lab, d in docs if d.get("kind") == "ClusterRoleBinding"]
    if len(cr) != 1 or any(_name(d) != NODE_ROLE for _, d in cr):
        problems.append(f"expected exactly one ClusterRole {NODE_ROLE!r}, got {[_name(d) for _, d in cr]}")
    if len(crb) != 1 or any(_name(d) != NODE_ROLE for _, d in crb):
        problems.append(f"expected exactly one ClusterRoleBinding {NODE_ROLE!r}, got {[_name(d) for _, d in crb]}")
    for lab, d in cr:
        if _name(d) == NODE_ROLE:
            problems += _role_problems(d, "node", lab)
    for lab, d in crb:
        if _name(d) == NODE_ROLE:
            problems += _binding_problems(d, "ClusterRole", NODE_ROLE, lab)
    return problems


def check_hub(docs: Docs, optional_docs: Docs) -> list[str]:
    return check_hub_manifests(docs) + check_node_manifests(optional_docs)


# --------------------------------------------------------------------------
# one rendered game
# --------------------------------------------------------------------------


def check_game_hub(manifests: list[dict], game: str, ns: str) -> list[str]:
    """The hub's Role and RoleBinding in the manifests render.py emits for one game."""
    problems: list[str] = []
    tag = f"render[{game}]"
    docs: Docs = [(f"{tag}:{d.get('kind')}/{_name(d)}", d) for d in manifests]
    for lab, d in docs:
        if d.get("kind") in ("ClusterRole", "ClusterRoleBinding"):
            problems.append(f"{lab}: {d['kind']} is not allowed")
    roles = [(lab, d) for lab, d in docs if d.get("kind") == "Role" and _name(d) == HUB_ROLE]
    bindings = [(lab, d) for lab, d in docs if d.get("kind") == "RoleBinding" and _name(d) == HUB_ROLE]
    if len(roles) != 1 or len(bindings) != 1:
        problems.append(
            f"{tag}: expected exactly one Role and one RoleBinding named {HUB_ROLE!r}, "
            f"got {len(roles)} and {len(bindings)}"
        )
    for lab, d in roles:
        if _ns(d) != ns:
            problems.append(f"{lab}: is in {_ns(d)!r}, expected {ns!r}")
        problems += _role_problems(d, "realtime", lab)
    for lab, d in bindings:
        if _ns(d) != ns:
            problems.append(f"{lab}: is in {_ns(d)!r}, expected {ns!r}")
        problems += _binding_problems(d, "Role", HUB_ROLE, lab)
    for lab, d in docs:
        if d.get("kind") == "RoleBinding" and _name(d) != HUB_ROLE and names_the_hub(d):
            problems.append(f"{lab}: binds the hub's ServiceAccount under another name; only {HUB_ROLE!r} may")
    return problems


def bindings_problems(items, scope: str, namespace: str = "", node: str = "absent") -> list[str]:
    """Problems in the live RoleBindings of one namespace (scope "namespaced") or in
    every live ClusterRoleBinding (scope "cluster"); [] = none.

    Reads subjects and roleRef, never only names: a binding called `studio-hub`
    that points at cluster-admin is a problem, and so is one called anything else
    that lists the hub's ServiceAccount.
    """
    problems: list[str] = []
    kind = "RoleBinding" if scope == "namespaced" else "ClusterRoleBinding"
    where = f"{namespace}/" if scope == "namespaced" else ""
    want_name = HUB_ROLE if scope == "namespaced" else NODE_ROLE
    role_kind = "Role" if scope == "namespaced" else "ClusterRole"
    items = [d for d in items if isinstance(d, dict)]
    mine = [d for d in items if _name(d) == want_name]
    if scope == "namespaced" or node == "present":
        if not mine:
            problems.append(f"missing: {kind} {where}{want_name}")
    elif mine:
        problems.append(
            f"foreign: {kind} {want_name} exists but the ClusterRole {NODE_ROLE} does not; "
            f"it would bind the ServiceAccount the moment a role of that name appears"
        )
    for d in mine:
        if scope == "namespaced" or node == "present":
            problems += _binding_problems(d, role_kind, want_name, f"binding: {kind} {where}{want_name}")
    for d in items:
        if _name(d) != want_name and names_the_hub(d):
            ref = d.get("roleRef") or {}
            problems.append(
                f"foreign: {kind} {where}{_name(d)} names ServiceAccount {HUB_NAMESPACE}/{HUB_ACCOUNT} "
                f"(roleRef {ref.get('kind')}/{ref.get('name')}); only {want_name!r} may"
            )
    return problems


def outside_namespaces_problems(items, expected) -> list[str]:
    """A RoleBinding naming the hub in a namespace that is NOT in `expected` (the list
    `kubectl get rolebindings -A` is read against). The expected namespaces are checked
    in full by bindings_problems; here only the others are looked at, and any binding
    that names the hub there is foreign, whatever it is called."""
    expected = set(expected)
    problems: list[str] = []
    for d in (d for d in items if isinstance(d, dict)):
        ns = _ns(d)
        if ns in expected or not names_the_hub(d):
            continue
        ref = d.get("roleRef") or {}
        problems.append(
            f"foreign: RoleBinding {ns}/{_name(d)} names ServiceAccount {HUB_NAMESPACE}/{HUB_ACCOUNT} "
            f"in a namespace the hub is not meant to have (roleRef {ref.get('kind')}/{ref.get('name')}); "
            f"expected namespaces are {', '.join(sorted(expected))}"
        )
    return problems


def load_renderer():
    spec = importlib.util.spec_from_file_location("rpg_mmo_games_render", GAME_RENDERER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)  # type: ignore[union-attr]
    return module


# --------------------------------------------------------------------------
# CLI
# --------------------------------------------------------------------------


def _compare_main(shape: str, stdin) -> int:
    raw = stdin.read()
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        print(f"cannot read JSON from stdin: {exc}", file=sys.stderr)
        return 2
    if isinstance(data, dict) and "rules" in data:
        rules = data["rules"] if data["rules"] is not None else []
        extra: list[str] = []
        if data.get("aggregationRule"):
            extra.append("invalid: carries an aggregationRule; the rules must be written out")
    elif isinstance(data, list):
        rules, extra = data, []
    elif isinstance(data, dict) and data.get("kind") in ("Role", "ClusterRole"):
        rules, extra = [], []  # an object with no `rules` key is a Role granting nothing
    else:
        print("stdin is neither a Role/ClusterRole object nor a list of rules", file=sys.stderr)
        return 2
    lines = extra + compare_rules(rules, shape)
    for line in lines:
        print(line)
    return 1 if lines else 0


def _bindings_main(args, stdin) -> int:
    raw = stdin.read()
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        print(f"cannot read JSON from stdin: {exc}", file=sys.stderr)
        return 2
    items = data.get("items") if isinstance(data, dict) else data
    if not isinstance(items, list):
        print("stdin is neither a list object with items nor a bare list of bindings", file=sys.stderr)
        return 2
    if args.scope == "namespaced" and not args.namespace:
        print("--scope namespaced needs --namespace", file=sys.stderr)
        return 2
    if args.scope == "all-namespaces":
        if not args.expected:
            print("--scope all-namespaces needs --expected", file=sys.stderr)
            return 2
        lines = outside_namespaces_problems(items, [n for n in args.expected.split(",") if n])
    else:
        lines = bindings_problems(items, args.scope, args.namespace or "", args.node)
    for line in lines:
        print(line)
    return 1 if lines else 0


def _policy_main(kind: str, stdin) -> int:
    raw = stdin.read()
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        print(f"cannot read JSON from stdin: {exc}", file=sys.stderr)
        return 2
    if not isinstance(data, dict) or data.get("kind") != (POLICY_KIND if kind == "policy" else POLICY_BINDING_KIND):
        print(f"stdin is not a {POLICY_KIND if kind == 'policy' else POLICY_BINDING_KIND} object", file=sys.stderr)
        return 2
    lines = policy_problems(data, kind)
    for line in lines:
        print(line)
    return 1 if lines else 0


def _secrets_main(stdin) -> int:
    lines = secrets_problems(stdin.read())
    for line in lines:
        print(line)
    return 1 if lines else 0


def main(argv: list[str] | None = None, stdin=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--dir", type=pathlib.Path, default=HUB_DIR,
                        help="a k8s/hub-shaped directory (its optional/ subdirectory is read too)")
    parser.add_argument("--no-game", action="store_true", help="skip rendering a sample game")
    sub = parser.add_subparsers(dest="command")
    cmp_parser = sub.add_parser("compare", help="compare a Role/ClusterRole (JSON on stdin) with an allow-list")
    cmp_parser.add_argument("--shape", required=True, choices=sorted(SHAPES))
    bind_parser = sub.add_parser("bindings", help="check live RoleBindings/ClusterRoleBindings (JSON on stdin)")
    bind_parser.add_argument("--scope", required=True, choices=("namespaced", "cluster", "all-namespaces"))
    bind_parser.add_argument("--expected", help="scope all-namespaces: comma-separated namespaces that are checked elsewhere")
    bind_parser.add_argument("--namespace", help="the namespace the list was read from (scope namespaced)")
    bind_parser.add_argument("--node", choices=("present", "absent"), default="absent",
                             help="scope cluster: whether the node ClusterRole exists (its binding must then exist, else not)")
    pol_parser = sub.add_parser("policy", help="compare a live ValidatingAdmissionPolicy / binding (JSON on stdin)")
    pol_parser.add_argument("--kind", required=True, choices=("policy", "binding"))
    sub.add_parser("secrets", help="check name<TAB>type<TAB>sa lines of the hub namespace (stdin)")
    args = parser.parse_args(argv)

    if args.command == "policy":
        return _policy_main(args.kind, stdin if stdin is not None else sys.stdin)
    if args.command == "secrets":
        return _secrets_main(stdin if stdin is not None else sys.stdin)
    if args.command == "bindings":
        return _bindings_main(args, stdin if stdin is not None else sys.stdin)
    if args.command == "compare":
        return _compare_main(args.shape, stdin if stdin is not None else sys.stdin)

    if yaml is None:
        sys.exit("PyYAML required: pip install pyyaml")
    docs, optional, problems = load_hub(args.dir)
    if problems:
        for p in problems:
            print(f"[FAIL] {p}")
        return 2
    problems = check_hub(docs, optional)
    for p in problems:
        print(f"[FAIL] {p}")
    if not problems:
        print(f"[ ok ] {args.dir}: {len(docs)} + {len(optional)} optional document(s), hub contract holds")
    failures = len(problems)
    if not args.no_game:
        renderer = load_renderer()
        for game in ("ember", "g" * 24):
            rendered = renderer.render(game, nakama_tls=False)
            ns = renderer.game_namespace(renderer.DEFAULT_STACK_NAMESPACE, game)
            game_problems = check_game_hub(rendered["manifests"], game, ns)
            for p in game_problems:
                print(f"[FAIL] {p}")
            failures += len(game_problems)
            if not game_problems:
                print(f"[ ok ] render[{game}]: hub Role + RoleBinding, REALTIME shape")
    print(f"\n{failures} failure(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
