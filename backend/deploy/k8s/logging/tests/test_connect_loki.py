"""Offline tests of k8s/logging/connect-loki.sh with a fake `docker` and `kubectl`.

No cluster, no Docker. The script runs under bash with two fake executables first
on PATH; they log every call and keep their state in a temp directory:

    python -m unittest discover -s backend/deploy/k8s/logging/tests -p "test_*.py"

Covered: it attaches once, is idempotent, never runs a `docker inspect` without a
network-settings --format (and never prints a container's environment), publishes
no port, warns and exits 0 when `lgtm` is absent while removing a stale
EndpointSlice, leaves a working EndpointSlice alone (WARN, exit 0, no kubectl)
when `docker ps` itself could not answer (empty output, non-zero status, or
docker missing), tolerates the WSL docker shim that returns non-zero after
succeeding (a non-empty listing is an answer whatever the status), resolves the container from environments.tsv, and `--dry-run`
changes nothing.
"""

import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest

HERE = pathlib.Path(__file__).resolve().parent
LOGGING = HERE.parent
SCRIPT = LOGGING / "connect-loki.sh"

FAKE_DOCKER = r"""#!/usr/bin/env bash
# fake docker: logs the call, keeps state in $FAKE_STATE
echo "docker $*" >> "$FAKE_STATE/calls.log"
case "$1" in
  ps)
    # docker could not answer: no output at all, non-zero status (daemon down,
    # or 127 as when the binary is missing)
    if [ -f "$FAKE_STATE/ps_fails" ]; then exit "$(cat "$FAKE_STATE/ps_fails")"; fi
    if [ -f "$FAKE_STATE/running" ]; then
      echo "$(cat "$FAKE_STATE/running")"
    fi
    echo "some-other-container"
    # the WSL shim: the listing was printed, the status is still non-zero
    if [ -f "$FAKE_STATE/ps_shim_nonzero" ]; then exit 1; fi
    ;;
  inspect)
    case "$*" in
      *--format*.NetworkSettings*)
        if [ -f "$FAKE_STATE/connected" ]; then cat "$FAKE_STATE/address"; fi
        ;;
      *)
        # a plain / Config inspect would leak the environment: fail loudly
        echo "Env: [JWT_SECRET=hunter2-leak]"
        ;;
    esac
    ;;
  network)
    if [ "$2" = "connect" ]; then
      if [ -f "$FAKE_STATE/connect_fails" ]; then exit 1; fi
      touch "$FAKE_STATE/connected"
      if [ -f "$FAKE_STATE/shim_nonzero" ]; then exit 1; fi
    fi
    ;;
esac
exit 0
"""

FAKE_KUBECTL = r"""#!/usr/bin/env bash
# fake kubectl: logs the call and its stdin, keeps the EndpointSlice in a file
echo "kubectl $*" >> "$FAKE_STATE/calls.log"
case "$*" in
  *"apply -f -"*)
    cat > "$FAKE_STATE/slice.yaml"
    if [ -f "$FAKE_STATE/apply_fails" ]; then exit 1; fi
    ;;
  *"delete endpointslice"*)
    rm -f "$FAKE_STATE/slice.yaml"
    ;;
esac
exit 0
"""

TSV = (
    "# comment\n"
    "environment\tdeploy_dir\tcompose_project\tname_prefix\tports\n"
    "dev\t/d\trpg-mmo-meta\trpg\t3100\n"
    "staging\t/s\trpg-mmo-staging\trpg-stg\t3120\n"
)


