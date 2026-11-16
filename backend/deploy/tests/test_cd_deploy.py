"""Offline tests of two deploy-safety properties of .github/workflows/cd.yml.

No Docker, no runner, no network. Python 3.12 + PyYAML; bash for the stubbed runs:

    python -m unittest discover -s backend/deploy/tests -p "test_*.py"

1. Loki port per environment (QA finding M1).
   environments.tsv reserves a Loki port for each environment, and the isolation
   preflight refuses a deploy that resolves another environment's port. A GitHub
   Environment variable that nobody has set yet must not be what decides whether the
   first staging or production deploy passes, so cd.yml carries the per-environment
   default itself (an explicit `vars.LOKI_PORT` still wins). The workflow
   expressions are evaluated here, and the real preflight-isolation.sh is run with
   the result against stubbed `docker` and `ss`.

2. Log shipping is best effort (QA finding M4).
   The two compose steps of the deploy job bring the stack up through
   backend/deploy/compose-stack.sh. A collector (alloy, docker-socket-proxy) that
   cannot be pulled or started warns and does not fail the step; everything else
   stays fatal. compose-stack.sh is run against a stub `docker` that records its
   arguments and fails on request.
"""

import os
import pathlib
import re
import shutil
import subprocess
import tempfile
import unittest

import yaml

HERE = pathlib.Path(__file__).resolve().parent
DEPLOY = HERE.parent
REPO = DEPLOY.parent.parent
CD = REPO / ".github" / "workflows" / "cd.yml"
ENVIRONMENTS = DEPLOY / "environments.tsv"
PREFLIGHT = DEPLOY / "preflight-isolation.sh"
COMPOSE_STACK = DEPLOY / "compose-stack.sh"


def find_bash():
    if os.name == "nt":
        for cand in (r"C:\Program Files\Git\bin\bash.exe", r"C:\Program Files\Git\usr\bin\bash.exe"):
            if os.path.exists(cand):
                return cand
        return None  # the WSL launcher on PATH would run the stubs in another world
    return shutil.which("bash")


BASH = find_bash()


def posix(path):
    return pathlib.Path(path).as_posix()


def registry():
    rows = {}
    for line in ENVIRONMENTS.read_text(encoding="utf-8").splitlines():
        if not line.strip() or line.startswith("#") or line.startswith("environment\t"):
            continue
        env, deploy_dir, project, prefix, ports = line.split("\t")
        rows[env] = {"deploy_dir": deploy_dir, "project": project, "prefix": prefix,
                     "ports": ports.split(",")}
    return rows


def deploy_steps():
    doc = yaml.safe_load(CD.read_text(encoding="utf-8"))
    return doc["jobs"]["deploy"]["steps"]


def step(name_prefix):
    found = [s for s in deploy_steps() if str(s.get("name", "")).startswith(name_prefix)]
    assert len(found) == 1, f"expected exactly one step starting {name_prefix!r}, found {len(found)}"
    return found[0]


# --------------------------------------------------------------------------
# a just-enough evaluator for the one expression shape used for LOKI_PORT:
#   ${{ vars.X || (<ref> == 'a' && 'p') || (<ref> == 'b' && 'q') || 'default' }}
# --------------------------------------------------------------------------

def split_top_level(text, sep):
    parts, depth, cur, i = [], 0, [], 0
    in_quote = False
    while i < len(text):
        c = text[i]
        if c == "'":
            in_quote = not in_quote
        if not in_quote:
            if c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
            elif depth == 0 and text.startswith(sep, i):
                parts.append("".join(cur).strip())
                cur = []
                i += len(sep)
                continue
        cur.append(c)
        i += 1
    parts.append("".join(cur).strip())
    return parts


def evaluate(expression, refs):
    """Evaluate ${{ ... }} with `refs` mapping reference names (vars.X,
    needs.resolve.outputs.environment) to strings; '' is falsy like in Actions."""
    inner = re.fullmatch(r"\$\{\{\s*(.*?)\s*\}\}", expression.strip(), re.S)
    assert inner, f"not a single ${{{{ }}}} expression: {expression!r}"
    return eval_or(inner.group(1), refs)


