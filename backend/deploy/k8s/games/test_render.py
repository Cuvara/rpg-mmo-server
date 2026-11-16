import contextlib
import importlib.util
import io
import pathlib
import unittest

path = pathlib.Path(__file__).resolve().parent / "render.py"
spec = importlib.util.spec_from_file_location("rpg_mmo_games_render", path)
renderer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(renderer)


def config(result):
    return next(d["data"] for d in result["manifests"]
                if d["kind"] == "ConfigMap" and d["metadata"]["name"] == "gameserver-config")


class RenderTlsTests(unittest.TestCase):
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
