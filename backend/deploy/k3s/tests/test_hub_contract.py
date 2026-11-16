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
            {"kind": "Group", "name": "system:serviceaccounts"},
        ):
            with self.subTest(subject=subject):
                def m(ms, subject=subject):
                    ms.append({"kind": "RoleBinding", "metadata": {"name": "sneaky", "namespace": self.NS},
                               "subjects": [subject],
                               "roleRef": {"kind": "ClusterRole", "name": "cluster-admin"}})
                self.assertRejected(m, "under another name")

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
    # Groups `system:serviceaccounts:NS` / `system:serviceaccounts`.
    ALIASES = (
        ("user", {"kind": "User", "name": "system:serviceaccount:rpg-k8s-hub:studio-hub",
                  "apiGroup": "rbac.authorization.k8s.io"}),
        ("group-namespace", {"kind": "Group", "name": "system:serviceaccounts:rpg-k8s-hub",
                             "apiGroup": "rbac.authorization.k8s.io"}),
        ("group-all", {"kind": "Group", "name": "system:serviceaccounts",
                       "apiGroup": "rbac.authorization.k8s.io"}),
    )

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

    def test_system_authenticated_is_not_covered(self):
        # Documented limit: Kubernetes binds its defaults (system:basic-user) to these groups.
        for name in ("system:authenticated", "system:unauthenticated"):
            self.assertFalse(hc.names_the_hub({"subjects": [{"kind": "Group", "name": name}]}))

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


if __name__ == "__main__":
    unittest.main()