def eval_or(text, refs):
    value = ""
    for operand in split_top_level(text, "||"):
        value = eval_and(operand, refs)
        if value:
            return value
    return value


def eval_and(text, refs):
    text = text.strip()
    if text.startswith("(") and text.endswith(")") and split_top_level(text, "&&") == [text]:
        return eval_or(text[1:-1], refs)
    parts = split_top_level(text, "&&")
    value = ""
    for part in parts:
        value = eval_atom(part, refs)
        if not value:
            return value
    return value


def eval_atom(text, refs):
    text = text.strip()
    if text.startswith("(") and text.endswith(")"):
        return eval_or(text[1:-1], refs)
    m = re.fullmatch(r"(\S+)\s*==\s*'([^']*)'", text)
    if m:
        return "true" if refs.get(m.group(1), "") == m.group(2) else ""
    m = re.fullmatch(r"'([^']*)'", text)
    if m:
        return m.group(1)
    if re.fullmatch(r"[A-Za-z_][\w.]*", text):
        return refs.get(text, "")
    raise AssertionError(f"expression operand not modelled: {text!r}")


def loki_expressions():
    """Every LOKI_PORT env entry of every step of the deploy job."""
    found = []
    for s in deploy_steps():
        env = s.get("env") or {}
        if "LOKI_PORT" in env:
            found.append((s["name"], env["LOKI_PORT"]))
    return found


class EvaluatorTests(unittest.TestCase):
    """The evaluator carries the M1 tests: pin it on shapes it must get right."""

    def test_or_and_precedence_and_empty_strings(self):
        refs = {"vars.X": "", "e": "b"}
        self.assertEqual(evaluate("${{ vars.X || (e == 'a' && '1') || (e == 'b' && '2') || '3' }}", refs), "2")
        self.assertEqual(evaluate("${{ vars.X || (e == 'a' && '1') || (e == 'b' && '2') || '3' }}", dict(refs, e="c")), "3")
        self.assertEqual(evaluate("${{ vars.X || (e == 'a' && '1') || '3' }}", dict(refs, **{"vars.X": "9"})), "9")
        self.assertEqual(evaluate("${{ vars.X || '' }}", refs), "")

    def test_evaluator_refuses_what_it_does_not_model(self):
        with self.assertRaises(AssertionError):
            evaluate("${{ format('{0}', vars.X) }}", {})


class LokiPortDefaultTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.rows = registry()
        cls.expressions = loki_expressions()

    def resolve(self, expression, environment, var=""):
        return evaluate(expression, {"vars.LOKI_PORT": var,
                                     "needs.resolve.outputs.environment": environment})

    def loki_port_of_row(self, environment):
        ports = [p for p in self.rows[environment]["ports"] if 3100 <= int(p) <= 3199]
        self.assertEqual(len(ports), 1)
        return ports[0]

    def test_the_isolation_preflight_and_the_env_file_step_both_set_it(self):
        names = [n for n, _ in self.expressions]
        self.assertEqual(len(names), 2, names)
        self.assertTrue(any(n.startswith("Isolation preflight") for n in names))
        self.assertTrue(any(n.startswith("Write environment file") for n in names))

    def test_with_no_variable_each_environment_resolves_its_registry_row(self):
        for name, expression in self.expressions:
            for environment in ("dev", "staging", "production"):
                with self.subTest(step=name, environment=environment):
                    self.assertEqual(self.resolve(expression, environment),
                                     self.loki_port_of_row(environment))

    def test_an_explicit_variable_still_overrides(self):
        for name, expression in self.expressions:
            for environment in ("dev", "staging", "production"):
                with self.subTest(step=name, environment=environment):
                    self.assertEqual(self.resolve(expression, environment, var="4999"), "4999")

    def test_an_unknown_environment_falls_back_to_the_dev_port_like_the_other_ports(self):
        for name, expression in self.expressions:
            with self.subTest(step=name):
                self.assertEqual(self.resolve(expression, "scratch"), self.loki_port_of_row("dev"))

    def test_the_old_default_is_what_broke_staging_and_production(self):
        old = "${{ vars.LOKI_PORT || '3100' }}"
        for environment in ("staging", "production"):
            self.assertNotEqual(self.resolve(old, environment), self.loki_port_of_row(environment))


