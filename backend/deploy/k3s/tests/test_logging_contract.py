"""Offline tests of the log collector contract (k3s/logging_contract.py).

PyYAML only: no jsonschema, no Agones download, no cluster. They show the contract
holding on the committed k8s/logging and on a rendered game, and REJECTING each
mutation it exists to catch (a ClusterRole, a widened Role, a hostPath, ...):

    python -m unittest discover -s backend/deploy/k3s/tests -p "test_*.py"
"""

import contextlib
import copy
import importlib.util
import io
import pathlib
import tempfile
import unittest

import yaml

HERE = pathlib.Path(__file__).resolve().parent
K3S = HERE.parent
spec = importlib.util.spec_from_file_location("logging_contract", K3S / "logging_contract.py")
lc = importlib.util.module_from_spec(spec)
spec.loader.exec_module(lc)


def committed():
    docs, problems = lc.load_dir(lc.LOGGING_DIR)
    assert not problems, problems
    return docs


def find(docs, kind, name=None, ns=None):
    out = [d for _, d in docs if d.get("kind") == kind and (name is None or d["metadata"]["name"] == name)
           and (ns is None or d["metadata"].get("namespace") == ns)]
    assert out, (kind, name, ns)
    return out[0]


def relabel(docs):
    return [(f"{d.get('kind')}/{d['metadata']['name']}", d) for _, d in docs]


class CommittedManifests(unittest.TestCase):
    def test_contract_holds(self):
        self.assertEqual(lc.check_logging_manifests(committed()), [])

    def test_every_collected_namespace_has_the_pair(self):
        docs = committed()
        roles = sorted(d["metadata"]["namespace"] for _, d in docs if d["kind"] == "Role")
        self.assertEqual(roles, ["rpg-k8s-data", "rpg-k8s-logging", "rpg-k8s-realtime"])


