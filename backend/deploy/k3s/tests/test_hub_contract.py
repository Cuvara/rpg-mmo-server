"""Offline tests of the Studio hub contract (k3s/hub_contract.py).

PyYAML only: no jsonschema, no Agones download, no cluster. They show the contract
holding on the committed k8s/hub (and its opt-in node file) and REJECTING each
mutation it exists to catch (an extra verb, an extra resource, a wildcard, a
secrets rule, a missing ServiceAccount, a ClusterRole in the default directory,
the data namespace gaining an Agones rule or a statefulsets patch, ...):

    python -m unittest discover -s backend/deploy/k3s/tests -p "test_*.py"
"""

import contextlib
import copy
import importlib.util
import io
import json
import pathlib
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parent
K3S = HERE.parent
spec = importlib.util.spec_from_file_location("hub_contract", K3S / "hub_contract.py")
hc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(hc)


def committed():
    docs, optional, problems = hc.load_hub(hc.HUB_DIR)
    assert not problems, problems
    return docs, optional


def find(docs, kind, name=None, ns=None):
    out = [d for _, d in docs if d.get("kind") == kind and (name is None or d["metadata"]["name"] == name)
           and (ns is None or d["metadata"].get("namespace") == ns)]
    assert out, (kind, name, ns)
    return out[0]


def rule(group, resource, verb):
    return {"apiGroups": [group], "resources": [resource], "verbs": [verb]}