@unittest.skipUnless(BASH, "bash is not available")
class PreflightWithResolvedPortTests(unittest.TestCase):
    """The real preflight-isolation.sh, offline: stubbed docker and ss see an empty host."""

    @classmethod
    def setUpClass(cls):
        cls.rows = registry()
        cls.tmp = tempfile.TemporaryDirectory()
        cls.bindir = pathlib.Path(cls.tmp.name) / "bin"
        cls.bindir.mkdir()
        for name in ("docker", "ss"):
            path = cls.bindir / name
            # docker: nothing runs, nothing is published. ss: nothing listens.
            path.write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8", newline="\n")
            path.chmod(0o755)
        cls.expressions = loki_expressions()
        # The script reads environments.tsv next to itself. Run copies with LF line
        # endings: a Windows checkout with autocrlf has CRLF in the tsv, and the
        # trailing CR would hide the last (Loki) port of each row from the check.
        cls.scripts = pathlib.Path(cls.tmp.name) / "scripts"
        cls.scripts.mkdir()
        for src in (PREFLIGHT, ENVIRONMENTS):
            text = src.read_text(encoding="utf-8").replace("\r\n", "\n")
            (cls.scripts / src.name).write_text(text, encoding="utf-8", newline="\n")

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def run_preflight(self, environment, loki_port):
        row = self.rows[environment]
        ports = [p for p in row["ports"] if not 3100 <= int(p) <= 3199] + [str(loki_port)]
        labels = [f"port{i}:{p}" for i, p in enumerate(ports[:-1])] + [f"loki:{ports[-1]}"]
        env = {k: v for k, v in os.environ.items() if not k.startswith(("GITHUB_", "ISOLATION_"))}
        env["PATH"] = posix(self.bindir) + os.pathsep + env.get("PATH", "")
        env.update({
            "DEPLOY_ENVIRONMENT": environment,
            "RPG_DEPLOY_DIR": row["deploy_dir"],
            "COMPOSE_PROJECT_NAME": row["project"],
            "COMPOSE_NAME_PREFIX": row["prefix"],
            "ISOLATION_PORTS": " ".join(labels),
        })
        script = self.scripts / PREFLIGHT.name
        return subprocess.run([BASH, posix(script)], capture_output=True, text=True, env=env)

    def resolved(self, expression, environment):
        return evaluate(expression, {"vars.LOKI_PORT": "",
                                     "needs.resolve.outputs.environment": environment})

    def test_preflight_passes_for_each_environment_with_no_variable_set(self):
        for name, expression in self.expressions:
            for environment in ("dev", "staging", "production"):
                with self.subTest(step=name, environment=environment):
                    proc = self.run_preflight(environment, self.resolved(expression, environment))
                    self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
                    self.assertIn("isolation preflight OK", proc.stdout)

    def test_the_old_default_fails_preflight_for_staging_and_production(self):
        for environment in ("staging", "production"):
            with self.subTest(environment=environment):
                proc = self.run_preflight(environment, 3100)
                self.assertNotEqual(proc.returncode, 0)
                self.assertIn("reserved for environment 'dev'", proc.stdout)


# --------------------------------------------------------------------------
# M4: the collector is best effort
# --------------------------------------------------------------------------

