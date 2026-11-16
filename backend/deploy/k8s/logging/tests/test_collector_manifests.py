"""Offline tests of the k8s log collector (Alloy DaemonSet) under k8s/logging/.

No cluster, no Docker, no Alloy binary. Python 3.12 + PyYAML:

    python -m unittest discover -s backend/deploy/k8s/logging/tests -p "test_*.py"

What is covered:
  * RBAC: no cluster-scoped kind exists; each collected namespace has a Role with
    EXACTLY two rules (pods get/list/watch, pods/log get) and a binding to the
    collector's ServiceAccount; nothing grants secrets, configmaps, nodes, events
  * the DaemonSet has no hostPath/hostNetwork/hostPID/hostPort/privileged, the
    required security context, one emptyDir, and the same image as the compose
    collector (ALLOY_VERSION default)
  * the generated alloy-config ConfigMap is in sync (generate-config.py --check)
    and contains the compose `level` block byte for byte
  * the label rules, evaluated by a small relabel evaluator over the rules in the
    config: default game -> cuvara, rpg-k8s-realtime-<game> -> <game>, the gateway
    and the data namespace get no game, server_id is the pod name on game servers
  * the canonical `service` table of docs/MONITORING.md, pinned to the containers of
    the manifests and to what the rules produce
  * the push goes to the Service `loki`, which has no selector, with no
    EndpointSlice in the directory

The Alloy parser is the one of backend/deploy/tests/test_log_shipping.py.
"""

import importlib.util
import pathlib
import re
import subprocess
import sys
import tempfile
import unittest

import yaml

HERE = pathlib.Path(__file__).resolve().parent
LOGGING = HERE.parent
DEPLOY = LOGGING.parent.parent
COMPOSE = DEPLOY / "docker-compose.yml"
COMPOSE_ALLOY = DEPLOY / "monitoring" / "alloy" / "config.alloy"
K8S_PART = LOGGING / "config-k8s.alloy"
GENERATED = LOGGING / "40-alloy-config.yaml"
GENERATOR = LOGGING / "generate-config.py"

BEGIN = "// BEGIN shared level pipeline"
END = "// END shared level pipeline"
STREAM_LABELS = {"env", "service", "level", "game", "server_id"}
COLLECTED_NAMESPACES = {"rpg-k8s-realtime", "rpg-k8s-data", "rpg-k8s-logging"}
EXPECTED_RULES = [
    {"apiGroups": [""], "resources": ["pods"], "verbs": ["get", "list", "watch"]},
    {"apiGroups": [""], "resources": ["pods/log"], "verbs": ["get"]},
]