def pair(ns, rules=None):
    """A Role + RoleBinding for the hub, as render.py would emit them into a game namespace."""
    meta = {"name": "studio-hub", "namespace": ns}
    return [
        {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "Role", "metadata": dict(meta),
         "rules": rules if rules is not None else [
             {"apiGroups": [g], "resources": list(r), "verbs": list(v)} for g, r, v in hc.REALTIME_RULES]},
        {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "RoleBinding", "metadata": dict(meta),
         "subjects": copy.deepcopy(hc.WANT_SUBJECTS),
         "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "Role", "name": "studio-hub"}},
    ]


class AllowList(unittest.TestCase):
    """The allow-list itself: what the design says, so a silent edit shows here."""

    def test_data_is_realtime_without_the_agones_groups(self):
        agones = {t for t in hc.SHAPES["realtime"] if t[0].endswith("agones.dev")}
        self.assertTrue(agones)
        self.assertEqual(hc.SHAPES["data"], hc.SHAPES["realtime"] - agones)

    def test_data_writes_are_delete_pods_and_patch_deployments_only(self):
        writes = {t for t in hc.SHAPES["data"] if t[2] not in ("get", "list", "watch")}
        self.assertEqual(writes, {("", "pods", "delete"), ("apps", "deployments", "patch")})

    def test_nothing_grants_secrets_configmaps_exec_or_a_wildcard(self):
        for shape, triples in hc.SHAPES.items():
            for group, resource, verb in triples:
                with self.subTest(shape=shape, triple=(group, resource, verb)):
                    self.assertNotIn(resource, ("secrets", "configmaps", "pods/exec", "pods/attach", "pods/portforward"))
                    self.assertNotIn("*", (group, resource, verb))

    def test_fleet_scale_is_the_subresource_not_patch_fleets(self):
        self.assertIn(("agones.dev", "fleets/scale", "patch"), hc.SHAPES["realtime"])
        self.assertNotIn(("agones.dev", "fleets", "patch"), hc.SHAPES["realtime"])
        self.assertNotIn(("agones.dev", "fleets", "update"), hc.SHAPES["realtime"])

    def test_no_statefulset_write(self):
        self.assertFalse({t for t in hc.SHAPES["realtime"] if t[1] == "statefulsets" and t[2] not in ("get", "list", "watch")})

    def test_node_shape_is_the_design_table(self):
        self.assertEqual(hc.SHAPES["node"], frozenset({
            ("", "nodes", "get"), ("", "nodes", "list"), ("", "nodes", "watch"), ("", "nodes", "patch"),
            ("", "pods", "list"), ("", "pods/eviction", "create")}))


class CommittedManifests(unittest.TestCase):
    def test_contract_holds(self):
        docs, optional = committed()
        self.assertEqual(hc.check_hub(docs, optional), [])

    def test_the_default_directory_has_no_cluster_scoped_object(self):
        docs, _ = committed()
        self.assertEqual([d["kind"] for _, d in docs if d["kind"].startswith("Cluster")], [])

    def test_the_default_glob_never_sees_the_optional_file(self):
        docs, optional = committed()
        self.assertTrue(all("node-access" not in lab for lab, _ in docs))
        self.assertEqual(sorted(d["kind"] for _, d in optional), ["ClusterRole", "ClusterRoleBinding"])

    def test_roles_are_only_in_realtime_and_data(self):
        docs, _ = committed()
        self.assertEqual(sorted(d["metadata"]["namespace"] for _, d in docs if d["kind"] == "Role"),
                         ["rpg-k8s-data", "rpg-k8s-realtime"])

    def test_no_secret_value_or_token_in_the_directory(self):
        for f in hc.HUB_DIR.rglob("*.yaml"):
            text = f.read_text(encoding="utf-8")
            for needle in ("eyJ", "BEGIN CERTIFICATE", "kind: Secret", "kind: ConfigMap", "token:"):
                self.assertNotIn(needle, text, f"{f.name} contains {needle!r}")


class Mutations(unittest.TestCase):
    """Every case starts from the committed manifests and breaks exactly one rule."""

    def mutate(self, fn):
        docs, optional = copy.deepcopy(committed())
        fn(docs, optional)
        return hc.check_hub(docs, optional)

    def assertRejected(self, fn, needle):
        problems = self.mutate(fn)
        self.assertTrue(problems, "the mutation was accepted")
        self.assertTrue(any(needle in p for p in problems), f"{needle!r} not in {problems}")

    # ---- extra grants in the namespaced Roles ----
    def test_extra_verb_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"][0]["verbs"].append("patch")
        self.assertRejected(m, "extra: core/pods patch")

    def test_extra_verb_on_the_scale_subresource_is_rejected(self):
        def m(docs, opt):
            for r in find(docs, "Role", ns="rpg-k8s-realtime")["rules"]:
                if r["resources"] == ["fleets/scale"]:
                    r["verbs"].append("create")
        self.assertRejected(m, "extra: agones.dev/fleets/scale create")

    def test_extra_resource_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"].append(rule("", "configmaps", "get"))
        self.assertRejected(m, "extra: core/configmaps get")

    def test_a_secrets_rule_is_rejected(self):
        for ns in ("rpg-k8s-realtime", "rpg-k8s-data"):
            with self.subTest(ns=ns):
                def m(docs, opt, ns=ns):
                    find(docs, "Role", ns=ns)["rules"].append(rule("", "secrets", "get"))
                self.assertRejected(m, "extra: core/secrets get")

    def test_patch_fleets_instead_of_the_scale_subresource_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"].append(rule("agones.dev", "fleets", "patch"))
        self.assertRejected(m, "extra: agones.dev/fleets patch")

    def test_a_pod_exec_rule_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"].append(rule("", "pods/exec", "create"))
        self.assertRejected(m, "extra: core/pods/exec create")

    def test_a_statefulsets_patch_in_realtime_is_rejected(self):
        def m(docs, opt):
            for r in find(docs, "Role", ns="rpg-k8s-realtime")["rules"]:
                if "statefulsets" in r["resources"]:
                    r["verbs"].append("patch")
        self.assertRejected(m, "extra: apps/statefulsets patch")

    def test_a_missing_triple_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"][0]["verbs"].remove("delete")
        self.assertRejected(m, "missing: core/pods delete")

    def test_a_dropped_rule_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-data")["rules"].pop()
        self.assertRejected(m, "missing: apps/")

    def test_wildcard_resource_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"][0]["resources"] = ["*"]
        self.assertRejected(m, "wildcard")

    def test_wildcard_verb_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-data")["rules"][0]["verbs"] = ["*"]
        self.assertRejected(m, "wildcard")

    def test_wildcard_group_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-data")["rules"].append(
                {"apiGroups": ["*"], "resources": ["pods"], "verbs": ["get"]})
        self.assertRejected(m, "wildcard")

    def test_resource_names_are_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"][0]["resourceNames"] = ["x"]
        self.assertRejected(m, "resourceNames")

    def test_non_resource_urls_are_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"].append({"nonResourceURLs": ["/healthz"], "verbs": ["get"]})
        self.assertRejected(m, "nonResourceURLs")

    def test_rule_order_and_grouping_do_not_matter(self):
        # Equal triples in another shape are NOT a mutation: the check reads rules, not layout.
        docs, optional = copy.deepcopy(committed())
        role = find(docs, "Role", ns="rpg-k8s-data")
        role["rules"] = [rule(g, r, v) for g, rs, vs in hc.DATA_RULES for r in rs for v in vs][::-1]
        self.assertEqual(hc.check_hub(docs, optional), [])

    # ---- the data namespace ----
    def test_data_gaining_an_agones_rule_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-data")["rules"].append(rule("agones.dev", "fleets", "get"))
        self.assertRejected(m, "extra: agones.dev/fleets get")

    def test_data_gaining_the_allocation_rule_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-data")["rules"].append(rule("allocation.agones.dev", "gameserverallocations", "create"))
        self.assertRejected(m, "extra: allocation.agones.dev/gameserverallocations create")

    def test_data_gaining_a_statefulsets_patch_is_rejected(self):
        def m(docs, opt):
            for r in find(docs, "Role", ns="rpg-k8s-data")["rules"]:
                if "statefulsets" in r["resources"]:
                    r["verbs"].append("patch")
        self.assertRejected(m, "extra: apps/statefulsets patch")

    def test_data_gaining_a_pod_create_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-data")["rules"][0]["verbs"].append("create")
        self.assertRejected(m, "extra: core/pods create")

    def test_data_given_the_realtime_shape_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-data")["rules"] = [
                {"apiGroups": [g], "resources": list(r), "verbs": list(v)} for g, r, v in hc.REALTIME_RULES]
        self.assertRejected(m, "extra: agones.dev/")

    # ---- cluster scope ----
    def test_cluster_role_in_the_default_directory_is_rejected(self):
        def m(docs, opt):
            docs.append(("x", {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRole",
                               "metadata": {"name": "studio-hub"},
                               "rules": [rule("", "pods", "get")]}))
        self.assertRejected(m, "ClusterRole is not allowed here")

    def test_cluster_role_binding_in_the_default_directory_is_rejected(self):
        def m(docs, opt):
            docs.append(("x", {"kind": "ClusterRoleBinding", "metadata": {"name": "studio-hub-nodes"}}))
        self.assertRejected(m, "ClusterRoleBinding is not allowed here")

    def test_the_node_file_moved_into_the_default_directory_is_rejected(self):
        def m(docs, opt):
            docs.extend(opt)
            opt.clear()
        self.assertRejected(m, "not allowed here")

    def test_a_secret_in_the_default_directory_is_rejected(self):
        def m(docs, opt):
            docs.append(("x", {"kind": "Secret", "metadata": {"name": "studio-hub-token", "namespace": "rpg-k8s-hub"}}))
        self.assertRejected(m, "kind 'Secret' is not allowed")

    def test_a_configmap_in_the_default_directory_is_rejected(self):
        def m(docs, opt):
            docs.append(("x", {"kind": "ConfigMap", "metadata": {"name": "x", "namespace": "rpg-k8s-hub"}}))
        self.assertRejected(m, "kind 'ConfigMap' is not allowed")

    def test_a_workload_in_the_default_directory_is_rejected(self):
        def m(docs, opt):
            docs.append(("x", {"kind": "Deployment", "metadata": {"name": "x", "namespace": "rpg-k8s-hub"}}))
        self.assertRejected(m, "kind 'Deployment' is not allowed")

    # ---- node file ----
    def test_node_file_gaining_a_verb_is_rejected(self):
        def m(docs, opt):
            find(opt, "ClusterRole")["rules"][0]["verbs"].append("delete")
        self.assertRejected(m, "extra: core/nodes delete")

    def test_node_file_gaining_a_resource_is_rejected(self):
        def m(docs, opt):
            find(opt, "ClusterRole")["rules"].append(rule("", "secrets", "get"))
        self.assertRejected(m, "extra: core/secrets get")

    def test_node_file_gaining_a_cluster_wide_pod_delete_is_rejected(self):
        def m(docs, opt):
            for r in find(opt, "ClusterRole")["rules"]:
                if r["resources"] == ["pods"]:
                    r["verbs"].append("delete")
        self.assertRejected(m, "extra: core/pods delete")

    def test_node_file_losing_a_verb_is_rejected(self):
        def m(docs, opt):
            find(opt, "ClusterRole")["rules"][0]["verbs"].remove("patch")
        self.assertRejected(m, "missing: core/nodes patch")

    def test_node_file_with_an_aggregation_rule_is_rejected(self):
        def m(docs, opt):
            find(opt, "ClusterRole")["aggregationRule"] = {"clusterRoleSelectors": [{"matchLabels": {"a": "b"}}]}
        self.assertRejected(m, "aggregationRule")

    def test_node_file_with_a_wildcard_is_rejected(self):
        def m(docs, opt):
            find(opt, "ClusterRole")["rules"][0]["resources"] = ["*"]
        self.assertRejected(m, "wildcard")

    def test_node_binding_to_another_subject_is_rejected(self):
        def m(docs, opt):
            find(opt, "ClusterRoleBinding")["subjects"][0]["name"] = "default"
        self.assertRejected(m, "only subject allowed")

    def test_node_binding_to_a_cluster_admin_roleref_is_rejected(self):
        def m(docs, opt):
            find(opt, "ClusterRoleBinding")["roleRef"]["name"] = "cluster-admin"
        self.assertRejected(m, "roleRef")

    def test_node_file_with_a_second_object_is_rejected(self):
        def m(docs, opt):
            opt.append(("x", {"kind": "Namespace", "metadata": {"name": "x"}}))
        self.assertRejected(m, "kind 'Namespace' is not allowed")

    def test_missing_node_file_is_an_input_problem(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            for f in hc.HUB_DIR.glob("*.yaml"):
                (tmp / f.name).write_text(f.read_text(encoding="utf-8"), encoding="utf-8")
            _, _, problems = hc.load_hub(tmp)
        self.assertTrue(any("no *.yaml files" in p for p in problems), problems)

    # ---- ServiceAccount and namespace ----
    def test_missing_service_account_is_rejected(self):
        def m(docs, opt):
            docs[:] = [(lab, d) for lab, d in docs if d["kind"] != "ServiceAccount"]
        self.assertRejected(m, "exactly one ServiceAccount")

    def test_service_account_in_another_namespace_is_rejected(self):
        def m(docs, opt):
            find(docs, "ServiceAccount")["metadata"]["namespace"] = "rpg-k8s-realtime"
        self.assertRejected(m, "exactly one ServiceAccount")

    def test_second_service_account_is_rejected(self):
        def m(docs, opt):
            docs.append(("x", {"kind": "ServiceAccount", "metadata": {"name": "other", "namespace": "rpg-k8s-hub"},
                               "automountServiceAccountToken": False}))
        self.assertRejected(m, "a ServiceAccount other than")

    def test_automount_true_is_rejected(self):
        def m(docs, opt):
            find(docs, "ServiceAccount")["automountServiceAccountToken"] = True
        self.assertRejected(m, "automountServiceAccountToken must be explicitly false")

    def test_automount_left_unset_is_rejected(self):
        def m(docs, opt):
            del find(docs, "ServiceAccount")["automountServiceAccountToken"]
        self.assertRejected(m, "automountServiceAccountToken must be explicitly false")

    def test_a_secret_listed_on_the_service_account_is_rejected(self):
        def m(docs, opt):
            find(docs, "ServiceAccount")["secrets"] = [{"name": "studio-hub-token"}]
        self.assertRejected(m, "lists secrets")

    def test_missing_namespace_is_rejected(self):
        def m(docs, opt):
            docs[:] = [(lab, d) for lab, d in docs if d["kind"] != "Namespace"]
        self.assertRejected(m, "exactly one Namespace")

    def test_wrong_namespace_name_is_rejected(self):
        def m(docs, opt):
            find(docs, "Namespace")["metadata"]["name"] = "rpg-k8s-realtime"
        self.assertRejected(m, "exactly one Namespace")

    # ---- where Roles may live ----
    def test_a_role_in_the_hub_namespace_is_rejected(self):
        def m(docs, opt):
            docs.extend(("x", d) for d in pair("rpg-k8s-hub"))
        self.assertRejected(m, "is in 'rpg-k8s-hub'")

    def test_a_role_in_kube_system_is_rejected(self):
        def m(docs, opt):
            docs.extend(("x", d) for d in pair("kube-system"))
        self.assertRejected(m, "is in 'kube-system'")

    def test_a_role_in_the_logging_namespace_is_rejected(self):
        def m(docs, opt):
            docs.extend(("x", d) for d in pair("rpg-k8s-logging"))
        self.assertRejected(m, "is in 'rpg-k8s-logging'")

    def test_a_role_in_agones_system_is_rejected(self):
        def m(docs, opt):
            docs.extend(("x", d) for d in pair("agones-system"))
        self.assertRejected(m, "is in 'agones-system'")

    def test_a_second_role_in_a_namespace_is_rejected(self):
        def m(docs, opt):
            docs.append(("x", copy.deepcopy(find(docs, "Role", ns="rpg-k8s-data"))))
        self.assertRejected(m, "exactly one Role 'studio-hub' in 'rpg-k8s-data', got 2")

    def test_missing_role_is_rejected(self):
        def m(docs, opt):
            docs[:] = [(lab, d) for lab, d in docs if not (d["kind"] == "Role" and d["metadata"]["namespace"] == "rpg-k8s-data")]
        self.assertRejected(m, "exactly one Role 'studio-hub' in 'rpg-k8s-data', got 0")

    def test_another_role_name_is_rejected(self):
        def m(docs, opt):
            find(docs, "Role", ns="rpg-k8s-data")["metadata"]["name"] = "something-else"
        self.assertRejected(m, "only Role the hub may have")

    # ---- bindings ----
    def test_binding_to_another_subject_is_rejected(self):
        def m(docs, opt):
            find(docs, "RoleBinding", ns="rpg-k8s-data")["subjects"][0]["name"] = "default"
        self.assertRejected(m, "only subject allowed")

    def test_binding_to_another_subject_namespace_is_rejected(self):
        def m(docs, opt):
            find(docs, "RoleBinding", ns="rpg-k8s-data")["subjects"][0]["namespace"] = "rpg-k8s-realtime"
        self.assertRejected(m, "only subject allowed")

    def test_binding_with_a_second_subject_is_rejected(self):
        def m(docs, opt):
            find(docs, "RoleBinding", ns="rpg-k8s-realtime")["subjects"].append(
                {"kind": "Group", "name": "system:authenticated", "apiGroup": "rbac.authorization.k8s.io"})
        self.assertRejected(m, "only subject allowed")

    def test_binding_to_a_cluster_role_is_rejected(self):
        def m(docs, opt):
            find(docs, "RoleBinding", ns="rpg-k8s-data")["roleRef"]["kind"] = "ClusterRole"
        self.assertRejected(m, "roleRef")

    def test_binding_to_another_role_is_rejected(self):
        def m(docs, opt):
            find(docs, "RoleBinding", ns="rpg-k8s-data")["roleRef"]["name"] = "admin"
        self.assertRejected(m, "roleRef")

    def test_missing_binding_is_rejected(self):
        def m(docs, opt):
            docs[:] = [(lab, d) for lab, d in docs
                       if not (d["kind"] == "RoleBinding" and d["metadata"]["namespace"] == "rpg-k8s-data")]
        self.assertRejected(m, "exactly one RoleBinding 'studio-hub' in 'rpg-k8s-data', got 0")

    def test_a_second_binding_name_is_rejected(self):
        def m(docs, opt):
            extra = copy.deepcopy(find(docs, "RoleBinding", ns="rpg-k8s-data"))
            extra["metadata"]["name"] = "studio-hub-extra"
            extra["roleRef"]["name"] = "admin"
            docs.append(("x", extra))
        self.assertRejected(m, "only 'studio-hub' may exist")


class RenderedGame(unittest.TestCase):
    """check_game_hub on the Role + RoleBinding render.py must emit for a game namespace."""

    NS = "rpg-k8s-realtime-ember"

    def check(self, fn=None, ns=NS):
        manifests = copy.deepcopy(pair(ns))
        if fn:
            fn(manifests)
        return hc.check_game_hub(manifests, "ember", self.NS)

    def assertRejected(self, fn, needle, **kw):
        problems = self.check(fn, **kw)
        self.assertTrue(any(needle in p for p in problems), f"{needle!r} not in {problems}")

    def test_the_expected_pair_passes(self):
        self.assertEqual(self.check(), [])

    def test_the_same_pair_in_the_static_realtime_namespace_has_the_same_rules(self):
        docs, _ = committed()
        static = find(docs, "Role", ns="rpg-k8s-realtime")
        self.assertEqual(hc.compare_rules(static["rules"], "realtime"), [])
        self.assertEqual(hc.rules_to_triples(static["rules"])[0], hc.rules_to_triples(pair(self.NS)[0]["rules"])[0])

    def test_extra_verb_is_rejected(self):
        self.assertRejected(lambda ms: ms[0]["rules"][0]["verbs"].append("patch"), "extra: core/pods patch")

    def test_extra_resource_is_rejected(self):
        self.assertRejected(lambda ms: ms[0]["rules"].append(rule("", "secrets", "get")), "extra: core/secrets get")

    def test_wildcard_is_rejected(self):
        self.assertRejected(lambda ms: ms[0]["rules"][0].update(resources=["*"]), "wildcard")

    def test_missing_role_is_rejected(self):
        self.assertRejected(lambda ms: ms.pop(0), "exactly one Role and one RoleBinding")

    def test_missing_binding_is_rejected(self):
        self.assertRejected(lambda ms: ms.pop(1), "exactly one Role and one RoleBinding")

    def test_role_in_another_namespace_is_rejected(self):
        self.assertRejected(lambda ms: None, "expected 'rpg-k8s-realtime-ember'", ns="rpg-k8s-realtime")

    def test_binding_to_another_subject_is_rejected(self):
        self.assertRejected(lambda ms: ms[1]["subjects"][0].update(name="rpg-gateway"), "only subject allowed")

    def test_a_cluster_role_in_the_render_is_rejected(self):
        self.assertRejected(lambda ms: ms.append({"kind": "ClusterRole", "metadata": {"name": "x"}}), "ClusterRole")

    def test_a_second_binding_of_the_hub_is_rejected(self):
        def m(ms):
            extra = copy.deepcopy(ms[1])
            extra["metadata"]["name"] = "hub-admin"
            ms.append(extra)
        self.assertRejected(m, "under another name")

    def test_a_binding_naming_the_hub_as_user_or_group_is_rejected(self):
        for subject in (
            {"kind": "User", "name": "system:serviceaccount:rpg-k8s-hub:studio-hub"},
            {"kind": "Group", "name": "system:serviceaccounts:rpg-k8s-hub"},
        ):
            with self.subTest(subject=subject):
                def m(ms, subject=subject):
                    ms.append({"kind": "RoleBinding", "metadata": {"name": "sneaky", "namespace": self.NS},
                               "subjects": [subject],
                               "roleRef": {"kind": "ClusterRole", "name": "cluster-admin"}})
                self.assertRejected(m, "under another name")

    def test_a_binding_of_a_broad_group_is_not_a_binding_naming_the_hub(self):
        # A group every ServiceAccount is in (or every user) does not name the hub; see names_the_hub.
        for name in hc.BROAD_GROUP_NAMES:
            with self.subTest(group=name):
                def m(ms, name=name):
                    ms.append({"kind": "RoleBinding", "metadata": {"name": "everyone", "namespace": self.NS},
                               "subjects": [{"kind": "Group", "name": name}],
                               "roleRef": {"kind": "ClusterRole", "name": "view"}})
                self.assertEqual(self.check(m), [])

    def test_the_gateway_allocate_binding_is_not_confused_with_the_hub(self):
        def m(ms):
            ms.append({"kind": "RoleBinding", "metadata": {"name": "rpg-gateway-allocate", "namespace": self.NS},
                       "subjects": [{"kind": "ServiceAccount", "name": "rpg-gateway", "namespace": "rpg-k8s-realtime"}],
                       "roleRef": {"kind": "Role", "name": "rpg-gateway-allocate"}})
        self.assertEqual(self.check(m), [])

    def test_a_real_render_carries_the_pair_and_satisfies_the_contract(self):
        renderer = hc.load_renderer()
        for game in ("ember", "g" * 24):
            with self.subTest(game=game):
                manifests = renderer.render(game, nakama_tls=False)["manifests"]
                ns = renderer.game_namespace(renderer.DEFAULT_STACK_NAMESPACE, game)
                emitted = [d for d in manifests if d.get("metadata", {}).get("name") == "studio-hub"]
                self.assertEqual(sorted(d["kind"] for d in emitted), ["Role", "RoleBinding"])
                self.assertEqual(hc.check_game_hub(manifests, game, ns), [])
                self.assertFalse([d for d in manifests if d["kind"] in ("ClusterRole", "ClusterRoleBinding")])

    def test_a_mutated_real_render_is_rejected(self):
        # The same mutations as above, but on what render.py really emits.
        renderer = hc.load_renderer()
        ns = renderer.game_namespace(renderer.DEFAULT_STACK_NAMESPACE, "ember")

        def mutated(fn):
            manifests = copy.deepcopy(renderer.render("ember", nakama_tls=False)["manifests"])
            role = next(d for d in manifests if d["kind"] == "Role" and d["metadata"]["name"] == "studio-hub")
            binding = next(d for d in manifests if d["kind"] == "RoleBinding" and d["metadata"]["name"] == "studio-hub")
            fn(role, binding, manifests)
            return hc.check_game_hub(manifests, "ember", ns)

        cases = {
            "extra: agones.dev/fleets patch": lambda r, b, ms: r["rules"].append(rule("agones.dev", "fleets", "patch")),
            "extra: core/pods create": lambda r, b, ms: r["rules"][0]["verbs"].append("create"),
            "extra: core/secrets get": lambda r, b, ms: r["rules"].append(rule("", "secrets", "get")),
            "missing: core/pods delete": lambda r, b, ms: r["rules"][0]["verbs"].remove("delete"),
            "wildcard": lambda r, b, ms: r["rules"][0].update(verbs=["*"]),
            "only subject allowed": lambda r, b, ms: b["subjects"][0].update(namespace="rpg-k8s-realtime"),
            "ClusterRole": lambda r, b, ms: ms.append({"kind": "ClusterRole", "metadata": {"name": "x"}}),
        }
        for needle, fn in cases.items():
            with self.subTest(needle=needle):
                problems = mutated(fn)
                self.assertTrue(any(needle in p for p in problems), f"{needle!r} not in {problems}")


class Compare(unittest.TestCase):
    """`hub_contract.py compare --shape S` reading JSON on stdin (the verify check's entry point)."""

    def run_compare(self, shape, payload):
        out, err = io.StringIO(), io.StringIO()
        stdin = io.StringIO(payload if isinstance(payload, str) else json.dumps(payload))
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            rc = hc.main(["compare", "--shape", shape], stdin=stdin)
        return rc, out.getvalue(), err.getvalue()

    def role_json(self, shape):
        rules = {"realtime": hc.REALTIME_RULES, "data": hc.DATA_RULES, "node": hc.NODE_RULES}[shape]
        return {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "Role",
                "metadata": {"name": "studio-hub", "namespace": "x", "uid": "u", "resourceVersion": "1"},
                "rules": [{"apiGroups": [g], "resources": list(r), "verbs": list(v)} for g, r, v in rules]}

    def test_equal_exits_zero_and_prints_nothing(self):
        for shape in ("realtime", "data", "node"):
            with self.subTest(shape=shape):
                rc, out, err = self.run_compare(shape, self.role_json(shape))
                self.assertEqual((rc, out, err), (0, "", ""))

    def test_a_bare_list_of_rules_is_accepted(self):
        rc, out, _ = self.run_compare("data", self.role_json("data")["rules"])
        self.assertEqual((rc, out), (0, ""))

    def test_live_rules_split_differently_are_equal(self):
        role = self.role_json("data")
        role["rules"] = [rule(g, r, v) for g, rs, vs in hc.DATA_RULES for r in rs for v in vs]
        rc, out, _ = self.run_compare("data", role)
        self.assertEqual((rc, out), (0, ""))

    def test_an_extra_verb_prints_one_line_per_extra_triple(self):
        role = self.role_json("realtime")
        role["rules"].append({"apiGroups": ["apps"], "resources": ["statefulsets"], "verbs": ["patch", "delete"]})
        rc, out, _ = self.run_compare("realtime", role)
        self.assertEqual(rc, 1)
        self.assertEqual(out.splitlines(), ["extra: apps/statefulsets delete", "extra: apps/statefulsets patch"])

    def test_a_missing_triple_prints_a_missing_line(self):
        role = self.role_json("node")
        role["rules"].pop()
        rc, out, _ = self.run_compare("node", role)
        self.assertEqual((rc, out.splitlines()), (1, ["missing: core/pods/eviction create"]))

    def test_the_realtime_role_compared_as_data_reports_the_agones_rows_as_extra(self):
        rc, out, _ = self.run_compare("data", self.role_json("realtime"))
        self.assertEqual(rc, 1)
        lines = out.splitlines()
        self.assertTrue(lines and all(line.startswith("extra: ") for line in lines), lines)
        self.assertIn("extra: agones.dev/fleets/scale patch", lines)

    def test_the_data_role_compared_as_realtime_reports_the_agones_rows_as_missing(self):
        rc, out, _ = self.run_compare("realtime", self.role_json("data"))
        self.assertEqual(rc, 1)
        self.assertTrue(all(line.startswith("missing: ") for line in out.splitlines()), out)

    def test_resource_names_and_wildcards_are_invalid(self):
        role = self.role_json("data")
        role["rules"][0]["resourceNames"] = ["x"]
        role["rules"].append({"apiGroups": [""], "resources": ["*"], "verbs": ["get"]})
        rc, out, _ = self.run_compare("data", role)
        self.assertEqual(rc, 1)
        self.assertIn("invalid: rule 1 carries 'resourceNames'", out)
        self.assertIn("wildcard", out)

    def test_an_aggregation_rule_is_invalid(self):
        role = self.role_json("node")
        role["aggregationRule"] = {"clusterRoleSelectors": []}
        rc, out, _ = self.run_compare("node", role)
        self.assertEqual(rc, 1)
        self.assertIn("aggregationRule", out)

    def test_a_role_with_no_rules_key_is_all_missing(self):
        rc, out, _ = self.run_compare("node", {"kind": "Role", "metadata": {"name": "studio-hub"}})
        self.assertEqual(rc, 1)
        self.assertEqual(len(out.splitlines()), len(hc.SHAPES["node"]))

    def test_a_null_rules_list_is_all_missing(self):
        rc, out, _ = self.run_compare("node", {"kind": "Role", "rules": None})
        self.assertEqual(rc, 1)
        self.assertTrue(all(line.startswith("missing: ") for line in out.splitlines()))

    def test_not_json_is_exit_two(self):
        rc, out, err = self.run_compare("data", "<html>")
        self.assertEqual((rc, out), (2, ""))
        self.assertIn("cannot read JSON", err)

    def test_a_json_that_is_not_a_role_is_exit_two(self):
        rc, _, _ = self.run_compare("data", {"kind": "Pod"})
        self.assertEqual(rc, 2)
        rc, _, _ = self.run_compare("data", "42")
        self.assertEqual(rc, 2)

    def test_an_unknown_shape_is_rejected_by_the_parser(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as ctx:
            hc.main(["compare", "--shape", "admin"], stdin=io.StringIO("[]"))
        self.assertEqual(ctx.exception.code, 2)


class Bindings(unittest.TestCase):
    """`hub_contract.py bindings` reading live RoleBinding / ClusterRoleBinding lists on stdin."""

    SUBJECT = {"kind": "ServiceAccount", "name": "studio-hub", "namespace": "rpg-k8s-hub"}

    def run_bindings(self, payload, *argv):
        out, err = io.StringIO(), io.StringIO()
        stdin = io.StringIO(payload if isinstance(payload, str) else json.dumps(payload))
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            rc = hc.main(["bindings", *argv], stdin=stdin)
        return rc, out.getvalue(), err.getvalue()

    def binding(self, name, ns=None, kind="Role", role=None, subjects=None, binding_kind="RoleBinding"):
        meta = {"name": name}
        if ns:
            meta["namespace"] = ns
        return {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": binding_kind, "metadata": meta,
                "subjects": copy.deepcopy(subjects if subjects is not None else [self.SUBJECT]),
                "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": kind, "name": role or name}}

    def listing(self, *items):
        return {"apiVersion": "v1", "kind": "List", "items": list(items)}

    def node_binding(self, **kw):
        return self.binding("studio-hub-nodes", kind="ClusterRole", binding_kind="ClusterRoleBinding", **kw)

    def test_the_exact_namespaced_binding_is_clean(self):
        unrelated = self.binding("unrelated", "x", subjects=[
            {"kind": "ServiceAccount", "name": "other", "namespace": "x"}])
        rc, out, err = self.run_bindings(
            self.listing(self.binding("studio-hub", "x"), unrelated),
            "--scope", "namespaced", "--namespace", "x")
        self.assertEqual((rc, out, err), (0, "", ""))

    def test_a_missing_namespaced_binding_is_reported(self):
        rc, out, _ = self.run_bindings(self.listing(), "--scope", "namespaced", "--namespace", "x")
        self.assertEqual((rc, out.splitlines()), (1, ["missing: RoleBinding x/studio-hub"]))

    def test_another_subject_or_another_role_is_reported(self):
        wrong_subject = self.binding("studio-hub", "x", subjects=[
            {"kind": "ServiceAccount", "name": "studio-hub", "namespace": "rpg-k8s-realtime"}])
        rc, out, _ = self.run_bindings(self.listing(wrong_subject), "--scope", "namespaced", "--namespace", "x")
        self.assertEqual(rc, 1)
        self.assertIn("binding: RoleBinding x/studio-hub: subjects are", out)
        cluster_admin = self.binding("studio-hub", "x", kind="ClusterRole", role="cluster-admin")
        rc, out, _ = self.run_bindings(self.listing(cluster_admin), "--scope", "namespaced", "--namespace", "x")
        self.assertEqual(rc, 1)
        self.assertIn("roleRef is", out)

    def test_an_extra_subject_on_the_hub_binding_is_reported(self):
        extra = self.binding("studio-hub", "x", subjects=[
            self.SUBJECT, {"kind": "User", "name": "mallory", "apiGroup": "rbac.authorization.k8s.io"}])
        rc, out, _ = self.run_bindings(self.listing(extra), "--scope", "namespaced", "--namespace", "x")
        self.assertEqual(rc, 1)
        self.assertIn("subjects are", out)

    def test_a_foreign_binding_that_names_the_account_is_reported(self):
        foreign = self.binding("edit-for-hub", "x", kind="ClusterRole", role="edit")
        rc, out, _ = self.run_bindings(
            self.listing(self.binding("studio-hub", "x"), foreign), "--scope", "namespaced", "--namespace", "x")
        self.assertEqual(rc, 1)
        self.assertEqual(out.splitlines(), [
            "foreign: RoleBinding x/edit-for-hub names ServiceAccount rpg-k8s-hub/studio-hub "
            "(roleRef ClusterRole/edit); only 'studio-hub' may"])

    def test_a_subject_with_no_namespace_still_names_the_hub(self):
        sloppy = self.binding("sloppy", "x", subjects=[{"kind": "ServiceAccount", "name": "studio-hub"}])
        rc, out, _ = self.run_bindings(
            self.listing(self.binding("studio-hub", "x"), sloppy), "--scope", "namespaced", "--namespace", "x")
        self.assertEqual(rc, 1)
        self.assertIn("foreign: RoleBinding x/sloppy", out)

    # The same identity under its other spellings: User `system:serviceaccount:NS:NAME` and the
    # Group `system:serviceaccounts:NS`. Both contain the hub and nobody else.
    ALIASES = (
        ("user", {"kind": "User", "name": "system:serviceaccount:rpg-k8s-hub:studio-hub",
                  "apiGroup": "rbac.authorization.k8s.io"}),
        ("group-namespace", {"kind": "Group", "name": "system:serviceaccounts:rpg-k8s-hub",
                             "apiGroup": "rbac.authorization.k8s.io"}),
    )

    # Groups that contain every ServiceAccount (or every user). They reach the hub, and everyone
    # else, so a binding to one of them is not a binding that names the hub.
    BROAD = tuple(
        (name, {"kind": "Group", "name": name, "apiGroup": "rbac.authorization.k8s.io"})
        for name in ("system:serviceaccounts", "system:authenticated", "system:unauthenticated"))

    def test_a_rolebinding_naming_the_hub_as_user_or_group_is_reported(self):
        for label, subject in self.ALIASES:
            with self.subTest(alias=label):
                sneaky = self.binding("sneaky", "x", kind="ClusterRole", role="cluster-admin", subjects=[subject])
                rc, out, _ = self.run_bindings(
                    self.listing(self.binding("studio-hub", "x"), sneaky),
                    "--scope", "namespaced", "--namespace", "x")
                self.assertEqual(rc, 1)
                self.assertIn("foreign: RoleBinding x/sneaky names ServiceAccount", out)
                self.assertIn("roleRef ClusterRole/cluster-admin", out)

    def test_a_clusterrolebinding_naming_the_hub_as_user_or_group_is_reported(self):
        for label, subject in self.ALIASES:
            for node, extra in (("absent", []), ("present", [self.node_binding()])):
                with self.subTest(alias=label, node=node):
                    sneaky = self.binding("sneaky", kind="ClusterRole", role="cluster-admin",
                                          binding_kind="ClusterRoleBinding", subjects=[subject])
                    rc, out, _ = self.run_bindings(
                        self.listing(*extra, sneaky), "--scope", "cluster", "--node", node)
                    self.assertEqual(rc, 1)
                    self.assertIn("foreign: ClusterRoleBinding sneaky names ServiceAccount", out)

    def test_the_hub_binding_itself_with_an_alias_subject_is_reported(self):
        # `studio-hub` carrying an extra User/Group subject is a wrong-subjects problem (exact list compare).
        for label, subject in self.ALIASES:
            with self.subTest(alias=label):
                widened = self.binding("studio-hub", "x", subjects=[self.SUBJECT, subject])
                rc, out, _ = self.run_bindings(
                    self.listing(widened), "--scope", "namespaced", "--namespace", "x")
                self.assertEqual(rc, 1)
                self.assertIn("subjects are", out)

    def test_names_the_hub_compares_kind_and_name_exactly(self):
        def names(*subjects):
            return hc.names_the_hub({"subjects": list(subjects)})

        for _, subject in self.ALIASES:
            self.assertTrue(names(subject))
        # Near misses are not the hub.
        for subject in (
            {"kind": "User", "name": "system:serviceaccount:rpg-k8s-realtime:studio-hub"},
            {"kind": "User", "name": "system:serviceaccount:rpg-k8s-hub:other"},
            {"kind": "User", "name": "studio-hub"},
            {"kind": "Group", "name": "system:serviceaccounts:rpg-k8s-realtime"},
            {"kind": "Group", "name": "system:serviceaccount:rpg-k8s-hub:studio-hub"},
            {"kind": "Group", "name": "system:serviceaccounts:rpg-k8s-hub:"},
            {"kind": "ServiceAccount", "name": "system:serviceaccounts", "namespace": "rpg-k8s-hub"},
            {"kind": "Group", "name": "System:ServiceAccounts"},
        ):
            with self.subTest(subject=subject):
                self.assertFalse(names(subject))

    def test_a_broad_group_is_not_the_hub(self):
        # Classified by subject, not by binding name: Kubernetes binds its own defaults to these groups.
        self.assertEqual(hc.BROAD_GROUP_NAMES,
                         ("system:serviceaccounts", "system:authenticated", "system:unauthenticated"))
        for name, subject in self.BROAD:
            with self.subTest(group=name):
                self.assertFalse(hc.names_the_hub({"subjects": [subject]}))

    def test_a_broad_group_next_to_a_subject_that_names_the_hub_is_still_foreign(self):
        # One subject that names the hub is enough, whatever else is listed beside it.
        for _, broad in self.BROAD:
            for label, hub in (("sa", self.SUBJECT), *self.ALIASES):
                with self.subTest(broad=broad["name"], hub=label):
                    mixed = self.binding("mixed", kind="ClusterRole", role="view",
                                         binding_kind="ClusterRoleBinding", subjects=[broad, hub])
                    rc, out, _ = self.run_bindings(self.listing(mixed), "--scope", "cluster", "--node", "absent")
                    self.assertEqual(rc, 1)
                    self.assertIn("foreign: ClusterRoleBinding mixed names ServiceAccount", out)

    # The ClusterRoleBindings of a stock cluster that reach every ServiceAccount or every user. The first is
    # the object the live QA read on k3s v1.35.5 (kubectl get clusterrolebinding -o json, trimmed to what the
    # check reads); the other three are the Kubernetes bootstrap defaults bound to system:authenticated /
    # system:unauthenticated.
    STOCK_DEFAULTS = (
        {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRoleBinding",
         "metadata": {"name": "system:service-account-issuer-discovery",
                      "annotations": {"rbac.authorization.kubernetes.io/autoupdate": "true"},
                      "labels": {"kubernetes.io/bootstrapping": "rbac-defaults"}},
         "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "ClusterRole",
                     "name": "system:service-account-issuer-discovery"},
         "subjects": [{"apiGroup": "rbac.authorization.k8s.io", "kind": "Group", "name": "system:serviceaccounts"}]},
        {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRoleBinding",
         "metadata": {"name": "system:basic-user"},
         "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "ClusterRole", "name": "system:basic-user"},
         "subjects": [{"apiGroup": "rbac.authorization.k8s.io", "kind": "Group", "name": "system:authenticated"}]},
        {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRoleBinding",
         "metadata": {"name": "system:discovery"},
         "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "ClusterRole", "name": "system:discovery"},
         "subjects": [{"apiGroup": "rbac.authorization.k8s.io", "kind": "Group", "name": "system:authenticated"}]},
        {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRoleBinding",
         "metadata": {"name": "system:public-info-viewer"},
         "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "ClusterRole",
                     "name": "system:public-info-viewer"},
         "subjects": [{"apiGroup": "rbac.authorization.k8s.io", "kind": "Group", "name": "system:authenticated"},
                      {"apiGroup": "rbac.authorization.k8s.io", "kind": "Group", "name": "system:unauthenticated"}]},
    )

    def test_the_stock_cluster_default_bindings_are_not_foreign(self):
        # F1 of the live QA: cluster.hub_rbac FAILed on a clean k3s because of the first of these.
        for node, extra in (("absent", []), ("present", [self.node_binding()])):
            with self.subTest(node=node):
                rc, out, err = self.run_bindings(
                    self.listing(*self.STOCK_DEFAULTS, *extra), "--scope", "cluster", "--node", node)
                self.assertEqual((rc, out, err), (0, "", ""))

    def test_beside_the_defaults_only_a_foreign_binding_is_reported(self):
        for default in self.STOCK_DEFAULTS:
            with self.subTest(default=default["metadata"]["name"]):
                self.assertEqual(self.run_bindings(self.listing(default), "--scope", "cluster")[:2], (0, ""))
        admin = self.binding("hub-admin", kind="ClusterRole", role="cluster-admin", binding_kind="ClusterRoleBinding")
        rc, out, _ = self.run_bindings(self.listing(*self.STOCK_DEFAULTS, admin), "--scope", "cluster")
        self.assertEqual(rc, 1)
        self.assertEqual(len(out.splitlines()), 1)
        self.assertIn("foreign: ClusterRoleBinding hub-admin", out)

    def test_a_default_looking_name_does_not_excuse_a_binding_that_names_the_hub(self):
        # The rule reads subjects, never names: a binding called like a default but listing the hub is foreign.
        imposter = copy.deepcopy(self.STOCK_DEFAULTS[0])
        imposter["subjects"].append(self.SUBJECT)
        rc, out, _ = self.run_bindings(self.listing(imposter), "--scope", "cluster")
        self.assertEqual(rc, 1)
        self.assertIn("foreign: ClusterRoleBinding system:service-account-issuer-discovery names ServiceAccount", out)

    def test_a_namespaced_binding_of_a_broad_group_is_not_foreign_either(self):
        for name, subject in self.BROAD:
            with self.subTest(group=name):
                everyone = self.binding("everyone", "x", kind="ClusterRole", role="view", subjects=[subject])
                rc, out, _ = self.run_bindings(
                    self.listing(self.binding("studio-hub", "x"), everyone),
                    "--scope", "namespaced", "--namespace", "x")
                self.assertEqual((rc, out), (0, ""))
                rc, out, _ = self.run_bindings(
                    self.listing(everyone), "--scope", "all-namespaces", "--expected", "rpg-k8s-data")
                self.assertEqual((rc, out), (0, ""))

    def test_the_verify_readme_lists_the_defaults_and_states_the_blind_spot(self):
        text = (hc.DEPLOY_DIR / "k8s" / "verify" / "README.md").read_text(encoding="utf-8")
        for default in self.STOCK_DEFAULTS:
            self.assertIn(default["metadata"]["name"], text)
        for needle in ("every ServiceAccount", "cannot see"):
            self.assertIn(needle, text)

    def test_a_service_account_of_the_same_name_elsewhere_is_not_the_hub(self):
        other = self.binding("elsewhere", "x", subjects=[
            {"kind": "ServiceAccount", "name": "studio-hub", "namespace": "some-other-ns"}])
        rc, out, _ = self.run_bindings(
            self.listing(self.binding("studio-hub", "x"), other), "--scope", "namespaced", "--namespace", "x")
        self.assertEqual((rc, out), (0, ""))

    def test_no_cluster_binding_is_clean_when_the_node_role_is_absent(self):
        unrelated = self.binding("system:node", kind="ClusterRole", binding_kind="ClusterRoleBinding", subjects=[
            {"kind": "Group", "name": "system:nodes", "apiGroup": "rbac.authorization.k8s.io"}])
        rc, out, _ = self.run_bindings(self.listing(unrelated), "--scope", "cluster", "--node", "absent")
        self.assertEqual((rc, out), (0, ""))

    def test_the_exact_node_binding_is_clean_when_the_node_role_is_present(self):
        rc, out, _ = self.run_bindings(self.listing(self.node_binding()), "--scope", "cluster", "--node", "present")
        self.assertEqual((rc, out), (0, ""))

    def test_the_node_binding_missing_while_the_role_exists_is_reported(self):
        rc, out, _ = self.run_bindings(self.listing(), "--scope", "cluster", "--node", "present")
        self.assertEqual((rc, out.splitlines()), (1, ["missing: ClusterRoleBinding studio-hub-nodes"]))

    def test_the_node_binding_without_its_role_is_reported(self):
        rc, out, _ = self.run_bindings(self.listing(self.node_binding()), "--scope", "cluster", "--node", "absent")
        self.assertEqual(rc, 1)
        self.assertIn("foreign: ClusterRoleBinding studio-hub-nodes exists but the ClusterRole", out)

    def test_the_node_binding_pointing_at_another_role_is_reported(self):
        admin = self.node_binding(role="cluster-admin")
        rc, out, _ = self.run_bindings(self.listing(admin), "--scope", "cluster", "--node", "present")
        self.assertEqual(rc, 1)
        self.assertIn("roleRef is", out)

    def test_any_other_cluster_binding_naming_the_account_is_reported(self):
        admin = self.binding("hub-admin", kind="ClusterRole", role="cluster-admin", binding_kind="ClusterRoleBinding")
        for node, extra in (("absent", []), ("present", [self.node_binding()])):
            with self.subTest(node=node):
                rc, out, _ = self.run_bindings(self.listing(*extra, admin), "--scope", "cluster", "--node", node)
                self.assertEqual(rc, 1)
                self.assertIn("foreign: ClusterRoleBinding hub-admin names ServiceAccount rpg-k8s-hub/studio-hub", out)
                self.assertIn("only 'studio-hub-nodes' may", out)

    def test_unreadable_input_is_exit_two(self):
        self.assertEqual(self.run_bindings("<html>", "--scope", "cluster")[0], 2)
        self.assertEqual(self.run_bindings({"kind": "Status"}, "--scope", "cluster")[0], 2)
        self.assertEqual(self.run_bindings(self.listing(), "--scope", "namespaced")[0], 2)

    def test_a_bare_list_is_accepted(self):
        rc, out, _ = self.run_bindings([self.binding("studio-hub", "x")], "--scope", "namespaced", "--namespace", "x")
        self.assertEqual((rc, out), (0, ""))