STUB_DOCKER = r"""#!/usr/bin/env bash
# Records every call. `config --services` lists a fixed set of services.
# STUB_FAIL_UP: space-separated service names; an `up` naming any of them fails.
# STUB_FAIL_CONFIG: non-empty makes `config --services` fail.
echo "$*" >> "$STUB_LOG"
args=" $* "
case "$args" in
  *" config --services"*)
    [ -z "${STUB_FAIL_CONFIG:-}" ] || { echo "config failed" >&2; exit 1; }
    printf '%s\n' postgres postgres-game postgres-game-roles nakama redis lgtm docker-socket-proxy alloy gateway
    exit 0 ;;
  *" up "*)
    for s in ${STUB_FAIL_UP:-}; do
      case "$args" in
        *" $s "*) echo "Error response from daemon: pull access denied for $s" >&2; exit 18 ;;
      esac
    done
    exit 0 ;;
esac
exit 0
"""

COLLECTOR = {"alloy", "docker-socket-proxy"}


@unittest.skipUnless(BASH, "bash is not available")
class CollectorIsBestEffortTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = pathlib.Path(self.tmp.name)
        self.bindir = root / "bin"
        self.bindir.mkdir()
        path = self.bindir / "docker"
        path.write_text(STUB_DOCKER, encoding="utf-8", newline="\n")
        path.chmod(0o755)
        self.log = root / "calls.log"
        self.summary = root / "summary.md"

    def run_stack(self, *up_flags, fail_up="", fail_config=False, monitoring="true", files=False):
        env = {k: v for k, v in os.environ.items() if not k.startswith("GITHUB_")}
        env["PATH"] = posix(self.bindir) + os.pathsep + env.get("PATH", "")
        env.update({
            "STUB_LOG": posix(self.log),
            "STUB_FAIL_UP": fail_up,
            "STUB_FAIL_CONFIG": "1" if fail_config else "",
            "MONITORING_ENABLED": monitoring,
            "GITHUB_STEP_SUMMARY": posix(self.summary),
            "RPG_DEPLOY_DIR": "/opt/rpg-mmo-test",
        })
        profile = "(--profile monitoring --profile realtime)" if monitoring != "false" else "()"
        file_args = "(-f docker-compose.yml -f docker-compose.agones.yml)" if files else "()"
        # The shape of the CD steps: set -euo pipefail, the arrays, the source, the call.
        script = (f"set -euo pipefail; file_args={file_args}; profile_args={profile}; "
                  f"source '{posix(COMPOSE_STACK)}'; compose_up_stack {' '.join(up_flags)}; "
                  "echo REACHED_END")
        return subprocess.run([BASH, "-c", script], capture_output=True, text=True, env=env)

    def calls(self):
        return self.log.read_text(encoding="utf-8").splitlines() if self.log.exists() else []

    def ups(self):
        return [c for c in self.calls() if " up " in f" {c} "]

    def test_everything_up_runs_the_stack_first_and_the_collector_second(self):
        proc = self.run_stack("--remove-orphans")
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        ups = self.ups()
        self.assertEqual(len(ups), 2, ups)
        first, second = ups
        self.assertIn("--remove-orphans", first)
        for svc in COLLECTOR:
            self.assertNotIn(f" {svc}", f" {first.split(' up ')[1]}")
        for svc in ("postgres", "postgres-game", "nakama", "redis", "lgtm", "gateway"):
            self.assertRegex(first, rf"(^| ){svc}( |$)")
        self.assertTrue(second.endswith("up -d alloy docker-socket-proxy"), second)
        self.assertIn("REACHED_END", proc.stdout)
        self.assertNotIn("::warning", proc.stdout)

    def test_a_collector_that_cannot_be_pulled_warns_and_the_step_succeeds(self):
        for failing in ("alloy", "docker-socket-proxy"):
            with self.subTest(failing=failing):
                self.log.unlink(missing_ok=True)
                self.summary.unlink(missing_ok=True)
                proc = self.run_stack("--remove-orphans", fail_up=failing)
                self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
                self.assertIn("REACHED_END", proc.stdout)
                self.assertIn("::warning title=Log collector not started::", proc.stdout)
                self.assertIn("Retry: cd /opt/rpg-mmo-test/deploy && docker compose --profile monitoring up -d "
                              "alloy docker-socket-proxy", proc.stdout)
                self.assertIn("pull access denied", proc.stderr)
                self.assertEqual(len(self.ups()), 2)
                text = self.summary.read_text(encoding="utf-8")
                self.assertIn("log collector", text)
                self.assertIn("up -d alloy docker-socket-proxy", text)

    def test_a_failure_of_the_game_or_data_services_or_lgtm_still_fails_the_step(self):
        for failing in ("lgtm", "gateway", "postgres-game", "nakama"):
            with self.subTest(failing=failing):
                self.log.unlink(missing_ok=True)
                proc = self.run_stack("--remove-orphans", fail_up=failing)
                self.assertNotEqual(proc.returncode, 0, proc.stdout)
                self.assertNotIn("REACHED_END", proc.stdout)
                self.assertEqual(len(self.ups()), 1, "the collector must not be tried after a fatal failure")

    def test_a_failing_config_listing_fails_the_step(self):
        proc = self.run_stack(fail_config=True)
        self.assertNotEqual(proc.returncode, 0)
        self.assertEqual(self.ups(), [])

    def test_with_monitoring_off_the_collector_is_not_started_at_all(self):
        proc = self.run_stack("--remove-orphans", monitoring="false")
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        ups = self.ups()
        self.assertEqual(len(ups), 1)
        self.assertNotIn("alloy", ups[0])
        self.assertNotIn("--profile monitoring", ups[0])

    def test_compose_file_arguments_are_passed_to_both_commands(self):
        proc = self.run_stack("--remove-orphans", files=True)
        self.assertEqual(proc.returncode, 0, proc.stdout + proc.stderr)
        for call in self.ups():
            self.assertTrue(call.startswith("compose -f docker-compose.yml -f docker-compose.agones.yml"), call)

    def test_the_script_is_valid_bash(self):
        proc = subprocess.run([BASH, "-n", posix(COMPOSE_STACK)], capture_output=True, text=True)
        self.assertEqual(proc.returncode, 0, proc.stderr)