def find_bash():
    if os.name == "nt":
        for cand in (r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files\Git\usr\bin\bash.exe"):
            if os.path.exists(cand):
                return cand
        return None  # the WSL launcher on PATH would run the fakes in another world
    return shutil.which("bash")


BASH = find_bash()


@unittest.skipUnless(BASH, "bash is not available")
class ConnectLokiTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = pathlib.Path(self.tmp.name)
        self.state = root / "state"
        self.state.mkdir()
        bindir = root / "bin"
        bindir.mkdir()
        for name, body in (("docker", FAKE_DOCKER), ("kubectl", FAKE_KUBECTL)):
            path = bindir / name
            path.write_text(body, encoding="utf-8", newline="\n")
            path.chmod(0o755)
        self.bindir = bindir
        (root / "environments.tsv").write_text(TSV, encoding="utf-8", newline="\n")
        self.tsv = root / "environments.tsv"
        (self.state / "address").write_text("172.18.0.5", encoding="utf-8")

    # -- helpers ---------------------------------------------------------------

    def posix(self, path):
        return pathlib.Path(path).as_posix()

    def run_script(self, *args, env=None, running="rpg-lgtm"):
        if running:
            (self.state / "running").write_text(running, encoding="utf-8")
        else:
            (self.state / "running").unlink(missing_ok=True)
        base = {k: v for k, v in os.environ.items()
                if k not in ("KUBE_CONTEXT", "DEPLOY_ENVIRONMENT", "LOKI_CONTAINER", "LOKI_NETWORK")}
        base["PATH"] = self.posix(self.bindir) + os.pathsep + base.get("PATH", "")
        base["FAKE_STATE"] = self.posix(self.state)
        base["ENVIRONMENTS_TSV"] = self.posix(self.tsv)
        base.update(env or {})
        return subprocess.run([BASH, self.posix(SCRIPT), *args], capture_output=True, text=True, env=base)

    def calls(self):
        log = self.state / "calls.log"
        return log.read_text(encoding="utf-8").splitlines() if log.exists() else []

    def connect_calls(self):
        return [c for c in self.calls() if c.startswith("docker network connect")]

    @property
    def slice(self):
        return self.state / "slice.yaml"

    # -- tests -------------------------------------------------------------------

    def test_syntax_is_valid(self):
        proc = subprocess.run([BASH, "-n", self.posix(SCRIPT)], capture_output=True, text=True)
        self.assertEqual(proc.returncode, 0, proc.stderr)

    def test_attaches_once_and_writes_the_endpointslice(self):
        proc = self.run_script()
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(self.connect_calls(), ["docker network connect k3d-rpg-dev rpg-lgtm"])
        text = self.slice.read_text(encoding="utf-8")
        self.assertIn("kind: EndpointSlice", text)
        self.assertIn("name: loki-lgtm", text)
        self.assertIn("namespace: rpg-k8s-logging", text)
        self.assertIn("kubernetes.io/service-name: loki", text)
        self.assertIn('"172.18.0.5"', text)
        self.assertIn("port: 3100", text)
        self.assertIn("kubectl --context k3d-rpg-dev apply -f -", self.calls())

    def test_second_run_does_not_attach_again(self):
        self.assertEqual(self.run_script().returncode, 0)
        first = self.slice.read_text(encoding="utf-8")
        proc = self.run_script()
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(len(self.connect_calls()), 1)
        self.assertEqual(self.slice.read_text(encoding="utf-8"), first)

    def test_never_inspects_without_a_network_settings_format_and_never_prints_env(self):
        proc = self.run_script()
        self.run_script("--dry-run")
        inspects = [c for c in self.calls() if c.startswith("docker inspect")]
        self.assertTrue(inspects)
        for call in inspects:
            self.assertIn("--format", call)
            self.assertIn(".NetworkSettings", call)
            self.assertNotIn(".Config", call)
            self.assertNotIn(".Env", call)
        self.assertNotIn("hunter2-leak", proc.stdout + proc.stderr)
        text = SCRIPT.read_text(encoding="utf-8")
        for line in text.splitlines():
            if line.lstrip().startswith("#"):
                continue
            if "docker inspect" in line:
                self.assertTrue("--format" in line or line.rstrip().endswith("\\"), line)

    def test_publishes_no_port_and_changes_no_binding(self):
        self.run_script()
        for call in self.calls():
            if call.startswith("docker "):
                self.assertRegex(call, r"^docker (ps|inspect|network connect)\b", call)
            self.assertNotRegex(call, r"(^| )(-p|--publish|--publish-all|-P)( |$)")
        text = SCRIPT.read_text(encoding="utf-8")
        code = "\n".join(l for l in text.splitlines() if not l.lstrip().startswith("#"))
        for forbidden in ("docker run", "docker update", "docker port", "--publish", "docker stop", "docker rm"):
            self.assertNotIn(forbidden, code)

    def test_a_docker_shim_that_fails_after_succeeding_is_not_a_failure(self):
        (self.state / "shim_nonzero").write_text("", encoding="utf-8")
        proc = self.run_script()
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertIn('"172.18.0.5"', self.slice.read_text(encoding="utf-8"))

    def test_absent_lgtm_warns_exits_zero_and_removes_the_stale_slice(self):
        self.slice.write_text("stale: 10.0.0.9\n", encoding="utf-8")
        proc = self.run_script(running=None)
        self.assertEqual(proc.returncode, 0)
        self.assertIn("WARN", proc.stderr)
        self.assertIn("connect-loki.sh", proc.stderr)
        self.assertIn("re-run", proc.stderr.lower())
        self.assertFalse(self.slice.exists(), "a stale EndpointSlice was left behind")
        self.assertEqual(self.connect_calls(), [])
        self.assertTrue(any("delete endpointslice loki-lgtm" in c for c in self.calls()))

    def assert_slice_left_alone(self, proc):
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertIn("WARN", proc.stderr)
        self.assertIn("docker could not be asked", proc.stderr)
        self.assertIn("EndpointSlice", proc.stderr)
        self.assertIn("left as it is", proc.stderr)
        self.assertTrue(self.slice.exists(), "a working EndpointSlice was deleted")
        self.assertEqual(self.slice.read_text(encoding="utf-8"), "working: 172.18.0.5\n")
        self.assertEqual(self.connect_calls(), [])
        self.assertEqual([c for c in self.calls() if c.startswith("kubectl")], [],
                         "kubectl must not be called when docker could not be asked")

    def test_docker_ps_failing_without_output_leaves_the_slice_alone(self):
        (self.state / "ps_fails").write_text("1", encoding="utf-8")
        self.slice.write_text("working: 172.18.0.5\n", encoding="utf-8")
        self.assert_slice_left_alone(self.run_script())

    def test_docker_missing_leaves_the_slice_alone(self):
        # a missing binary answers 127 with nothing on stdout
        (self.state / "ps_fails").write_text("127", encoding="utf-8")
        self.slice.write_text("working: 172.18.0.5\n", encoding="utf-8")
        self.assert_slice_left_alone(self.run_script())

    def test_docker_ps_failing_in_dry_run_changes_and_deletes_nothing(self):
        (self.state / "ps_fails").write_text("1", encoding="utf-8")
        self.slice.write_text("working: 172.18.0.5\n", encoding="utf-8")
        proc = self.run_script("--dry-run")
        self.assert_slice_left_alone(proc)

    def test_a_nonempty_listing_with_a_nonzero_status_is_an_answer(self):
        # the WSL shim printed the list and still exited non-zero: lgtm is listed,
        # so it is running and the slice is written
        (self.state / "ps_shim_nonzero").write_text("", encoding="utf-8")
        proc = self.run_script()
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertNotIn("could not be asked", proc.stderr)
        self.assertIn('"172.18.0.5"', self.slice.read_text(encoding="utf-8"))

    def test_a_nonempty_listing_without_lgtm_and_a_nonzero_status_still_deletes_the_slice(self):
        # docker answered (the list is there), lgtm is not in it: that is "stopped"
        (self.state / "ps_shim_nonzero").write_text("", encoding="utf-8")
        self.slice.write_text("stale: 10.0.0.9\n", encoding="utf-8")
        proc = self.run_script(running=None)
        self.assertEqual(proc.returncode, 0)
        self.assertNotIn("could not be asked", proc.stderr)
        self.assertFalse(self.slice.exists())

    def test_lgtm_with_a_different_name_counts_as_absent(self):
        proc = self.run_script(running="someone-elses-lgtm")
        self.assertEqual(proc.returncode, 0)
        self.assertIn("WARN", proc.stderr)
        self.assertEqual(self.connect_calls(), [])

    def test_failed_attach_warns_exits_zero_and_leaves_no_slice(self):
        (self.state / "connect_fails").write_text("", encoding="utf-8")
        self.slice.write_text("stale: 10.0.0.9\n", encoding="utf-8")
        proc = self.run_script()
        self.assertEqual(proc.returncode, 0)
        self.assertIn("WARN", proc.stderr)
        self.assertFalse(self.slice.exists())

    def test_kubectl_apply_failure_is_an_error(self):
        (self.state / "apply_fails").write_text("", encoding="utf-8")
        proc = self.run_script()
        self.assertEqual(proc.returncode, 1)
        self.assertIn("EndpointSlice", proc.stderr)

    def test_container_name_comes_from_environments_tsv(self):
        self.run_script()
        self.assertEqual(self.connect_calls(), ["docker network connect k3d-rpg-dev rpg-lgtm"])
        (self.state / "connected").unlink()
        (self.state / "calls.log").unlink()
        proc = self.run_script(env={"KUBE_CONTEXT": "k3d-rpg-stg"}, running="rpg-stg-lgtm")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(self.connect_calls(), ["docker network connect k3d-rpg-stg rpg-stg-lgtm"])
        self.assertIn("kubectl --context k3d-rpg-stg apply -f -", self.calls())

    def test_explicit_variables_override_the_derivation(self):
        proc = self.run_script(env={"KUBE_CONTEXT": "k3d-other", "DEPLOY_ENVIRONMENT": "dev",
                                    "LOKI_CONTAINER": "mine-lgtm", "LOKI_NETWORK": "my-net"},
                               running="mine-lgtm")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertEqual(self.connect_calls(), ["docker network connect my-net mine-lgtm"])

    def test_unknown_context_without_variables_warns_exits_zero_and_attaches_nothing(self):
        proc = self.run_script(env={"KUBE_CONTEXT": "minikube"})
        self.assertEqual(proc.returncode, 0)
        self.assertIn("WARN", proc.stderr)
        self.assertEqual(self.connect_calls(), [])
        self.assertFalse(self.slice.exists())
        self.assertEqual([c for c in self.calls() if c.startswith("kubectl")], [])

    def test_odd_characters_in_names_are_refused(self):
        proc = self.run_script(env={"LOKI_CONTAINER": "x;touch pwned", "LOKI_NETWORK": "n"})
        self.assertEqual(proc.returncode, 0)
        self.assertIn("WARN", proc.stderr)
        self.assertEqual(self.calls(), [])

    def test_dry_run_prints_the_plan_and_changes_nothing(self):
        proc = self.run_script("--dry-run")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertIn("[dry-run]", proc.stdout)
        self.assertIn("docker network connect k3d-rpg-dev rpg-lgtm", proc.stdout)
        self.assertEqual(self.connect_calls(), [])
        self.assertFalse(self.slice.exists())
        self.assertEqual([c for c in self.calls() if c.startswith("kubectl")], [])

    def test_dry_run_with_lgtm_absent_deletes_nothing(self):
        proc = self.run_script("--dry-run", running=None)
        self.assertEqual(proc.returncode, 0)
        self.assertEqual([c for c in self.calls() if c.startswith("kubectl")], [])

    def test_unknown_argument_is_refused(self):
        proc = self.run_script("--bogus")
        self.assertEqual(proc.returncode, 2)


if __name__ == "__main__":
    unittest.main()