class Mutations(unittest.TestCase):
    """Every case starts from the committed manifests and breaks exactly one rule."""

    def mutate(self, fn):
        docs = copy.deepcopy(committed())
        fn(docs)
        return lc.check_logging_manifests(docs)

    def assertRejected(self, fn, needle):
        problems = self.mutate(fn)
        self.assertTrue(problems, "the mutation was accepted")
        self.assertTrue(any(needle in p for p in problems), f"{needle!r} not in {problems}")

    # ---- RBAC ----
    def test_cluster_role_is_rejected(self):
        def m(docs):
            docs.append(("x", {"apiVersion": "rbac.authorization.k8s.io/v1", "kind": "ClusterRole",
                               "metadata": {"name": "rpg-log-collector"},
                               "rules": [{"apiGroups": [""], "resources": ["pods"], "verbs": ["list"]}]}))
        self.assertRejected(m, "ClusterRole is not allowed")

    def test_cluster_role_binding_is_rejected(self):
        def m(docs):
            docs.append(("x", {"kind": "ClusterRoleBinding", "metadata": {"name": "rpg-log-collector"}}))
        self.assertRejected(m, "ClusterRoleBinding is not allowed")

    def test_widened_role_resources_are_rejected(self):
        def m(docs):
            find(docs, "Role", ns="rpg-k8s-data")["rules"].append(
                {"apiGroups": [""], "resources": ["secrets"], "verbs": ["get"]})
        self.assertRejected(m, "exactly")

    def test_widened_role_verbs_are_rejected(self):
        def m(docs):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"][0]["verbs"].append("delete")
        self.assertRejected(m, "exactly")

    def test_pods_log_widened_to_list_is_rejected(self):
        def m(docs):
            find(docs, "Role", ns="rpg-k8s-realtime")["rules"][1]["verbs"].append("list")
        self.assertRejected(m, "exactly")

    def test_dropped_rule_is_rejected(self):
        def m(docs):
            find(docs, "Role", ns="rpg-k8s-logging")["rules"].pop()
        self.assertRejected(m, "exactly")

    def test_wildcard_is_rejected(self):
        def m(docs):
            find(docs, "Role", ns="rpg-k8s-logging")["rules"][0]["resources"] = ["*"]
        self.assertRejected(m, "exactly")

    def test_resource_names_are_part_of_the_rule(self):
        def m(docs):
            find(docs, "Role", ns="rpg-k8s-logging")["rules"][0]["resourceNames"] = ["x"]
        self.assertRejected(m, "resourceNames")

    def test_other_role_name_is_rejected(self):
        def m(docs):
            find(docs, "Role", ns="rpg-k8s-logging")["metadata"]["name"] = "something-else"
        self.assertRejected(m, "only Role the collector may have")

    def test_role_outside_rpg_k8s_is_rejected(self):
        def m(docs):
            find(docs, "Role", ns="rpg-k8s-data")["metadata"]["namespace"] = "kube-system"
            find(docs, "RoleBinding", ns="rpg-k8s-data")["metadata"]["namespace"] = "kube-system"
        self.assertRejected(m, "not an rpg-k8s-* namespace")

    def test_binding_to_another_subject_is_rejected(self):
        def m(docs):
            find(docs, "RoleBinding", ns="rpg-k8s-data")["subjects"][0]["name"] = "default"
        self.assertRejected(m, "only subject allowed")

    def test_binding_to_another_namespace_is_rejected(self):
        def m(docs):
            find(docs, "RoleBinding", ns="rpg-k8s-data")["subjects"][0]["namespace"] = "rpg-k8s-data"
        self.assertRejected(m, "only subject allowed")

    def test_binding_to_a_cluster_role_is_rejected(self):
        def m(docs):
            find(docs, "RoleBinding", ns="rpg-k8s-data")["roleRef"]["kind"] = "ClusterRole"
        self.assertRejected(m, "roleRef")

    def test_role_without_binding_is_rejected(self):
        def m(docs):
            docs[:] = [(lab, d) for lab, d in docs
                       if not (d["kind"] == "RoleBinding" and d["metadata"]["namespace"] == "rpg-k8s-data")]
        self.assertRejected(m, "has no RoleBinding")

    # ---- DaemonSet ----
    def pod(self, docs):
        return find(docs, "DaemonSet", "alloy")["spec"]["template"]["spec"]

    def test_hostpath_is_rejected(self):
        def m(docs):
            self.pod(docs)["volumes"].append({"name": "varlog", "hostPath": {"path": "/var/log"}})
        self.assertRejected(m, "hostPath")

    def test_host_network_pid_ipc_are_rejected(self):
        for flag in ("hostNetwork", "hostPID", "hostIPC"):
            with self.subTest(flag=flag):
                def m(docs, flag=flag):
                    self.pod(docs)[flag] = True
                self.assertRejected(m, flag)

    def test_hostport_is_rejected(self):
        def m(docs):
            self.pod(docs)["containers"][0]["ports"] = [{"containerPort": 12345, "hostPort": 12345}]
        self.assertRejected(m, "hostPort")

    def test_privileged_is_rejected(self):
        def m(docs):
            self.pod(docs)["containers"][0]["securityContext"]["privileged"] = True
        self.assertRejected(m, "privileged")

    def test_privilege_escalation_is_rejected(self):
        def m(docs):
            self.pod(docs)["containers"][0]["securityContext"]["allowPrivilegeEscalation"] = True
        self.assertRejected(m, "privilege escalation")

    def test_writable_root_filesystem_is_rejected(self):
        def m(docs):
            self.pod(docs)["containers"][0]["securityContext"]["readOnlyRootFilesystem"] = False
        self.assertRejected(m, "readOnlyRootFilesystem")

    def test_missing_read_only_root_filesystem_is_rejected(self):
        def m(docs):
            del self.pod(docs)["containers"][0]["securityContext"]["readOnlyRootFilesystem"]
        self.assertRejected(m, "readOnlyRootFilesystem")

    def test_root_is_rejected(self):
        def m(docs):
            self.pod(docs)["securityContext"]["runAsNonRoot"] = False
        self.assertRejected(m, "non-root")

    def test_an_init_container_is_held_to_the_same_rules(self):
        def m(docs):
            self.pod(docs)["initContainers"] = [{"name": "init", "image": "busybox",
                                                 "securityContext": {"privileged": True}}]
        self.assertRejected(m, "privileged")

    def test_missing_daemonset_is_rejected(self):
        def m(docs):
            docs[:] = [(lab, d) for lab, d in docs if d["kind"] != "DaemonSet"]
        self.assertRejected(m, "no DaemonSet")

    # ---- Service ----
    def loki(self, docs):
        return find(docs, "Service", "loki")

    def test_selector_on_loki_is_rejected(self):
        def m(docs):
            self.loki(docs)["spec"]["selector"] = {"app": "loki"}
        self.assertRejected(m, "selector")

    def test_nodeport_is_rejected(self):
        def m(docs):
            self.loki(docs)["spec"]["type"] = "NodePort"
        self.assertRejected(m, "NodePort")

    def test_loadbalancer_is_rejected(self):
        def m(docs):
            self.loki(docs)["spec"]["type"] = "LoadBalancer"
        self.assertRejected(m, "LoadBalancer")

    def test_external_ips_are_rejected(self):
        def m(docs):
            self.loki(docs)["spec"]["externalIPs"] = ["1.2.3.4"]
        self.assertRejected(m, "externalIPs")

    def test_missing_loki_service_is_rejected(self):
        def m(docs):
            docs[:] = [(lab, d) for lab, d in docs if d["kind"] != "Service"]
        self.assertRejected(m, "no Service")