class CdStepsUseTheHelperTests(unittest.TestCase):
    """The workflow must call the helper; a bare `up -d` would undo the point of it."""

    STEPS = ("Start the data tier", "Start / update the stack")

    def test_both_compose_steps_source_the_helper_and_call_it(self):
        for name in self.STEPS:
            with self.subTest(step=name):
                run = step(name)["run"]
                self.assertIn("backend/deploy/compose-stack.sh", run)
                self.assertRegex(run, r"(?m)^\s*compose_up_stack\b")

    def test_neither_step_runs_a_bare_compose_up(self):
        for name in self.STEPS:
            with self.subTest(step=name):
                code = "\n".join(l for l in step(name)["run"].splitlines() if not l.lstrip().startswith("#"))
                self.assertNotRegex(code, r"docker compose[^\n]*\bup\b",
                                    "a direct `docker compose up` bypasses the best-effort collector")

    def test_the_stack_step_keeps_remove_orphans_and_the_data_tier_does_not_gain_it(self):
        self.assertRegex(step("Start / update the stack")["run"], r"compose_up_stack --remove-orphans")
        lines = step("Start the data tier")["run"].splitlines()
        code = "\n".join(l for l in lines if not l.lstrip().startswith("#"))
        self.assertNotIn("--remove-orphans", code)

    def test_the_deploy_step_for_k8s_receives_the_environment(self):
        env = step("Deploy to Kubernetes")["env"]
        self.assertEqual(env.get("DEPLOY_ENVIRONMENT"), "${{ needs.resolve.outputs.environment }}")

    def test_every_step_that_runs_dev_up_receives_it(self):
        ran = 0
        for s in deploy_steps():
            if re.search(r"(?m)^\s*\./dev-up\.sh\b", str(s.get("run", ""))):
                ran += 1
                with self.subTest(step=s.get("name")):
                    self.assertEqual((s.get("env") or {}).get("DEPLOY_ENVIRONMENT"),
                                     "${{ needs.resolve.outputs.environment }}")
        self.assertGreaterEqual(ran, 1)


if __name__ == "__main__":
    unittest.main()