def _load_compose_tests_module():
    path = DEPLOY / "tests" / "test_log_shipping.py"
    spec = importlib.util.spec_from_file_location("compose_log_shipping_tests", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


CT = _load_compose_tests_module()


def manifests():
    """Every YAML document in k8s/logging/*.yaml as (file name, object)."""
    out = []
    for path in sorted(LOGGING.glob("*.yaml")):
        for doc in yaml.safe_load_all(path.read_text(encoding="utf-8")):
            if doc:
                out.append((path.name, doc))
    return out


def of_kind(kind):
    return [d for _, d in manifests() if d.get("kind") == kind]


def walk_keys(obj):
    if isinstance(obj, dict):
        for k, v in obj.items():
            yield k, v
            yield from walk_keys(v)
    elif isinstance(obj, list):
        for v in obj:
            yield from walk_keys(v)


def compose_level_block():
    lines = COMPOSE_ALLOY.read_text(encoding="utf-8").split("\n")
    begin = [i for i, line in enumerate(lines) if line.rstrip() == BEGIN]
    end = [i for i, line in enumerate(lines) if line.rstrip() == END]
    assert len(begin) == 1 and len(end) == 1 and begin[0] < end[0]
    return "\n".join(lines[begin[0]:end[0] + 1])


def generated_config():
    cm = [d for d in of_kind("ConfigMap") if d["metadata"]["name"] == "alloy-config"]
    assert len(cm) == 1
    return cm[0]["data"]["config.alloy"]


# --------------------------------------------------------------------------
# relabel evaluator: the one in backend/deploy/tests/test_log_shipping.py
# --------------------------------------------------------------------------

eval_expr = CT.eval_expr
relabel = CT.relabel
stream_labels = CT.stream_labels


# --------------------------------------------------------------------------
# tests
# --------------------------------------------------------------------------

class RbacTests(unittest.TestCase):
    def test_no_cluster_scoped_rbac_kind_exists(self):
        kinds = {d["kind"] for _, d in manifests()}
        self.assertFalse({k for k in kinds if k.startswith("Cluster")},
                         "a Cluster* kind exists under k8s/logging")
        for _, doc in manifests():
            self.assertNotIn("ClusterRole", doc.get("roleRef", {}).get("kind", ""))

    def test_each_collected_namespace_has_a_role_with_exactly_two_rules(self):
        roles = of_kind("Role")
        self.assertEqual({r["metadata"]["namespace"] for r in roles}, COLLECTED_NAMESPACES)
        self.assertEqual(len(roles), len(COLLECTED_NAMESPACES))
        for role in roles:
            self.assertEqual(role["metadata"]["name"], "rpg-log-collector")
            self.assertEqual(role["rules"], EXPECTED_RULES, role["metadata"]["namespace"])

    def test_role_bindings_bind_only_the_collector_service_account(self):
        bindings = of_kind("RoleBinding")
        self.assertEqual({b["metadata"]["namespace"] for b in bindings}, COLLECTED_NAMESPACES)
        for b in bindings:
            self.assertEqual(b["metadata"]["name"], "rpg-log-collector")
            self.assertEqual(b["subjects"], [{"kind": "ServiceAccount", "name": "rpg-log-collector",
                                              "namespace": "rpg-k8s-logging"}])
            self.assertEqual(b["roleRef"], {"apiGroup": "rbac.authorization.k8s.io", "kind": "Role",
                                            "name": "rpg-log-collector"})

    def test_nothing_grants_secrets_configmaps_nodes_events_or_a_wildcard(self):
        forbidden = {"secrets", "configmaps", "nodes", "events", "namespaces", "*"}
        for role in of_kind("Role"):
            for rule in role["rules"]:
                self.assertFalse(forbidden & set(rule["resources"]), rule)
                self.assertFalse({"*", "create", "update", "patch", "delete", "deletecollection"}
                                 & set(rule["verbs"]), rule)

    def test_service_account_is_in_the_logging_namespace(self):
        sas = of_kind("ServiceAccount")
        self.assertEqual([(s["metadata"]["name"], s["metadata"]["namespace"]) for s in sas],
                         [("rpg-log-collector", "rpg-k8s-logging")])

    def test_no_secret_is_referenced_or_defined(self):
        self.assertEqual(of_kind("Secret"), [])
        for k, _ in walk_keys([d for _, d in manifests()]):
            self.assertNotIn(k, {"secretKeyRef", "secretName", "secret"})


class DaemonSetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        sets = of_kind("DaemonSet")
        assert len(sets) == 1
        cls.ds = sets[0]
        cls.pod = cls.ds["spec"]["template"]["spec"]
        cls.container = cls.pod["containers"][0]

    def test_fixed_names(self):
        self.assertEqual(self.ds["metadata"]["name"], "alloy")
        self.assertEqual(self.ds["metadata"]["namespace"], "rpg-k8s-logging")
        self.assertEqual(self.ds["spec"]["template"]["metadata"]["labels"]["app.kubernetes.io/name"], "alloy")
        self.assertEqual(self.pod["serviceAccountName"], "rpg-log-collector")

    def test_no_host_access_and_nothing_privileged(self):
        for key, value in walk_keys(self.ds):
            self.assertNotIn(key, {"hostPath", "hostNetwork", "hostPID", "hostIPC", "hostPort"}, key)
            if key == "privileged":
                self.assertFalse(value)

    def test_security_context(self):
        sc = self.container["securityContext"]
        self.assertIs(sc["allowPrivilegeEscalation"], False)
        self.assertIs(sc["readOnlyRootFilesystem"], True)
        self.assertEqual(sc["capabilities"], {"drop": ["ALL"]})
        psc = self.pod["securityContext"]
        self.assertIs(psc["runAsNonRoot"], True)
        self.assertGreater(psc["runAsUser"], 0)
        self.assertEqual(psc["seccompProfile"], {"type": "RuntimeDefault"})

    def test_only_writable_volume_is_one_emptydir(self):
        volumes = {v["name"]: v for v in self.pod["volumes"]}
        empties = [n for n, v in volumes.items() if "emptyDir" in v]
        self.assertEqual(len(empties), 1)
        for name, vol in volumes.items():
            self.assertTrue(set(vol) - {"name"} <= {"emptyDir", "configMap"}, name)
        mounts = {m["name"]: m for m in self.container["volumeMounts"]}
        self.assertEqual({n for n, m in mounts.items() if not m.get("readOnly")}, set(empties))

    def test_image_is_pinned_to_the_compose_default(self):
        text = COMPOSE.read_text(encoding="utf-8")
        m = re.search(r"image:\s*grafana/alloy:\$\{ALLOY_VERSION:-(v[0-9.]+)\}", text)
        self.assertIsNotNone(m, "compose no longer pins grafana/alloy with a default")
        self.assertEqual(self.container["image"], f"grafana/alloy:{m.group(1)}")

    def test_environment(self):
        env = {e["name"]: e for e in self.container["env"]}
        self.assertEqual(env["NODE_NAME"]["valueFrom"]["fieldRef"]["fieldPath"], "spec.nodeName")
        for name, key in (("DEPLOY_ENVIRONMENT", "deploy-environment"), ("LOG_NAMESPACES", "log-namespaces")):
            ref = env[name]["valueFrom"]["configMapKeyRef"]
            self.assertEqual((ref["name"], ref["key"]), ("alloy-env", key))

    def test_alloy_env_defaults(self):
        cm = [d for d in of_kind("ConfigMap") if d["metadata"]["name"] == "alloy-env"]
        self.assertEqual(len(cm), 1)
        self.assertEqual(cm[0]["data"], {
            "deploy-environment": "local",
            "log-namespaces": "rpg-k8s-realtime,rpg-k8s-data,rpg-k8s-logging"})

    def test_readiness_probe_resources_and_no_service_for_the_ui(self):
        self.assertEqual(self.container["readinessProbe"]["httpGet"]["path"], "/-/ready")
        res = self.container["resources"]
        self.assertTrue({"cpu", "memory"} <= set(res["requests"]) and {"cpu", "memory"} <= set(res["limits"]))
        names = [s["metadata"]["name"] for s in of_kind("Service")]
        self.assertEqual(names, ["loki"])

    def test_namespace_manifest(self):
        ns = of_kind("Namespace")
        self.assertEqual([n["metadata"]["name"] for n in ns], ["rpg-k8s-logging"])


class LokiServiceTests(unittest.TestCase):
    def test_service_has_no_selector_and_publishes_3100(self):
        svc = [s for s in of_kind("Service") if s["metadata"]["name"] == "loki"][0]
        self.assertEqual(svc["metadata"]["namespace"], "rpg-k8s-logging")
        self.assertNotIn("selector", svc["spec"])
        self.assertEqual([p["port"] for p in svc["spec"]["ports"]], [3100])
        self.assertNotIn(svc["spec"].get("type", "ClusterIP"), {"NodePort", "LoadBalancer"})

    def test_no_endpointslice_or_endpoints_is_committed(self):
        # The address exists only after connect-loki.sh ran; only that script writes it.
        kinds = {d["kind"] for _, d in manifests()}
        self.assertFalse({"EndpointSlice", "Endpoints"} & kinds)


class GeneratedConfigTests(unittest.TestCase):
    def test_generated_manifest_is_in_sync(self):
        proc = subprocess.run([sys.executable, str(GENERATOR), "--check"],
                              capture_output=True, text=True)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)

    def test_check_detects_drift(self):
        text = GENERATED.read_text(encoding="utf-8")
        mutated = text.replace('level = "unknown"', 'level = "mutated"', 1)
        self.assertNotEqual(text, mutated)
        spec = importlib.util.spec_from_file_location("generate_config", GENERATOR)
        mod = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(mod)
        with tempfile.TemporaryDirectory() as tmp:
            target = pathlib.Path(tmp) / "alloy-config-drift-test.yaml"
            target.write_text(mutated, encoding="utf-8")
            mod.OUTPUT = target
            self.assertEqual(mod.main(["--check"]), 1)

    def test_generated_config_contains_the_compose_level_block_byte_for_byte(self):
        block = compose_level_block()
        self.assertIn('loki.process "level" {', block)
        self.assertIn(block, generated_config())

    def test_generated_file_says_it_is_generated_and_how(self):
        head = GENERATED.read_text(encoding="utf-8").split("apiVersion:")[0]
        self.assertIn("GENERATED", head)
        self.assertIn("generate-config.py", head)
        self.assertIn("--check", head)

    def test_configmap_identity(self):
        cm = [d for d in of_kind("ConfigMap") if d["metadata"]["name"] == "alloy-config"][0]
        self.assertEqual(cm["metadata"]["namespace"], "rpg-k8s-logging")
        self.assertEqual(list(cm["data"]), ["config.alloy"])

    def test_the_k8s_half_has_no_copy_of_the_level_stage(self):
        text = K8S_PART.read_text(encoding="utf-8")
        # A reference to the shared block's receiver is expected; a declaration
        # of the block itself would be a fork.
        self.assertNotIn('loki.process "', text)
        self.assertNotIn("stage.", text)
        self.assertEqual(text.count("// @@SHARED_LEVEL_PIPELINE@@"), 1)


class ConfigStructureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.blocks, _ = CT.parse_alloy(generated_config())
        cls.relabel = CT.component(cls.blocks, "discovery.relabel", "pods")
        cls.rules = CT.rules_of(cls.relabel)

    def test_components_are_defined_once_and_wired(self):
        names = [b.name for b in self.blocks]
        for expected in ("discovery.kubernetes.pods", "discovery.relabel.pods",
                         "loki.source.kubernetes.pods", "loki.process.level", "loki.write.lgtm"):
            self.assertEqual(names.count(expected), 1, expected)
        source = CT.component(self.blocks, "loki.source.kubernetes", "pods")
        self.assertEqual(CT.idents_in(source.attrs["targets"]), ["discovery.relabel.pods.output"])
        self.assertEqual(CT.idents_in(source.attrs["forward_to"]), ["loki.process.level.receiver"])
        process = CT.component(self.blocks, "loki.process", "level")
        self.assertEqual(CT.idents_in(process.attrs["forward_to"]), ["loki.write.lgtm.receiver"])
        self.assertEqual(CT.idents_in(self.relabel.attrs["targets"]), ["discovery.kubernetes.pods.targets"])

    def test_every_component_reference_resolves(self):
        defined = {b.name for b in CT.walk(self.blocks)}
        for block in CT.walk(self.blocks):
            for expr in block.attrs.values():
                for ident in CT.idents_in(expr):
                    m = re.match(r"((?:discovery|loki)\.[a-z]+\.[a-z]+)\.", ident)
                    if m:
                        self.assertIn(m.group(1), defined, ident)

    def test_discovery_is_limited_to_the_listed_namespaces_and_this_node(self):
        disco = CT.component(self.blocks, "discovery.kubernetes", "pods")
        self.assertEqual(CT.strings_in(disco.attrs["role"]), ["pod"])
        ns = [c for c in disco.children if c.kind == "namespaces"][0]
        # string.split(sys.env("LOG_NAMESPACES"), ",")
        self.assertEqual(CT.strings_in(ns.attrs["names"]), ["LOG_NAMESPACES", ","])
        sel = [c for c in disco.children if c.kind == "selectors"][0]
        self.assertEqual(CT.strings_in(sel.attrs["field"])[0], "spec.nodeName=")
        self.assertIn(("str", "NODE_NAME"), sel.attrs["field"])
        self.assertNotIn("kubeconfig_file", disco.attrs)
        self.assertNotIn("api_server", disco.attrs)

    def test_push_goes_to_the_loki_service_of_the_logging_namespace(self):
        write = CT.component(self.blocks, "loki.write", "lgtm")
        urls = [CT.strings_in(c.attrs["url"])[0] for c in write.children if c.kind == "endpoint"]
        self.assertEqual(urls, ["http://loki.rpg-k8s-logging.svc.cluster.local:3100/loki/api/v1/push"])

    def test_namespace_keep_rule_is_anchored_to_rpg_k8s(self):
        keeps = [r for r in self.rules if CT.rule_value(r, "action") == "keep"]
        by_source = {tuple(CT.strings_in(r.attrs["source_labels"])): r for r in keeps}
        ns_rule = by_source[("__meta_kubernetes_namespace",)]
        self.assertEqual(CT.rule_value(ns_rule, "regex"), "rpg-k8s-.+")
        self.assertIn(("__meta_kubernetes_pod_node_name",), by_source)

    def test_only_contract_labels_are_set_and_none_from_unbounded_sources(self):
        for rule in self.rules:
            if CT.rule_value(rule, "action") in (None, "replace"):
                self.assertIn(CT.rule_value(rule, "target_label"), {"env", "service", "game", "server_id"})
            for src in CT.strings_in(rule.attrs.get("source_labels", [])):
                for bad in ("uid", "container_id", "_ip", "annotation", "host_ip"):
                    self.assertNotIn(bad, src)
        # the shared stage keeps exactly the contract
        process = CT.component(self.blocks, "loki.process", "level")
        keep = [c for c in process.children if c.kind == "stage.label_keep"][0]
        self.assertEqual(set(CT.strings_in(keep.attrs["values"])), STREAM_LABELS)

    def test_the_whole_config_balances(self):
        CT.check_balance(CT.tokenize(generated_config()))