class Cli(unittest.TestCase):
    def run_main(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = hc.main(list(argv))
        return rc, out.getvalue()

    def copy_dir(self, tmp):
        tmp = pathlib.Path(tmp)
        (tmp / "optional").mkdir()
        for f in hc.HUB_DIR.glob("*.yaml"):
            (tmp / f.name).write_text(f.read_text(encoding="utf-8"), encoding="utf-8")
        for f in (hc.HUB_DIR / "optional").glob("*.yaml"):
            (tmp / "optional" / f.name).write_text(f.read_text(encoding="utf-8"), encoding="utf-8")
        return tmp

    def test_default_run_passes(self):
        rc, out = self.run_main()
        self.assertEqual(rc, 0, out)
        self.assertIn("0 failure(s)", out)
        # The no-argument run includes a rendered sample non-default game, like logging_contract.
        self.assertIn("render[ember]: hub Role + RoleBinding", out)

    def test_no_game_skips_the_render_leg(self):
        rc, out = self.run_main("--no-game")
        self.assertEqual(rc, 0, out)
        self.assertNotIn("render[", out)

    def test_a_mutated_copy_of_the_directory_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = self.copy_dir(tmp)
            f = tmp / "20-rbac.yaml"
            text = f.read_text(encoding="utf-8")
            marker = 'verbs: ["create"]\n---'
            self.assertIn(marker, text)
            f.write_text(text.replace(marker, 'verbs: ["create", "delete"]\n---', 1), encoding="utf-8")
            rc, out = self.run_main("--dir", str(tmp))
        self.assertEqual(rc, 1, out)
        self.assertIn("extra: allocation.agones.dev/gameserverallocations delete", out)

    def test_a_secret_rule_added_to_a_copy_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = self.copy_dir(tmp)
            f = tmp / "20-rbac.yaml"
            text = f.read_text(encoding="utf-8")
            # The end of the DATA Role: the only place where the statefulsets rule is followed by `---`.
            marker = '    resources: ["statefulsets", "replicasets"]\n    verbs: ["get", "list", "watch"]\n---'
            self.assertEqual(text.count(marker), 1)
            f.write_text(text.replace(
                marker, marker[:-3] + '  - apiGroups: [""]\n    resources: ["secrets"]\n    verbs: ["get"]\n---'),
                encoding="utf-8")
            rc, out = self.run_main("--dir", str(tmp))
        self.assertEqual(rc, 1, out)
        self.assertIn("extra: core/secrets get", out)

    def test_a_node_verb_added_to_the_optional_copy_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = self.copy_dir(tmp)
            f = tmp / "optional" / "node-access.yaml"
            text = f.read_text(encoding="utf-8")
            marker = 'verbs: ["get", "list", "watch", "patch"]'
            self.assertIn(marker, text)
            f.write_text(text.replace(marker, 'verbs: ["get", "list", "watch", "patch", "delete"]'), encoding="utf-8")
            rc, out = self.run_main("--dir", str(tmp))
        self.assertEqual(rc, 1, out)
        self.assertIn("extra: core/nodes delete", out)

    def test_an_empty_directory_is_an_input_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            rc, _ = self.run_main("--dir", tmp)
        self.assertEqual(rc, 2)


class AdmissionPolicy(unittest.TestCase):
    """The ValidatingAdmissionPolicy that narrows `patch deployments` to a rollout restart.

    No CEL evaluator is available to this repository's toolchain, so these tests pin the TEXT of
    the policy and show the contract rejecting each departure from it. They do NOT prove that an
    API server denies an image change; that is the live dry-run in docs/RUNBOOK.md.
    """

    def mutate(self, fn):
        docs, optional = copy.deepcopy(committed())
        fn(docs, optional)
        return hc.check_hub(docs, optional)

    def assertRejected(self, fn, needle):
        problems = self.mutate(fn)
        self.assertTrue(problems, "the mutation was accepted")
        self.assertTrue(any(needle in p for p in problems), f"{needle!r} not in {problems}")

    def policy(self, docs):
        return find(docs, "ValidatingAdmissionPolicy")

    def binding(self, docs):
        return find(docs, "ValidatingAdmissionPolicyBinding")

    # ---- the committed files equal the contract ----
    def test_the_committed_policy_and_binding_equal_the_contract(self):
        docs, _ = committed()
        self.assertEqual(hc.policy_problems(self.policy(docs), "policy"), [])
        self.assertEqual(hc.policy_problems(self.binding(docs), "binding"), [])

    def test_the_policy_objects_are_cluster_scoped_and_named(self):
        docs, _ = committed()
        for d in (self.policy(docs), self.binding(docs)):
            self.assertEqual(d["metadata"]["name"], "studio-hub-restart-only")
            self.assertNotIn("namespace", d["metadata"])
            self.assertEqual(d["apiVersion"], "admissionregistration.k8s.io/v1")

    # ---- what the contract pins ----
    def test_it_matches_only_the_hub_username(self):
        self.assertEqual(hc.POLICY_SPEC["matchConditions"], [
            {"name": "only-the-studio-hub",
             "expression": "request.userInfo.username == 'system:serviceaccount:rpg-k8s-hub:studio-hub'"}])
        self.assertEqual(hc.HUB_USER_NAME, "system:serviceaccount:rpg-k8s-hub:studio-hub")

    def test_it_matches_update_of_deployments_only(self):
        rules = hc.POLICY_SPEC["matchConstraints"]["resourceRules"]
        self.assertEqual(len(rules), 1)
        self.assertEqual(rules[0]["operations"], ["UPDATE"])
        self.assertEqual(rules[0]["apiGroups"], ["apps"])
        self.assertEqual(rules[0]["resources"], ["deployments"])          # no deployments/scale, no /status
        self.assertEqual(hc.POLICY_SPEC["matchConstraints"]["excludeResourceRules"], [])

    def test_it_fails_closed_and_the_binding_denies_cluster_wide(self):
        self.assertEqual(hc.POLICY_SPEC["failurePolicy"], "Fail")
        self.assertEqual(hc.BINDING_SPEC["validationActions"], ["Deny"])
        self.assertIsNone(hc.BINDING_SPEC["matchResources"]["namespaceSelector"])
        self.assertIsNone(hc.BINDING_SPEC["matchResources"]["objectSelector"])
        self.assertEqual(hc.BINDING_SPEC["matchResources"]["excludeResourceRules"], [])

    def test_the_policy_is_required_because_every_shape_grants_patch_on_deployments(self):
        for shape in ("realtime", "data"):
            self.assertIn(("apps", "deployments", "patch"), hc.SHAPES[shape])
        # ... and nothing narrower than a Deployment `patch` exists for a restart: not `deployments/scale`.
        for shape, triples in hc.SHAPES.items():
            self.assertNotIn(("apps", "deployments/scale", "update"), triples)
            self.assertNotIn(("apps", "deployments/scale", "patch"), triples)
            self.assertNotIn(("apps", "deployments/status", "patch"), triples)

    def test_the_restart_annotation_is_what_kubectl_rollout_restart_writes(self):
        self.assertEqual(hc.RESTART_ANNOTATION, "kubectl.kubernetes.io/restartedAt")
        text = "\n".join(e for e, _ in hc.POLICY_VALIDATIONS)
        self.assertIn("'kubectl.kubernetes.io/restartedAt'", text)
        # exactly the two template-annotation expressions name it, and no other expression
        self.assertEqual(sum(1 for e, _ in hc.POLICY_VALIDATIONS if "restartedAt" in e), 2)

    # Every field of apps/v1 DeploymentSpec (Kubernetes 1.30-1.34) must be covered by an expression:
    # the template (as the two variables) and each sibling. A field Kubernetes adds later is NOT covered
    # until this list and the policy grow together; reviewing it is part of a Kubernetes upgrade.
    DEPLOYMENT_SPEC_FIELDS = ("minReadySeconds", "paused", "progressDeadlineSeconds", "replicas",
                              "revisionHistoryLimit", "selector", "strategy", "template")

    def test_every_deployment_spec_field_is_covered(self):
        text = "\n".join(e for e, _ in hc.POLICY_VALIDATIONS)
        for field in self.DEPLOYMENT_SPEC_FIELDS:
            with self.subTest(field=field):
                if field == "template":
                    self.assertIn("variables.newTemplate.spec == variables.oldTemplate.spec", text)
                    self.assertEqual(
                        {v["name"]: v["expression"] for v in hc.POLICY_SPEC["variables"]},
                        {"newTemplate": "object.spec.template", "oldTemplate": "oldObject.spec.template"})
                else:
                    self.assertIn(f"object.spec.{field} ", text + " ")
                    self.assertIn(f"oldObject.spec.{field}", text)

    def test_every_pod_template_metadata_field_the_controller_copies_is_covered(self):
        text = "\n".join(e for e, _ in hc.POLICY_VALIDATIONS)
        for field in ("labels", "finalizers", "annotations"):
            with self.subTest(field=field):
                self.assertIn(f"variables.newTemplate.metadata.{field}", text)
                self.assertIn(f"variables.oldTemplate.metadata.{field}", text)
        for field in ("labels", "annotations"):
            self.assertIn(f"object.metadata.{field}", text)
            self.assertIn(f"oldObject.metadata.{field}", text)

    # Every field of a Deployment's ObjectMeta (apps/v1, Kubernetes 1.30-1.35), what the hub can do with it
    # through `patch deployments`, and what decides. The "api" rows rest on the API server's own rules
    # (immutable or reset on update) and are NOT proven offline: the live check in docs/RUNBOOK.md runs one
    # dry-run per row that matters. A field Kubernetes adds to ObjectMeta is not in this table: review it on
    # every upgrade, like DEPLOYMENT_SPEC_FIELDS.
    METADATA_FIELDS = {
        # compared by the policy (a validation of the policy decides)
        "labels": ("policy", "object.metadata.labels"),
        "annotations": ("policy", "object.metadata.annotations"),
        "ownerReferences": ("policy", "object.metadata.ownerReferences"),   # a fake owner: the garbage collector deletes the Deployment
        "finalizers": ("policy", "object.metadata.finalizers"),             # a finalizer wedges every delete and its namespace
        "name": ("policy", "object.metadata.name"),
        "namespace": ("policy", "object.metadata.namespace"),
        "generateName": ("policy", "object.metadata.generateName"),
        # the API server decides, on every update, whatever the hub sends
        "uid": ("api", "immutable on update"),
        "creationTimestamp": ("api", "immutable on update"),
        "deletionTimestamp": ("api", "immutable on update; a client cannot start a deletion by writing it"),
        "deletionGracePeriodSeconds": ("api", "immutable on update"),
        "generation": ("api", "reset to the stored value, then incremented by the server when the spec changed"),
        "resourceVersion": ("api", "an optimistic lock: a wrong value is a conflict, never a change"),
        "selfLink": ("api", "removed from the API"),
        # deliberately NOT compared: the API server rewrites it on every request, so comparing it would deny
        # the restart itself
        "managedFields": ("not compared", "rewritten by the field manager on every patch"),
    }

    def test_every_deployment_metadata_field_has_a_stated_disposition(self):
        text = "\n".join(e for e, _ in hc.POLICY_VALIDATIONS)
        for field, (who, why) in self.METADATA_FIELDS.items():
            with self.subTest(field=field):
                if who == "policy":
                    self.assertIn(why, text)
                    self.assertIn(why.replace("object.", "oldObject.", 1), text)
                else:
                    self.assertNotIn(f"metadata.{field}", text)

    def test_the_documents_state_the_metadata_decision(self):
        deploy = hc.DEPLOY_DIR
        readme = (deploy / "k8s" / "README.md").read_text(encoding="utf-8")
        runbook = (deploy / "docs" / "RUNBOOK.md").read_text(encoding="utf-8")
        yaml_text = (hc.HUB_DIR / "15-admission-policy.yaml").read_text(encoding="utf-8")
        for name, text in (("k8s/README.md", readme), ("docs/RUNBOOK.md", runbook)):
            for needle in ("ownerReferences", "finalizers", "managedFields"):
                self.assertIn(needle, text, f"{name} does not mention {needle}")
        for needle in ("ownerReferences", "finalizers", "managedFields", "garbage collector"):
            self.assertIn(needle, yaml_text)

    def test_the_fleetautoscaler_metadata_power_is_documented_as_accepted(self):
        # The hub may patch/update a FleetAutoscaler, and an ownerReference there makes the garbage collector
        # delete it. No policy covers it (accepted, with the reason): the docs must say so, in both places.
        deploy = hc.DEPLOY_DIR
        for name in ("k8s/README.md", "docs/RUNBOOK.md"):
            text = (deploy / name).read_text(encoding="utf-8")
            self.assertRegex(text, r"(?s)FleetAutoscaler.{0,400}ownerReference|ownerReference.{0,400}FleetAutoscaler", name)

    def test_optional_fields_are_read_only_behind_has(self):
        """Reading an absent field is a CEL error; each optional field's value is compared only under has()."""
        for e, _ in hc.POLICY_VALIDATIONS:
            for field in ("labels", "finalizers", "ownerReferences", "generateName", "minReadySeconds", "paused"):
                if f".{field}" in e:
                    self.assertIn(f"has(", e, e)
                    self.assertIn(f"!has(", e, e)
        # the annotation expressions guard BOTH maps before indexing them
        e4, e5 = hc.POLICY_VALIDATIONS[3][0], hc.POLICY_VALIDATIONS[4][0]
        self.assertIn("has(variables.oldTemplate.metadata.annotations) && k in", e4)
        self.assertIn("has(variables.newTemplate.metadata.annotations) && k in", e5)

    def test_expressions_are_single_line_with_balanced_brackets_and_quotes(self):
        exprs = [e for e, _ in hc.POLICY_VALIDATIONS] + [hc.POLICY_MATCH_USER_EXPRESSION]
        exprs += [v["expression"] for v in hc.POLICY_SPEC["variables"]]
        for e in exprs:
            with self.subTest(expression=e[:60]):
                self.assertNotIn("\n", e)
                self.assertEqual(e.count("("), e.count(")"))
                self.assertEqual(e.count("'") % 2, 0)

    def test_every_validation_has_a_message_and_forbidden_reason(self):
        for v in hc.POLICY_SPEC["validations"]:
            self.assertTrue(v["message"].startswith("the Studio hub may only restart a Deployment"))
            self.assertEqual(v["reason"], "Forbidden")

    def test_the_cases_the_policy_must_decide_are_each_named_by_an_expression(self):
        """The intent table (restart: allow; image, env, privileged, hostPath: pod spec;
        replicas; label: deny). Which expression decides each case, as text; the evaluation is live-only."""
        decides = {
            "restartedAt added, changed or removed": (3, 4),
            "image / env / privileged / hostPath / command (pod spec)": (0,),
            "replicas change": (5,),
            "template label change": (1,),
            "Deployment label change": (7,),
            "Deployment annotation change": (8,),
            "paused / minReadySeconds": (6,),
        }
        for case, idx in decides.items():
            for i in idx:
                self.assertLess(i, len(hc.POLICY_VALIDATIONS), case)
        self.assertEqual(len(hc.POLICY_VALIDATIONS), 12)
        # F2 of the live QA: the Deployment's own ownerReferences / finalizers / identity are 9, 10, 11.
        for i, field in ((9, "ownerReferences"), (10, "finalizers"), (11, "name")):
            self.assertIn(f"object.metadata.{field}", hc.POLICY_VALIDATIONS[i][0])
            self.assertIn(f"oldObject.metadata.{field}", hc.POLICY_VALIDATIONS[i][0])

    # ---- the live comparison ----
    def api_policy(self):
        spec = copy.deepcopy(hc.POLICY_SPEC)
        spec["matchConstraints"]["namespaceSelector"] = {}
        spec["matchConstraints"]["objectSelector"] = {}
        return {"apiVersion": hc.ADMISSION_API, "kind": "ValidatingAdmissionPolicy",
                "metadata": {"name": hc.POLICY_NAME}, "spec": spec, "status": {"observedGeneration": 1}}

    def test_defaults_filled_by_the_api_server_are_not_differences(self):
        obj = self.api_policy()
        del obj["spec"]["failurePolicy"]                       # defaults to Fail
        obj["spec"]["matchConstraints"]["resourceRules"][0].pop("scope")   # defaults to "*"
        obj["spec"]["matchConstraints"]["resourceRules"][0].pop("resourceNames")
        for v in obj["spec"]["validations"]:
            v.pop("messageExpression")
        self.assertEqual(hc.policy_problems(obj, "policy"), [])

    def test_a_changed_expression_is_named_by_path(self):
        obj = self.api_policy()
        obj["spec"]["validations"][2]["expression"] = "true"
        self.assertEqual(len(hc.policy_problems(obj, "policy")), 1)
        self.assertIn("spec.validations[2].expression", hc.policy_problems(obj, "policy")[0])

    def test_an_unexpected_field_is_a_difference(self):
        obj = self.api_policy()
        obj["spec"]["auditAnnotations"] = [{"key": "k", "valueExpression": "'v'"}]
        self.assertTrue(hc.policy_problems(obj, "policy"))
        obj = self.api_policy()
        obj["spec"]["paramKind"] = {"apiVersion": "v1", "kind": "ConfigMap"}
        self.assertTrue(hc.policy_problems(obj, "policy"))

    def test_a_binding_that_only_warns_or_audits_differs(self):
        for actions in (["Warn"], ["Audit"], ["Deny", "Warn"], []):
            obj = {"kind": "ValidatingAdmissionPolicyBinding",
                   "spec": {"policyName": hc.POLICY_NAME, "validationActions": actions}}
            with self.subTest(actions=actions):
                self.assertTrue(hc.policy_problems(obj, "binding"))
        ok = {"kind": "ValidatingAdmissionPolicyBinding",
              "spec": {"policyName": hc.POLICY_NAME, "validationActions": ["Deny"],
                       "matchResources": {"namespaceSelector": {}, "objectSelector": {}}}}
        self.assertEqual(hc.policy_problems(ok, "binding"), [])

    def test_a_namespace_selector_on_the_binding_differs(self):
        obj = {"spec": {"policyName": hc.POLICY_NAME, "validationActions": ["Deny"],
                        "matchResources": {"namespaceSelector": {"matchLabels": {"a": "b"}}}}}
        self.assertTrue(hc.policy_problems(obj, "binding"))

    # ---- manifest mutations ----
    def test_a_replaced_expression_is_rejected(self):
        def m(docs, opt):
            self.policy(docs)["spec"]["validations"][0]["expression"] = "true"
        self.assertRejected(m, "differs: spec.validations[0].expression")

    def test_the_policy_missing_is_rejected(self):
        def m(docs, opt):
            docs[:] = [(lab, d) for lab, d in docs if d["kind"] != "ValidatingAdmissionPolicy"]
        self.assertRejected(m, "expected exactly one ValidatingAdmissionPolicy")

    def test_the_binding_missing_is_rejected(self):
        def m(docs, opt):
            docs[:] = [(lab, d) for lab, d in docs if d["kind"] != "ValidatingAdmissionPolicyBinding"]
        self.assertRejected(m, "expected exactly one ValidatingAdmissionPolicyBinding")

    def test_two_policies_are_rejected(self):
        def m(docs, opt):
            docs.append(("copy", copy.deepcopy(self.policy(docs))))
        self.assertRejected(m, "expected exactly one ValidatingAdmissionPolicy")

    def test_a_binding_that_warns_is_rejected(self):
        def m(docs, opt):
            self.binding(docs)["spec"]["validationActions"] = ["Warn"]
        self.assertRejected(m, "differs: spec.validationActions")

    def test_a_namespace_selector_on_the_binding_is_rejected(self):
        def m(docs, opt):
            self.binding(docs)["spec"]["matchResources"] = {"namespaceSelector": {"matchLabels": {"a": "b"}}}
        self.assertRejected(m, "differs: spec.matchResources.namespaceSelector")

    def test_failure_policy_ignore_is_rejected(self):
        def m(docs, opt):
            self.policy(docs)["spec"]["failurePolicy"] = "Ignore"
        self.assertRejected(m, "differs: spec.failurePolicy")

    def test_matchconditions_removed_is_rejected(self):
        def m(docs, opt):
            del self.policy(docs)["spec"]["matchConditions"]
        self.assertRejected(m, "differs: spec.matchConditions")

    def test_the_policy_with_a_namespace_is_rejected(self):
        def m(docs, opt):
            self.policy(docs)["metadata"]["namespace"] = "rpg-k8s-realtime"
        self.assertRejected(m, "cluster-scoped")

    def test_a_wrong_api_version_is_rejected(self):
        def m(docs, opt):
            self.policy(docs)["apiVersion"] = "admissionregistration.k8s.io/v1beta1"
        self.assertRejected(m, "apiVersion")

    def test_a_clusterrole_is_still_rejected_next_to_the_policy(self):
        def m(docs, opt):
            docs.append(("x", {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRole",
                               "metadata": {"name": "x"}, "rules": []}))
        self.assertRejected(m, "ClusterRole is not allowed here")

    def test_the_manifest_comments_state_the_property_change(self):
        text = (hc.HUB_DIR / "15-admission-policy.yaml").read_text(encoding="utf-8")
        for needle in ("CLUSTER-SCOPED", "NO ClusterRole and NO ClusterRoleBinding", "RESTRICT",
                       "restart", "1.30", "NOT PROVEN OFFLINE", "FAILS CLOSED", "ownerReferences"):
            self.assertIn(needle, text)


class WriteSurface(unittest.TestCase):
    """Which writes the Roles grant, and that none of them carries a pod template the hub can set
    (other than `patch deployments`, which the admission policy narrows)."""

    def writes(self, shape):
        return {t for t in hc.SHAPES[shape] if t[2] not in ("get", "list", "watch")}

    def test_the_realtime_writes_are_exactly_these(self):
        self.assertEqual(self.writes("realtime"), {
            ("", "pods", "delete"),
            ("apps", "deployments", "patch"),
            ("agones.dev", "fleets/scale", "update"),
            ("agones.dev", "fleets/scale", "patch"),
            ("agones.dev", "gameservers", "delete"),
            ("autoscaling.agones.dev", "fleetautoscalers", "patch"),
            ("autoscaling.agones.dev", "fleetautoscalers", "update"),
            ("allocation.agones.dev", "gameserverallocations", "create"),
        })

    def test_no_pod_template_bearing_resource_is_written_but_deployments(self):
        # Kinds that carry a pod template (or a pod spec): a write on any of them would let the hub set one.
        carriers = {"pods", "statefulsets", "daemonsets", "replicasets", "replicationcontrollers", "jobs",
                    "cronjobs", "fleets", "gameserversets", "gameservers"}
        for shape in ("realtime", "data", "node"):
            for group, resource, verb in self.writes(shape):
                if resource in carriers:
                    # the only allowed: delete (removes, never sets a spec)
                    self.assertEqual(verb, "delete", (shape, group, resource, verb))

    def test_no_statefulset_replicaset_or_daemonset_write(self):
        for shape in ("realtime", "data"):
            for group, resource, verb in hc.SHAPES[shape]:
                if resource in ("statefulsets", "replicasets", "daemonsets", "statefulsets/scale"):
                    self.assertIn(verb, ("get", "list", "watch"))


class OutsideNamespaces(unittest.TestCase):
    """RoleBindings of `kubectl get rolebindings -A` against the expected namespaces."""

    EXPECTED = ["rpg-k8s-realtime", "rpg-k8s-data"]

    def rb(self, ns, name, subjects):
        return {"kind": "RoleBinding", "metadata": {"name": name, "namespace": ns}, "subjects": subjects,
                "roleRef": {"apiGroup": "rbac.authorization.k8s.io", "kind": "ClusterRole", "name": "edit"}}

    def test_a_binding_elsewhere_naming_the_account_is_foreign(self):
        for ns in ("kube-system", "rpg-k8s-hub", "agones-system", "rpg-k8s-realtime-zephyr"):
            with self.subTest(ns=ns):
                problems = hc.outside_namespaces_problems(
                    [self.rb(ns, "x", hc.WANT_SUBJECTS)], self.EXPECTED)
                self.assertEqual(len(problems), 1)
                self.assertIn(f"foreign: RoleBinding {ns}/x", problems[0])

    def test_the_other_spellings_are_foreign_too(self):
        for subject in ({"kind": "User", "name": hc.HUB_USER_NAME},
                        {"kind": "Group", "name": "system:serviceaccounts:rpg-k8s-hub"}):
            with self.subTest(subject=subject):
                self.assertTrue(hc.outside_namespaces_problems([self.rb("kube-system", "x", [subject])], self.EXPECTED))

    def test_unrelated_bindings_and_the_expected_namespaces_are_not_reported_here(self):
        items = [self.rb("kube-system", "other", [{"kind": "ServiceAccount", "name": "other", "namespace": "kube-system"}]),
                 self.rb("rpg-k8s-data", "studio-hub", hc.WANT_SUBJECTS),
                 self.rb("kube-system", "auth", [{"kind": "Group", "name": "system:authenticated"}]),
                 self.rb("kube-system", "all-sa", [{"kind": "Group", "name": "system:serviceaccounts"}])]
        self.assertEqual(hc.outside_namespaces_problems(items, self.EXPECTED), [])

    def test_a_game_namespace_that_is_expected_is_not_foreign(self):
        item = self.rb("rpg-k8s-realtime-ember", "studio-hub", hc.WANT_SUBJECTS)
        self.assertEqual(hc.outside_namespaces_problems([item], self.EXPECTED + ["rpg-k8s-realtime-ember"]), [])
        self.assertTrue(hc.outside_namespaces_problems([item], self.EXPECTED))


class LegacyTokenSecrets(unittest.TestCase):
    TOKEN = "kubernetes.io/service-account-token"

    def test_a_service_account_token_secret_of_the_hub_is_a_problem(self):
        problems = hc.secrets_problems(f"studio-hub-token\t{self.TOKEN}\tstudio-hub\n")
        self.assertEqual(len(problems), 1)
        self.assertIn("legacy token: Secret rpg-k8s-hub/studio-hub-token", problems[0])

    def test_the_runbooks_opaque_binding_secret_and_other_accounts_are_fine(self):
        text = f"studio-hub-token-binding\tOpaque\t\nbuilder-token\t{self.TOKEN}\tbuilder\n\n"
        self.assertEqual(hc.secrets_problems(text), [])

    def test_a_secret_of_another_type_annotated_for_the_hub_is_not_a_token(self):
        self.assertEqual(hc.secrets_problems("x\tOpaque\tstudio-hub\n"), [])

    def test_there_is_no_documented_token_secret(self):
        self.assertEqual(hc.DOCUMENTED_TOKEN_SECRETS, ())

    def test_a_documented_one_would_be_accepted(self):
        old = hc.DOCUMENTED_TOKEN_SECRETS
        try:
            hc.DOCUMENTED_TOKEN_SECRETS = ("keep",)
            self.assertEqual(hc.secrets_problems(f"keep\t{self.TOKEN}\tstudio-hub\n"), [])
        finally:
            hc.DOCUMENTED_TOKEN_SECRETS = old


class NewCli(unittest.TestCase):
    def run_main(self, text, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = hc.main(list(argv), stdin=io.StringIO(text))
        return rc, out.getvalue()

    def test_policy_equal_exits_0(self):
        docs, _ = committed()
        obj = copy.deepcopy(find(docs, "ValidatingAdmissionPolicy"))
        rc, out = self.run_main(json.dumps(obj), "policy", "--kind", "policy")
        self.assertEqual((rc, out), (0, ""))

    def test_policy_different_exits_1_with_a_differs_line(self):
        docs, _ = committed()
        obj = copy.deepcopy(find(docs, "ValidatingAdmissionPolicy"))
        obj["spec"]["failurePolicy"] = "Ignore"
        rc, out = self.run_main(json.dumps(obj), "policy", "--kind", "policy")
        self.assertEqual(rc, 1)
        self.assertIn("differs: spec.failurePolicy", out)

    def test_policy_unreadable_or_the_wrong_kind_exits_2(self):
        self.assertEqual(self.run_main("not json", "policy", "--kind", "policy")[0], 2)
        docs, _ = committed()
        binding = find(docs, "ValidatingAdmissionPolicyBinding")
        self.assertEqual(self.run_main(json.dumps(binding), "policy", "--kind", "policy")[0], 2)
        self.assertEqual(self.run_main(json.dumps(binding), "policy", "--kind", "binding")[0], 0)

    def test_secrets_exit_codes(self):
        self.assertEqual(self.run_main("", "secrets")[0], 0)
        rc, out = self.run_main("t\tkubernetes.io/service-account-token\tstudio-hub\n", "secrets")
        self.assertEqual(rc, 1)
        self.assertIn("legacy token", out)

    def test_bindings_all_namespaces(self):
        item = {"kind": "RoleBinding", "metadata": {"name": "x", "namespace": "kube-system"},
                "subjects": hc.WANT_SUBJECTS, "roleRef": {"kind": "ClusterRole", "name": "edit"}}
        rc, out = self.run_main(json.dumps({"items": [item]}), "bindings", "--scope", "all-namespaces",
                                "--expected", "rpg-k8s-realtime,rpg-k8s-data")
        self.assertEqual(rc, 1)
        self.assertIn("foreign: RoleBinding kube-system/x", out)
        self.assertEqual(self.run_main(json.dumps({"items": [item]}), "bindings", "--scope", "all-namespaces")[0], 2)


if __name__ == "__main__":
    unittest.main()