def render(game="ember"):
    rspec = importlib.util.spec_from_file_location("render_under_test", lc.GAME_RENDERER)
    renderer = importlib.util.module_from_spec(rspec)
    rspec.loader.exec_module(renderer)
    rendered = renderer.render(game, nakama_tls=False)
    return renderer, rendered["manifests"], renderer.game_namespace(renderer.DEFAULT_STACK_NAMESPACE, game)


class RenderedGame(unittest.TestCase):
    def test_rendered_games_hold_the_contract(self):
        for game in ("ember", "g" * 24):
            with self.subTest(game=game):
                _, manifests, ns = render(game)
                self.assertEqual(lc.check_game_logging(manifests, game, ns), [])

    def test_the_game_namespace_is_what_the_game_label_comes_from(self):
        # `game` is derived from the namespace name by the collector's relabel
        # rules (tested in k8s/logging/tests): rpg-k8s-realtime-<game> -> <game>.
        _, manifests, ns = render("ember")
        self.assertEqual(ns, "rpg-k8s-realtime-ember")
        for d in manifests:
            if d["kind"] != "Namespace":
                self.assertEqual(d["metadata"]["namespace"], ns)

    def check(self, fn, needle, game="ember"):
        _, manifests, ns = render(game)
        manifests = copy.deepcopy(manifests)
        fn(manifests)
        problems = lc.check_game_logging(manifests, game, ns)
        self.assertTrue(any(needle in p for p in problems), f"{needle!r} not in {problems}")

    def test_missing_collector_role_is_rejected(self):
        self.check(lambda ms: ms.remove(find(relabel_pairs(ms), "Role", "rpg-log-collector")), "exactly one Role")

    def test_widened_collector_role_is_rejected(self):
        def m(ms):
            find(relabel_pairs(ms), "Role", "rpg-log-collector")["rules"].append(
                {"apiGroups": [""], "resources": ["secrets"], "verbs": ["get"]})
        self.check(m, "exactly")

    def test_cluster_role_in_the_render_is_rejected(self):
        self.check(lambda ms: ms.append({"kind": "ClusterRole", "metadata": {"name": "x"}}), "ClusterRole")

    def test_collector_binding_to_the_wrong_subject_is_rejected(self):
        def m(ms):
            find(relabel_pairs(ms), "RoleBinding", "rpg-log-collector")["subjects"][0]["namespace"] = "rpg-k8s-realtime"
        self.check(m, "only subject allowed")

    def test_lost_log_key_is_rejected(self):
        def m(ms):
            del find(relabel_pairs(ms), "ConfigMap", "gameserver-config")["data"]["log-format"]
        self.check(m, "log-format")

    def test_fleet_without_the_log_env_is_rejected(self):
        def m(ms):
            for d in ms:
                if d["kind"] == "Fleet":
                    c = d["spec"]["template"]["spec"]["template"]["spec"]["containers"][0]
                    c["env"] = [e for e in c["env"] if e["name"] != "GAMESERVER_LOG_LEVEL"]
        self.check(m, "GAMESERVER_LOG_LEVEL")


def relabel_pairs(manifests):
    return [("x", d) for d in manifests]


class Cli(unittest.TestCase):
    def run_main(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            rc = lc.main(list(argv))
        return rc, out.getvalue()

    def test_default_run_passes(self):
        rc, out = self.run_main()
        self.assertEqual(rc, 0, out)
        self.assertIn("0 failure(s)", out)

    def test_a_mutated_copy_of_the_directory_fails(self):
        with tempfile.TemporaryDirectory() as tmp:
            tmp = pathlib.Path(tmp)
            for f in lc.LOGGING_DIR.glob("*.yaml"):
                (tmp / f.name).write_text(f.read_text(encoding="utf-8"), encoding="utf-8")
            ds = tmp / "50-daemonset.yaml"
            text = ds.read_text(encoding="utf-8")
            marker = "      volumes:\n"
            self.assertIn(marker, text)
            ds.write_text(text.replace(
                marker, marker + "        - name: varlog\n          hostPath:\n            path: /var/log\n"),
                encoding="utf-8")
            rc, out = self.run_main("--dir", str(tmp), "--no-game")
        self.assertEqual(rc, 1, out)
        self.assertIn("hostPath", out)

    def test_an_empty_directory_is_an_input_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            rc, _ = self.run_main("--dir", tmp, "--no-game")
        self.assertEqual(rc, 2)


if __name__ == "__main__":
    unittest.main()
