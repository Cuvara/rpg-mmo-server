import contextlib
import importlib.util
import io
import pathlib
import shlex
import unittest

path = pathlib.Path(__file__).resolve().parent / "render.py"
spec = importlib.util.spec_from_file_location("rpg_mmo_games_render", path)
renderer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(renderer)


def config(result):
    return next(d["data"] for d in result["manifests"]
                if d["kind"] == "ConfigMap" and d["metadata"]["name"] == "gameserver-config")


class RenderTlsTests(unittest.TestCase):
    def test_dev_up_secret_template_command_runs(self):
        dev_up = (path.parent.parent / "dev-up.sh").read_text(encoding="utf-8")
        commands = [line.strip().removeprefix('echo "').removesuffix('" >&2')
                    for line in dev_up.splitlines()
                    if "render.py --game" in line and "--print secret-template" in line]
        self.assertEqual(len(commands), 1)
        args = shlex.split(commands[0].replace("$game", "ember"))[2:]
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(renderer.main(args), 0)
        self.assertIn("kind: Secret", output.getvalue())

    def test_operator_examples_include_tls_choice(self):
        deploy = path.parent.parent.parent
        examples = (
            (path.parent.parent / "dev-up.sh", 1),
            (deploy / "docs/RUNBOOK-local-dev.md", 1),
            (path.parent.parent / "README.md", 1),
            (path.parent.parent / "app/40-gateway.yaml", 1),
        )
        for file, count in examples:
            lines = [line for line in file.read_text(encoding="utf-8").splitlines()
                     if "render.py --game" in line and "--print" in line]
            with self.subTest(file=file):
                self.assertEqual(len(lines), count)
                for line in lines:
                    self.assertRegex(line, r"--nakama-tls (?:on|off)(?=\s|`|$)")

    def test_cli_snippets_do_not_require_tls_decision(self):
        for part in ("secret-template", "gateway-env"):
            with self.subTest(part=part), contextlib.redirect_stdout(io.StringIO()) as output:
                self.assertEqual(renderer.main(["--game", "ember", "--print", part]), 0)
                self.assertIn("ember", output.getvalue())

    def test_tls_on_and_off_preserve_other_config(self):
        source = renderer._one(renderer._load("20-configmaps.yaml"), "ConfigMap", "gameserver-config")["data"]
        for enabled, scheme, pin in ((True, "https", "/etc/nakama-tls/tls.crt"), (False, "http", "")):
            with self.subTest(tls=enabled):
                result = renderer.render("ember", nakama_tls=enabled)
                data = config(result)
                self.assertEqual(data["nakama-url"], scheme + "://" + source["nakama-url"].split("://", 1)[1])
                self.assertEqual(data["nakama-tls-pin"], pin)
                self.assertEqual({k: v for k, v in data.items() if k not in ("game-id", "nakama-url", "nakama-tls-pin")},
                                 {k: v for k, v in source.items() if k not in ("game-id", "nakama-url", "nakama-tls-pin")})
                for fleet in (d for d in result["manifests"] if d["kind"] == "Fleet"):
                    pod = fleet["spec"]["template"]["spec"]["template"]["spec"]
                    self.assertTrue(any(v.get("secret", {}).get("secretName") == "nakama-tls-pin" for v in pod["volumes"]))
                    self.assertTrue(any(m["name"] == "nakama-tls-pin" for c in pod["containers"] for m in c.get("volumeMounts", [])))

    def test_log_knobs_follow_app_defaults(self):
        source = renderer._one(renderer._load("20-configmaps.yaml"), "ConfigMap", "gameserver-config")["data"]
        self.assertEqual(source["log-level"], "Information")
        self.assertEqual(source["log-format"], "json")
        result = renderer.render("ember")
        data = config(result)
        self.assertEqual(data["log-level"], source["log-level"])
        self.assertEqual(data["log-format"], source["log-format"])
        fleets = [d for d in result["manifests"] if d["kind"] == "Fleet"]
        self.assertEqual(len(fleets), 2)
        for fleet in fleets:
            env = {e["name"]: e for c in fleet["spec"]["template"]["spec"]["template"]["spec"]["containers"]
                   for e in c.get("env", [])}
            for name, key in (("GAMESERVER_LOG_LEVEL", "log-level"), ("GAMESERVER_LOG_FORMAT", "log-format")):
                with self.subTest(fleet=fleet["metadata"]["name"], env=name):
                    self.assertEqual(env[name]["valueFrom"]["configMapKeyRef"],
                                     {"name": "gameserver-config", "key": key, "optional": True})

    def test_log_collector_grant_is_exactly_read_pods_and_logs(self):
        result = renderer.render("ember")
        ns = "rpg-k8s-realtime-ember"
        roles = [d for d in result["manifests"]
                 if d["kind"] == "Role" and d["metadata"]["name"] == "rpg-log-collector"]
        bindings = [d for d in result["manifests"]
                    if d["kind"] == "RoleBinding" and d["metadata"]["name"] == "rpg-log-collector"]
        self.assertEqual(len(roles), 1)
        self.assertEqual(len(bindings), 1)
        self.assertEqual(roles[0]["metadata"]["namespace"], ns)
        self.assertEqual(roles[0]["rules"], [
            {"apiGroups": [""], "resources": ["pods"], "verbs": ["get", "list", "watch"]},
            {"apiGroups": [""], "resources": ["pods/log"], "verbs": ["get"]},
        ])
        self.assertEqual(bindings[0]["metadata"]["namespace"], ns)
        self.assertEqual(bindings[0]["subjects"], [
            {"kind": "ServiceAccount", "name": "rpg-log-collector", "namespace": "rpg-k8s-logging"}])
        self.assertEqual(bindings[0]["roleRef"],
                         {"apiGroup": "rbac.authorization.k8s.io", "kind": "Role", "name": "rpg-log-collector"})
        # Nothing cluster-wide is rendered for the collector.
        self.assertFalse([d for d in result["manifests"] if d["kind"] in ("ClusterRole", "ClusterRoleBinding")])

    def test_mismatches_are_refused(self):
        cases = [
            (True, "http://nakama:7350", None), (True, None, ""),
            (True, None, "relative.crt"), (False, "https://nakama:7350", None),
            (False, None, "/pin"), (True, "ftp://nakama", None),
            (True, "https:///path", None), (True, "https://naka ma", None),
            (True, None, "/path with space"),
        ]
        for tls, url, pin in cases:
            with self.subTest(tls=tls, url=url, pin=pin):
                with self.assertRaises(renderer.RenderError):
                    renderer.render("ember", nakama_tls=tls, nakama_url=url, nakama_tls_pin=pin)

    def test_cli_requires_decision_and_refuses_mismatch(self):
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as exc:
                renderer.main(["--game", "ember"])
            self.assertNotEqual(exc.exception.code, 0)
            with self.assertRaises(SystemExit) as exc:
                renderer.main(["--game", "ember", "--print", "secret-template", "--out", "unused-output"])
            self.assertNotEqual(exc.exception.code, 0)
            self.assertEqual(renderer.main(["--game", "ember", "--nakama-tls", "on", "--nakama-url", "http://nakama"]), 2)

    def test_header_records_decision_and_overrides(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(renderer.main(["--game", "ember", "--nakama-tls", "on", "--nakama-url", "https://nakama", "--nakama-tls-pin", "/custom.crt"]), 0)
        header = output.getvalue()
        for expected in ("--nakama-tls on", "--nakama-url https://nakama", "--nakama-tls-pin /custom.crt", "nakama-tls-pin", "dev-up.sh", "kubectl"):
            self.assertIn(expected, header)


if __name__ == "__main__":
    unittest.main()