ENV = {"NODE_NAME": "k3d-rpg-dev-server-0", "DEPLOY_ENVIRONMENT": "local"}


def pod_target(namespace, container, pod, role=None, node="k3d-rpg-dev-server-0", controller=None):
    t = {
        "__meta_kubernetes_namespace": namespace,
        "__meta_kubernetes_pod_name": pod,
        "__meta_kubernetes_pod_container_name": container,
        "__meta_kubernetes_pod_node_name": node,
        "__meta_kubernetes_pod_uid": "uid-" + pod,
        "__meta_kubernetes_pod_ip": "10.42.0.9",
    }
    if role:
        t["__meta_kubernetes_pod_label_agones_dev_role"] = role
    if controller:
        t["__meta_kubernetes_pod_controller_kind"] = "StatefulSet"
        t["__meta_kubernetes_pod_controller_name"] = controller
    return t


class LabelRuleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        blocks, _ = CT.parse_alloy(generated_config())
        cls.rules = CT.rules_of(CT.component(blocks, "discovery.relabel", "pods"))

    def labels(self, target, env=ENV):
        return stream_labels(self.rules, target, env)

    def test_default_game_server_is_labelled_cuvara_with_its_pod_name_as_server_id(self):
        pod = "map-servers-dotnet-k8s-7xk2q-abcde"
        self.assertEqual(
            self.labels(pod_target("rpg-k8s-realtime", "gameserver", pod, "gameserver")),
            {"env": "local", "service": "gameserver", "game": "cuvara", "server_id": pod})

    def test_agones_sidecar_of_a_game_server_pod_carries_the_same_game_and_server_id(self):
        pod = "map-servers-dotnet-k8s-7xk2q-abcde"
        self.assertEqual(
            self.labels(pod_target("rpg-k8s-realtime", "agones-gameserver-sidecar", pod, "gameserver")),
            {"env": "local", "service": "agones-gameserver-sidecar", "game": "cuvara", "server_id": pod})

    def test_rendered_game_namespace_gets_its_own_game_label(self):
        pod = "ember-map-servers-dotnet-k8s-9f3zt-qwert"
        self.assertEqual(
            self.labels(pod_target("rpg-k8s-realtime-ember", "gameserver", pod, "gameserver")),
            {"env": "local", "service": "gameserver", "game": "ember", "server_id": pod})
        self.assertEqual(
            self.labels(pod_target("rpg-k8s-realtime-ember", "gameserver", pod, "gameserver"))["game"], "ember")

    def test_a_game_id_with_a_dash_or_digits_is_taken_whole(self):
        pod = "dark-realm2-map-servers-dotnet-k8s-1-x"
        got = self.labels(pod_target("rpg-k8s-realtime-dark-realm2", "gameserver", pod, "gameserver"))
        self.assertEqual(got["game"], "dark-realm2")

    def test_gateway_shares_the_default_namespace_but_has_no_game_or_server_id(self):
        got = self.labels(pod_target("rpg-k8s-realtime", "gateway", "gateway-6d9c7b8f5-x2x2x"))
        self.assertEqual(got, {"env": "local", "service": "gateway"})

    def test_data_namespace_has_no_game_or_server_id(self):
        for container, pod, controller, service in (
                ("nakama", "nakama-5c7d-abc", None, "nakama"),
                ("migrate", "nakama-5c7d-abc", None, "migrate"),
                ("redis", "redis-0", "redis", "redis"),
                ("postgres", "postgres-meta-0", "postgres-meta", "postgres"),
                ("postgres", "postgres-game-0", "postgres-game", "postgres-game")):
            got = self.labels(pod_target("rpg-k8s-data", container, pod, controller=controller))
            self.assertEqual(got, {"env": "local", "service": service}, f"{container} {pod}")

    def test_the_collector_itself_has_no_game(self):
        got = self.labels(pod_target("rpg-k8s-logging", "alloy", "alloy-abcde"))
        self.assertEqual(got, {"env": "local", "service": "alloy"})

    def test_a_pod_of_a_game_namespace_without_the_agones_role_has_no_game(self):
        got = self.labels(pod_target("rpg-k8s-realtime-ember", "gateway-like", "something-1"))
        self.assertEqual(got, {"env": "local", "service": "gateway-like"})

    def test_other_namespaces_are_never_shipped(self):
        for ns in ("kube-system", "rpg-realtime", "rpg-k8s", "default", "agones-system", "xrpg-k8s-data"):
            self.assertIsNone(self.labels(pod_target(ns, "c", "p")), ns)

    def test_pods_of_another_node_are_dropped(self):
        self.assertIsNone(self.labels(pod_target("rpg-k8s-data", "redis", "redis-0", node="other-node")))

    def test_env_comes_from_the_deploy_environment_variable(self):
        env = dict(ENV, DEPLOY_ENVIRONMENT="staging")
        self.assertEqual(self.labels(pod_target("rpg-k8s-data", "redis", "redis-0"), env)["env"], "staging")


def manifest_containers():
    """Container and initContainer names of every workload manifest of the tree."""
    names = set()

    def walk(obj):
        if isinstance(obj, dict):
            for key, value in obj.items():
                if key in ("containers", "initContainers") and isinstance(value, list):
                    names.update(c["name"] for c in value if isinstance(c, dict) and "name" in c)
                walk(value)
        elif isinstance(obj, list):
            for item in obj:
                walk(item)

    for sub in ("app", "data", "logging"):
        for path in sorted((DEPLOY / "k8s" / sub).rglob("*.yaml")):
            if "proof" in path.parts:
                continue
            for doc in yaml.safe_load_all(path.read_text(encoding="utf-8")):
                walk(doc)
    return names


class CanonicalServiceTests(unittest.TestCase):
    """docs/MONITORING.md, "Canonical service values", pinned on the k8s side."""

    # Added to every game server pod by Agones, so no manifest of this repository names it.
    NOT_IN_MANIFESTS = {"agones-gameserver-sidecar"}
    EXPECTED = {
        "gameserver": [("gameserver", None)],
        "gateway": [("gateway", None)],
        "nakama": [("nakama", None)],
        "redis": [("redis", None)],
        "postgres": [("postgres", "postgres-meta")],
        "postgres-game": [("postgres", "postgres-game")],
        "alloy": [("alloy", None)],
    }
    NAMESPACE = {"alloy": "rpg-k8s-logging", "gameserver": "rpg-k8s-realtime",
                 "agones-gameserver-sidecar": "rpg-k8s-realtime", "gateway": "rpg-k8s-realtime"}

    @classmethod
    def setUpClass(cls):
        cls.table = CT.canonical_service_table()
        blocks, _ = CT.parse_alloy(generated_config())
        cls.rules = CT.rules_of(CT.component(blocks, "discovery.relabel", "pods"))

    def test_the_table_is_pinned_to_the_documented_values(self):
        """The core roles and their k8s sources: changing one is a contract change."""
        k8s = {row["service"]: row["k8s"] for row in self.table if row["k8s"]}
        for service, source in self.EXPECTED.items():
            self.assertEqual(k8s.get(service), source, service)

    def test_every_container_of_the_manifests_has_a_row(self):
        listed = {c for row in self.table for c, _ in row["k8s"]}
        self.assertEqual(manifest_containers() - listed, set(),
                         "a container with no row in the canonical service table")
        self.assertEqual(listed - manifest_containers(), self.NOT_IN_MANIFESTS,
                         "a row names a k8s container that no manifest defines")

    def test_each_row_is_what_the_collector_produces(self):
        for row in self.table:
            for container, controller in row["k8s"]:
                with self.subTest(service=row["service"], container=container, controller=controller):
                    role = "gameserver" if container in ("gameserver", "agones-gameserver-sidecar") else None
                    pod = f"{controller}-0" if controller else f"{container}-0"
                    target = pod_target(self.NAMESPACE.get(container, "rpg-k8s-data"), container, pod, role,
                                        controller=controller)
                    self.assertEqual(stream_labels(self.rules, target, ENV)["service"], row["service"])

    def test_two_game_servers_of_one_game_share_service_and_differ_in_server_id(self):
        a, b = "map-servers-dotnet-k8s-aaaaa-11111", "map-servers-dotnet-k8s-aaaaa-22222"
        la = stream_labels(self.rules, pod_target("rpg-k8s-realtime", "gameserver", a, "gameserver"), ENV)
        lb = stream_labels(self.rules, pod_target("rpg-k8s-realtime", "gameserver", b, "gameserver"), ENV)
        self.assertEqual((la["service"], la["server_id"], lb["server_id"]), ("gameserver", a, b))
        self.assertEqual(lb["service"], "gameserver")

    def test_the_two_databases_have_different_service_values_and_no_game(self):
        meta = stream_labels(self.rules, pod_target("rpg-k8s-data", "postgres", "postgres-meta-0",
                                                    controller="postgres-meta"), ENV)
        game = stream_labels(self.rules, pod_target("rpg-k8s-data", "postgres", "postgres-game-0",
                                                    controller="postgres-game"), ENV)
        self.assertEqual((meta["service"], game["service"]), ("postgres", "postgres-game"))
        self.assertEqual(set(game), {"env", "service"})


if __name__ == "__main__":
    unittest.main()
