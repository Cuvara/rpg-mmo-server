"""Offline contract tests for compose log shipping (Grafana Alloy -> Loki).

No Docker, no alloy binary, no network. Python 3.12 + PyYAML, run the way the
offline deploy job runs the k8s/games tests:

    python -m unittest discover -s backend/deploy/tests -p "test_*.py"

Everything is read relative to this file, so a test run against a copy of the
tree (the way each test was seen to fail: mutate a copy, never the tracked
files) needs only this directory copied next to the files it reads:

    <root>/backend/deploy/{tests,docker-compose*.yml,monitoring,environments.tsv}
    <root>/.github/workflows/cd.yml

What is covered (see docs/MONITORING.md for the design these assert):
  * the monitoring-profile services are pinned by version
  * Alloy has no Docker socket and no published port
  * the socket proxy is read-only, internal and publishes nothing
  * Loki is published on literal 127.0.0.1 only, with or without the override
  * every bind-mounted file travels with the compose file in CD
  * the Alloy config is well formed and keeps the label contract
  * game server services carry the game / server-id labels consistently
  * Loki retention is 168h and the compactor enforces it
  * environments.tsv reserves one distinct Loki port per environment
"""

import pathlib
import re
import unittest

import yaml

HERE = pathlib.Path(__file__).resolve().parent
DEPLOY = HERE.parent
REPO = DEPLOY.parent.parent
COMPOSE = DEPLOY / "docker-compose.yml"
OVERRIDE = DEPLOY / "docker-compose.override.yml"
ALLOY_CONFIG = DEPLOY / "monitoring" / "alloy" / "config.alloy"
LOKI_CONFIG = DEPLOY / "monitoring" / "loki-config.yaml"
ENVIRONMENTS = DEPLOY / "environments.tsv"
CD = REPO / ".github" / "workflows" / "cd.yml"

PROFILE = "monitoring"
STREAM_LABELS = {"env", "service", "level", "game", "server_id"}
LEVEL_VALUES = {"trace", "debug", "info", "warn", "error", "fatal", "unknown"}


# --------------------------------------------------------------------------
# compose helpers: interpolation and the base + override merge
# --------------------------------------------------------------------------

def interpolate(text, env=None):
    """Expand ${VAR}, ${VAR:-d}, ${VAR-d} (nested defaults) and $$ like compose."""
    env = env or {}

    def parse(s, i, stop):
        out = []
        while i < len(s):
            c = s[i]
            if stop and c == "}":
                return "".join(out), i
            if c == "$" and s.startswith("$$", i):
                out.append("$")
                i += 2
            elif c == "$" and s.startswith("${", i):
                i += 2
                m = re.match(r"[A-Za-z_][A-Za-z0-9_]*", s[i:])
                if not m:
                    raise ValueError(f"bad variable reference in {text!r}")
                name = m.group(0)
                i += len(name)
                if s.startswith("}", i):
                    out.append(env.get(name, ""))
                    i += 1
                    continue
                m = re.match(r"(:-|-|:\?|\?|:\+|\+)", s[i:])
                if not m:
                    raise ValueError(f"bad variable operator in {text!r}")
                op = m.group(0)
                i += len(op)
                arg, i = parse(s, i, True)
                i += 1  # the closing brace
                value = env.get(name)
                if op == ":-":
                    out.append(value if value else arg)
                elif op == "-":
                    out.append(value if value is not None else arg)
                else:
                    out.append(value or "")
            else:
                out.append(c)
                i += 1
        return "".join(out), i

    return parse(text, 0, False)[0]


def normalise_pairs(value):
    """environment / labels as a mapping, whichever of the two forms was used."""
    if value is None:
        return {}
    if isinstance(value, dict):
        return {str(k): ("" if v is None else str(v)) for k, v in value.items()}
    out = {}
    for item in value:
        key, _, val = str(item).partition("=")
        out[key] = val
    return out


def merge_service(base, extra):
    merged = dict(base)
    for key, value in extra.items():
        if key in ("environment", "labels"):
            merged[key] = {**normalise_pairs(base.get(key)), **normalise_pairs(value)}
        elif key in ("ports", "volumes", "networks", "depends_on") and isinstance(value, list):
            have = base.get(key)
            merged[key] = (list(have) if isinstance(have, list) else []) + list(value)
        else:
            merged[key] = value
    return merged


def load_compose(with_override):
    doc = yaml.safe_load(COMPOSE.read_text(encoding="utf-8"))
    services = {k: dict(v) for k, v in doc["services"].items()}
    if with_override:
        extra = yaml.safe_load(OVERRIDE.read_text(encoding="utf-8"))
        for name, svc in (extra.get("services") or {}).items():
            services[name] = merge_service(services.get(name, {}), svc)
    for svc in services.values():
        svc["environment"] = normalise_pairs(svc.get("environment"))
        svc["labels"] = normalise_pairs(svc.get("labels"))
    return services, doc.get("networks") or {}, doc.get("volumes") or {}


def monitoring_services(services):
    return {n: s for n, s in services.items() if PROFILE in (s.get("profiles") or [])}


def port_mappings(svc):
    """(host_ip_raw, host_port_raw, container_port, protocol) for each publish."""
    out = []
    for entry in svc.get("ports") or []:
        if isinstance(entry, dict):
            out.append((entry.get("host_ip", ""), str(entry.get("published", "")),
                        str(entry.get("target", "")), entry.get("protocol", "tcp")))
            continue
        spec = str(entry)
        proto = "tcp"
        if "/" in spec:
            spec, proto = spec.rsplit("/", 1)
        # Split on ':' outside ${...}: a default may itself contain a colon.
        parts, depth, cur = [], 0, ""
        for ch in spec:
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
            if ch == ":" and depth == 0:
                parts.append(cur)
                cur = ""
            else:
                cur += ch
        parts.append(cur)
        if len(parts) == 3:
            out.append((parts[0], parts[1], parts[2], proto))
        elif len(parts) == 2:
            out.append(("", parts[0], parts[1], proto))
        else:
            out.append(("", "", parts[0], proto))
    return out


def volume_mappings(svc):
    """(source, target, mode) for each volume of a service."""
    out = []
    for entry in svc.get("volumes") or []:
        if isinstance(entry, dict):
            mode = "ro" if entry.get("read_only") else "rw"
            out.append((str(entry.get("source", "")), str(entry.get("target", "")), mode))
            continue
        parts = str(entry).split(":")
        if len(parts) == 1:
            out.append(("", parts[0], "rw"))
        else:
            out.append((parts[0], parts[1], parts[2] if len(parts) > 2 else "rw"))
    return out


def is_bind(source):
    return source.startswith((".", "/", "~", "$"))


# --------------------------------------------------------------------------
# Alloy configuration parser (just enough of the syntax to check structure)
# --------------------------------------------------------------------------

class ConfigError(ValueError):
    pass


def tokenize(text):
    """Tokens (kind, value): str, ident, nl, op. Comments are dropped."""
    tokens, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == "\n":
            tokens.append(("nl", "\n"))
            i += 1
        elif c in " \t\r":
            i += 1
        elif text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            if end < 0:
                raise ConfigError("unterminated block comment")
            tokens.extend(("nl", "\n") for _ in range(text.count("\n", i, end)))
            i = end + 2
        elif c == '"':
            j, buf = i + 1, []
            while True:
                if j >= n or text[j] == "\n":
                    raise ConfigError(f"unterminated string near offset {i}")
                if text[j] == "\\" and j + 1 < n:
                    buf.append(text[j:j + 2])
                    j += 2
                    continue
                if text[j] == '"':
                    break
                buf.append(text[j])
                j += 1
            tokens.append(("str", "".join(buf)))
            i = j + 1
        elif c == "`":
            end = text.find("`", i + 1)
            if end < 0:
                raise ConfigError(f"unterminated raw string near offset {i}")
            tokens.append(("str", text[i + 1:end]))
            tokens.extend(("nl", "\n") for _ in range(text.count("\n", i, end)))
            i = end + 1
        elif re.match(r"[A-Za-z_]", c):
            m = re.match(r"[A-Za-z_][A-Za-z0-9_.]*", text[i:])
            tokens.append(("ident", m.group(0)))
            i += len(m.group(0))
        else:
            tokens.append(("op", c))
            i += 1
    return tokens


class Block:
    def __init__(self, kind, label):
        self.kind = kind
        self.label = label
        self.attrs = {}
        self.children = []

    @property
    def name(self):
        return f"{self.kind}.{self.label}" if self.label is not None else self.kind


CLOSERS = {"{": "}", "[": "]", "(": ")"}


def check_balance(tokens):
    stack = []
    for kind, value in tokens:
        if kind != "op":
            continue
        if value in CLOSERS:
            stack.append(value)
        elif value in CLOSERS.values():
            if not stack or CLOSERS[stack.pop()] != value:
                raise ConfigError(f"unbalanced {value!r}")
    if stack:
        raise ConfigError(f"unclosed {stack[-1]!r}")


def parse_body(tokens, pos, top):
    """Parse attributes and blocks until the closing brace (or end when top)."""
    attrs, children = {}, []
    while pos < len(tokens):
        kind, value = tokens[pos]
        if kind == "nl":
            pos += 1
            continue
        if kind == "op" and value == "}":
            if top:
                raise ConfigError("stray '}'")
            return attrs, children, pos + 1
        if kind != "ident":
            raise ConfigError(f"unexpected token {value!r}")
        name = value
        pos += 1
        if tokens[pos] == ("op", "="):
            pos += 1
            depth, expr = 0, []
            while pos < len(tokens):
                k, v = tokens[pos]
                if k == "op" and v in CLOSERS:
                    depth += 1
                elif k == "op" and v in CLOSERS.values():
                    depth -= 1
                if k == "nl" and depth == 0:
                    break
                if k != "nl":
                    expr.append((k, v))
                pos += 1
            attrs[name] = expr
            continue
        label = None
        if tokens[pos][0] == "str":
            label = tokens[pos][1]
            pos += 1
        if tokens[pos] != ("op", "{"):
            raise ConfigError(f"expected '{{' after {name}")
        block = Block(name, label)
        block.attrs, block.children, pos = parse_body(tokens, pos + 1, False)
        children.append(block)
    if not top:
        raise ConfigError("missing '}'")
    return attrs, children, pos


def parse_alloy(text):
    tokens = tokenize(text)
    check_balance(tokens)
    _, blocks, _ = parse_body(tokens, 0, True)
    return blocks, tokens


def strings_in(expr):
    return [v for k, v in expr if k == "str"]


def idents_in(expr):
    return [v for k, v in expr if k == "ident"]


def object_keys(expr):
    """Keys of the first {...} object literal in an expression."""
    keys, depth = [], 0
    for idx, (k, v) in enumerate(expr):
        if k == "op" and v in CLOSERS:
            depth += 1
        elif k == "op" and v in CLOSERS.values():
            depth -= 1
        elif depth == 1 and k in ("ident", "str") and idx + 1 < len(expr) and expr[idx + 1] == ("op", "="):
            keys.append(v)
    return keys


def walk(blocks):
    for b in blocks:
        yield b
        yield from walk(b.children)


def component(blocks, kind, label):
    for b in blocks:
        if b.kind == kind and b.label == label:
            return b
    raise AssertionError(f"component {kind} \"{label}\" is not defined")


def rules_of(relabel):
    return [c for c in relabel.children if c.kind == "rule"]


def rule_value(rule, attr):
    expr = rule.attrs.get(attr)
    return strings_in(expr)[0] if expr and strings_in(expr) else None


# --------------------------------------------------------------------------
# tests
# --------------------------------------------------------------------------

class ComposeMonitoringTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.base, cls.base_nets, cls.base_vols = load_compose(False)
        cls.merged, cls.merged_nets, _ = load_compose(True)

    def test_monitoring_profile_has_the_expected_services(self):
        self.assertEqual(set(monitoring_services(self.base)),
                         {"lgtm", "docker-socket-proxy", "alloy"})

    def test_monitoring_images_are_pinned_by_version(self):
        for name, svc in monitoring_services(self.base).items():
            raw = svc["image"]
            with self.subTest(service=name, image=raw):
                for ref in re.findall(r"\$\{([^}]*)\}", raw):
                    self.assertRegex(ref, r":?-.+",
                                     "an image variable must carry a default tag")
                image = interpolate(raw)
                self.assertNotIn("${", image)
                if "@sha256:" in image:
                    continue
                tag = image.rsplit("/", 1)[-1].partition(":")[2]
                self.assertTrue(tag, "image has no tag")
                self.assertNotEqual(tag.lower(), "latest")
                self.assertRegex(tag, r"^v?\d+(\.\d+)*", "tag is not version-like")

    def test_alloy_mounts_no_docker_socket_and_publishes_no_port(self):
        for label, services in (("base", self.base), ("with override", self.merged)):
            alloy = services["alloy"]
            with self.subTest(files=label):
                self.assertFalse(alloy.get("ports"), "alloy must publish no port")
                self.assertFalse(alloy.get("privileged"))
                self.assertNotIn(alloy.get("network_mode"), ("host",))
                self.assertNotIn(alloy.get("pid"), ("host",))
                for source, target, mode in volume_mappings(alloy):
                    self.assertNotIn("docker.sock", source + target)
                    self.assertNotIn("/var/run", source)
                    self.assertNotIn("/var/lib/docker", source)
                    if is_bind(source):
                        self.assertEqual(mode, "ro", f"bind mount {source} must be read-only")
                self.assertNotIn("DOCKER_HOST",
                                 {k for k in alloy["environment"] if "unix://" in alloy["environment"][k]})

    def test_socket_proxy_is_read_only_internal_and_unpublished(self):
        for label, services, networks in (("base", self.base, self.base_nets),
                                          ("with override", self.merged, self.merged_nets)):
            proxy = services["docker-socket-proxy"]
            env = proxy["environment"]
            with self.subTest(files=label):
                self.assertFalse(proxy.get("ports"), "the proxy must publish no port")
                self.assertFalse(proxy.get("privileged"))
                sockets = [v for v in volume_mappings(proxy) if "docker.sock" in v[0]]
                self.assertEqual(len(sockets), 1, "exactly one socket mount")
                self.assertEqual(sockets[0][2], "ro", "the socket mount must be :ro")
                self.assertEqual(len(volume_mappings(proxy)), 1, "nothing else is mounted")
                # Writes: no POST (and so no PUT/DELETE reaching an enabled path).
                self.assertEqual(env.get("POST"), "0")
                for flag in ("ALLOW_START", "ALLOW_STOP", "ALLOW_RESTARTS"):
                    self.assertEqual(env.get(flag), "0", flag)
                # Only the read endpoints Alloy needs are on.
                enabled = {k for k, v in env.items() if v == "1"}
                self.assertEqual(enabled, {"CONTAINERS", "NETWORKS", "PING", "VERSION"})
                for dangerous in ("EXEC", "IMAGES", "VOLUMES", "SERVICES", "SWARM", "SECRETS",
                                  "CONFIGS", "TASKS", "PLUGINS", "SYSTEM", "BUILD", "COMMIT",
                                  "DISTRIBUTION", "SESSION", "GRPC", "AUTH", "EVENTS", "INFO"):
                    self.assertEqual(env.get(dangerous), "0",
                                     f"{dangerous} must be stated and off")
                # Its network is internal, and it is not also on the default one.
                joined = proxy.get("networks")
                joined = list(joined) if joined else []
                self.assertTrue(joined, "the proxy must join an explicit network")
                self.assertNotIn("default", joined)
                for net in joined:
                    self.assertIs((networks.get(net) or {}).get("internal"), True,
                                  f"network {net} must be internal")
                # Only the proxy and alloy share that network.
                for net in joined:
                    members = {n for n, s in services.items()
                               if net in (list(s.get("networks") or []))}
                    self.assertEqual(members, {"docker-socket-proxy", "alloy"})

    def test_alloy_reaches_proxy_and_lgtm_networks(self):
        alloy = self.base["alloy"]
        joined = list(alloy.get("networks") or [])
        proxy_nets = list(self.base["docker-socket-proxy"]["networks"])
        self.assertTrue(set(joined) & set(proxy_nets), "alloy must share a network with the proxy")
        self.assertIn("default", joined, "alloy must stay on the default network to reach lgtm:3100")

    def test_loki_port_is_loopback_only_in_base_and_with_override(self):
        for label, services in (("base", self.base), ("with override", self.merged)):
            with self.subTest(files=label):
                published = []
                for name, svc in services.items():
                    for ip, host, container, proto in port_mappings(svc):
                        if container == "3100":
                            published.append((name, ip, host))
                self.assertEqual(len(published), 1, f"expected one Loki publish, got {published}")
                name, ip, host = published[0]
                self.assertEqual(name, "lgtm")
                self.assertEqual(ip, "127.0.0.1", "host ip must be the literal 127.0.0.1")
                self.assertIn("LOKI_PORT", host)

    def test_log_pipeline_services_expose_nothing_beyond_loopback(self):
        for label, services in (("base", self.base), ("with override", self.merged)):
            for name in ("alloy", "docker-socket-proxy"):
                with self.subTest(files=label, service=name):
                    self.assertEqual(port_mappings(services[name]), [])

    def test_every_monitoring_bind_mount_exists_and_travels_in_cd(self):
        cd = CD.read_text(encoding="utf-8")
        required = set()
        m = re.search(r"for f in(.*?);\s*do\s*\n\s*\[ -f \"dist/\$f\"", cd, re.S)
        self.assertIsNotNone(m, "cd.yml required-file loop not found")
        for token in m.group(1).replace("\\", " ").split():
            required.add(token)
        copied = set(re.findall(r"cp (?:-r )?backend/deploy/(\S+)\s+dist/deploy/", cd))
        copied_dirs = set(re.findall(r"cp -r backend/deploy/(\S+)\s+dist/deploy/", cd))
        self.assertIn("monitoring", copied_dirs, "cd.yml must copy the monitoring/ directory")

        checked = 0
        for name, svc in monitoring_services(self.merged).items():
            for source, target, mode in volume_mappings(svc):
                if not is_bind(source):
                    continue
                with self.subTest(service=name, source=source):
                    if "docker.sock" in source:
                        self.assertEqual(name, "docker-socket-proxy")
                        continue
                    self.assertTrue(source.startswith("./"), "bind source must be relative")
                    self.assertNotIn("$", source, "bind source must be a literal path")
                    self.assertNotIn("..", source.split("/"))
                    path = (DEPLOY / source).resolve()
                    self.assertTrue(path.exists(), f"{source} does not exist")
                    rel = source[2:]
                    self.assertTrue(
                        rel in copied or any(rel.startswith(d + "/") for d in copied_dirs),
                        f"{source} is not copied into the bundle by cd.yml")
                    if path.is_file():
                        listed = f"deploy/{rel}"
                        self.assertIn(listed, required,
                                      f"{listed} is not in cd.yml's required-file list")
                    checked += 1
        self.assertGreaterEqual(checked, 6)

    def test_loki_and_alloy_config_are_in_the_required_list(self):
        cd = CD.read_text(encoding="utf-8")
        m = re.search(r"for f in(.*?);\s*do\s*\n\s*\[ -f \"dist/\$f\"", cd, re.S)
        self.assertIsNotNone(m, "cd.yml required-file loop not found")
        listed = m.group(1).replace("\\", " ").split()
        for path in ("deploy/monitoring/alloy/config.alloy", "deploy/monitoring/loki-config.yaml"):
            self.assertIn(path, listed)

    def test_alloy_environment_provides_every_variable_the_config_reads(self):
        text = ALLOY_CONFIG.read_text(encoding="utf-8")
        wanted = set(re.findall(r'sys\.env\("([A-Z_][A-Z0-9_]*)"\)', text))
        self.assertTrue(wanted)
        self.assertLessEqual(wanted, set(self.base["alloy"]["environment"]))


class GameServerLabelTests(unittest.TestCase):
    ENVS = [
        {},
        {"GAMESERVER_GAME_ID": ""},
        {"GAMESERVER_GAME_ID": "ember"},
        {"GAMESERVER_ID": "gs-x"},
        {"GAMESERVER_MAP_ID": "map_09"},
        {"GAMESERVER_GAME_ID": "ember", "GAMESERVER_ID": "gs-y", "GAMESERVER_MAP_ID": "m"},
    ]

    @classmethod
    def setUpClass(cls):
        cls.merged, _, _ = load_compose(True)
        cls.game_servers = {n: s for n, s in cls.merged.items()
                            if "GAMESERVER_MAP_ID" in s["environment"]}

    def test_every_map_server_is_found_in_base_and_override(self):
        self.assertIn("gameserver-dotnet", self.game_servers)
        self.assertIn("gameserver-dotnet-map02", self.game_servers)

    def test_game_servers_carry_both_log_labels(self):
        for name, svc in self.game_servers.items():
            with self.subTest(service=name):
                self.assertIn("cuvara.log.game", svc["labels"])
                self.assertIn("cuvara.log.server_id", svc["labels"])

    def test_game_label_matches_the_configured_game_id(self):
        for name, svc in self.game_servers.items():
            for env in self.ENVS:
                with self.subTest(service=name, env=env):
                    label = interpolate(svc["labels"]["cuvara.log.game"], env)
                    configured = interpolate(svc["environment"]["GAMESERVER_GAME_ID"], env)
                    # The server treats an empty id as the default game.
                    self.assertEqual(label, configured or "cuvara")

    def test_server_id_label_matches_the_configured_server_id(self):
        for name, svc in self.game_servers.items():
            for env in self.ENVS:
                with self.subTest(service=name, env=env):
                    label = interpolate(svc["labels"]["cuvara.log.server_id"], env)
                    configured = interpolate(svc["environment"]["GAMESERVER_ID"], env)
                    self.assertTrue(label)
                    self.assertEqual(label, configured)

    def test_default_game_label_is_cuvara(self):
        for name, svc in self.game_servers.items():
            with self.subTest(service=name):
                self.assertEqual(interpolate(svc["labels"]["cuvara.log.game"], {}), "cuvara")

    def test_alloy_reads_the_label_names_compose_sets(self):
        blocks, _ = parse_alloy(ALLOY_CONFIG.read_text(encoding="utf-8"))
        relabel = component(blocks, "discovery.relabel", "containers")
        sources = {rule_value(r, "target_label"): idents_and_strings(r.attrs.get("source_labels"))
                   for r in rules_of(relabel)}
        for name, svc in self.game_servers.items():
            for key, target in (("cuvara.log.game", "game"), ("cuvara.log.server_id", "server_id")):
                self.assertIn(key, svc["labels"])
                expected = "__meta_docker_container_label_" + re.sub(r"[^a-zA-Z0-9_]", "_", key)
                self.assertEqual(sources.get(target), [expected], f"{name}: {target}")


def idents_and_strings(expr):
    return [v for k, v in (expr or []) if k == "str"]


class AlloyConfigTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.text = ALLOY_CONFIG.read_text(encoding="utf-8")
        cls.blocks, cls.tokens = parse_alloy(cls.text)
        cls.compose, _, _ = load_compose(True)

    # ---- structure ------------------------------------------------------

    def test_braces_brackets_and_quotes_balance(self):
        # parse_alloy already raised on unterminated strings / unbalanced pairs;
        # a stray backtick or quote also leaves an odd count outside comments.
        self.assertGreater(len(self.tokens), 50)
        self.assertEqual(self.text.count("`") % 2, 0)

    def test_expected_components_are_defined_once(self):
        names = [b.name for b in self.blocks]
        self.assertEqual(sorted(names), sorted([
            "discovery.docker.containers", "discovery.relabel.containers",
            "loki.source.docker.containers", "loki.process.level", "loki.write.lgtm"]))

    def test_every_component_reference_resolves(self):
        defined = {b.name for b in self.blocks}
        prefixes = ("discovery.", "loki.", "prometheus.", "otelcol.", "local.", "remote.")
        references = 0
        for block in walk(self.blocks):
            for expr in block.attrs.values():
                for ident in idents_in(expr):
                    if not ident.startswith(prefixes):
                        continue
                    references += 1
                    self.assertTrue(
                        any(ident.startswith(d + ".") for d in defined),
                        f"{ident} refers to a component that is not defined")
        self.assertGreaterEqual(references, 4)

    def test_pipeline_is_wired_source_to_process_to_write(self):
        source = component(self.blocks, "loki.source.docker", "containers")
        process = component(self.blocks, "loki.process", "level")
        relabel = component(self.blocks, "discovery.relabel", "containers")
        self.assertEqual(idents_in(source.attrs["targets"]), ["discovery.relabel.containers.output"])
        self.assertEqual(idents_in(source.attrs["forward_to"]), ["loki.process.level.receiver"])
        self.assertEqual(idents_in(process.attrs["forward_to"]), ["loki.write.lgtm.receiver"])
        self.assertEqual(idents_in(relabel.attrs["targets"]), ["discovery.docker.containers.targets"])

    def test_docker_hosts_use_the_proxy_not_a_socket(self):
        hosts = set()
        for kind in ("discovery.docker", "loki.source.docker"):
            hosts |= {strings_in(b.attrs["host"])[0] for b in self.blocks if b.kind == kind}
        self.assertEqual(hosts, {"tcp://docker-socket-proxy:2375"})
        self.assertIn("docker-socket-proxy", self.compose)
        self.assertNotIn("docker.sock", self.text.replace("docker-socket-proxy", ""))
        self.assertNotIn("unix://", self.text)

    def test_push_goes_to_lgtm_loki(self):
        write = component(self.blocks, "loki.write", "lgtm")
        endpoints = [c for c in write.children if c.kind == "endpoint"]
        self.assertEqual(len(endpoints), 1)
        self.assertEqual(strings_in(endpoints[0].attrs["url"]),
                         ["http://lgtm:3100/loki/api/v1/push"])
        self.assertIn("lgtm", self.compose)

    # ---- label rules ----------------------------------------------------

    def test_stream_labels_are_exactly_the_contract(self):
        process = component(self.blocks, "loki.process", "level")
        keeps = [c for c in process.children if c.kind == "stage.label_keep"]
        self.assertEqual(len(keeps), 1)
        kept = strings_in(keeps[0].attrs["values"])
        self.assertEqual(sorted(kept), sorted(STREAM_LABELS))
        self.assertEqual(len(kept), len(set(kept)))
        # It must run after every stage that can add a label.
        self.assertIs(process.children[-1], keeps[0], "label_keep must be the last stage")

    def test_every_label_the_config_can_set_is_in_the_contract(self):
        produced = set()
        relabel = component(self.blocks, "discovery.relabel", "containers")
        for rule in rules_of(relabel):
            target = rule_value(rule, "target_label")
            if target is not None:
                produced.add(target)
        for block in walk(self.blocks):
            if block.kind == "stage.static_labels":
                produced |= set(object_keys(block.attrs["values"]))
            if block.kind == "stage.labels":
                produced |= set(object_keys(block.attrs["values"]))
        self.assertEqual(produced, STREAM_LABELS)

    def test_no_label_is_built_from_a_container_id_or_name(self):
        relabel = component(self.blocks, "discovery.relabel", "containers")
        for rule in rules_of(relabel):
            self.assertNotEqual(rule_value(rule, "action"), "labelmap",
                                "labelmap copies every discovered label, container id included")
            for attr in ("source_labels", "replacement", "regex"):
                for text in strings_in(rule.attrs.get(attr) or []):
                    self.assertNotRegex(
                        text, r"container_(id|name)|__meta_docker_container_id|"
                              r"__meta_docker_container_name|__meta_docker_container_log",
                        f"{attr} of a relabel rule reads a container id or name")
        self.assertNotRegex(self.text, r"__meta_docker_container_(id|name)\b")
        for forbidden in ("container", "container_id", "container_name", "instance", "name", "id"):
            self.assertNotIn(f'target_label = "{forbidden}"', self.text)

    def test_label_sources_are_the_compose_and_cuvara_labels(self):
        relabel = component(self.blocks, "discovery.relabel", "containers")
        wanted = {
            "service": "__meta_docker_container_label_com_docker_compose_service",
            "game": "__meta_docker_container_label_cuvara_log_game",
            "server_id": "__meta_docker_container_label_cuvara_log_server_id",
        }
        got = {rule_value(r, "target_label"): strings_in(r.attrs.get("source_labels") or [])
               for r in rules_of(relabel)}
        for target, source in wanted.items():
            self.assertEqual(got.get(target), [source], target)

    def test_env_label_comes_from_the_deploy_environment_variable(self):
        relabel = component(self.blocks, "discovery.relabel", "containers")
        env_rules = [r for r in rules_of(relabel) if rule_value(r, "target_label") == "env"]
        self.assertEqual(len(env_rules), 1)
        replacement = env_rules[0].attrs["replacement"]
        self.assertIn("sys.env", idents_in(replacement))
        self.assertEqual(strings_in(replacement), ["DEPLOY_ENVIRONMENT"])

    def test_only_containers_of_the_compose_project_are_kept(self):
        project_label = "__meta_docker_container_label_com_docker_compose_project"
        discovery = component(self.blocks, "discovery.docker", "containers")
        filters = [c for c in discovery.children if c.kind == "filter"]
        self.assertEqual(len(filters), 1)
        self.assertEqual(strings_in(filters[0].attrs["name"]), ["label"])
        self.assertIn("com.docker.compose.project=", strings_in(filters[0].attrs["values"]))
        self.assertEqual(idents_in(filters[0].attrs["values"]), ["sys.env"])
        self.assertEqual(strings_in(filters[0].attrs["values"])[-1], "COMPOSE_PROJECT_NAME")

        relabel = component(self.blocks, "discovery.relabel", "containers")
        keeps = [r for r in rules_of(relabel) if rule_value(r, "action") == "keep"]
        self.assertEqual(len(keeps), 1)
        self.assertEqual(strings_in(keeps[0].attrs["source_labels"]), [project_label])
        self.assertEqual(strings_in(keeps[0].attrs["regex"]), ["COMPOSE_PROJECT_NAME"])
        self.assertEqual(idents_in(keeps[0].attrs["regex"]), ["sys.env"])

    def test_default_level_is_unknown(self):
        process = component(self.blocks, "loki.process", "level")
        statics = [c for c in process.children if c.kind == "stage.static_labels"]
        self.assertEqual(len(statics), 1)
        self.assertEqual(object_keys(statics[0].attrs["values"]), ["level"])
        self.assertEqual(strings_in(statics[0].attrs["values"]), ["unknown"])
        # It must come before the JSON stage, which can then only override it.
        names = [c.kind for c in process.children]
        self.assertLess(names.index("stage.static_labels"), names.index("stage.match"))

    def test_level_is_parsed_only_from_json_lines(self):
        process = component(self.blocks, "loki.process", "level")
        matches = [c for c in process.children if c.kind == "stage.match"]
        self.assertEqual(len(matches), 1)
        selector = strings_in(matches[0].attrs["selector"])[0]
        self.assertIn("|~", selector)
        self.assertIn(r"\\{", selector, "selector must anchor on a leading '{'")
        self.assertIn("^", selector)
        kinds = [c.kind for c in matches[0].children]
        self.assertIn("stage.json", kinds)
        json_stage = next(c for c in matches[0].children if c.kind == "stage.json")
        self.assertEqual(object_keys(json_stage.attrs["expressions"]), ["level"])
        self.assertEqual(strings_in(json_stage.attrs["expressions"]), ["level"])
        # stage.json outside the match would run on plain lines too.
        for child in process.children:
            self.assertNotEqual(child.kind, "stage.json")

    def test_level_values_are_normalised_to_a_closed_set(self):
        template = next(b for b in walk(self.blocks) if b.kind == "stage.template")
        body = strings_in(template.attrs["template"])[0]
        produced = set(re.findall(r"\}\}([a-z]+)\{\{", body))
        self.assertEqual(produced, LEVEL_VALUES)
        self.assertTrue(body.rstrip().endswith("{{ end }}"))

    def test_real_level_spellings_map_to_the_closed_set(self):
        """Evaluate the template's if / else-if chain without Alloy.

        The spellings are what the services really write: the C# game server's
        JSON logger (INFO WARN ERROR DEBUG TRACE CRITICAL; NONE is never a
        logged level), the Go gateway's slog (INFO WARN ERROR DEBUG) and
        Nakama's zap (lowercase, plus dpanic panic fatal). `information` and
        `warning` are accepted only as harmless aliases.
        """
        template = next(b for b in walk(self.blocks) if b.kind == "stage.template")
        body = strings_in(template.attrs["template"])[0]
        branches = re.findall(
            r'(?:\{\{ if|\{\{ else if) or((?: \(eq \$l "[a-z]+"\))+) \}\}([a-z]+)(?=\{\{)', body)
        self.assertEqual(len(branches), 6, "one branch per level except unknown")
        table = {}
        for spellings, result in branches:
            for spelling in re.findall(r'"([a-z]+)"', spellings):
                self.assertNotIn(spelling, table, f"{spelling} is matched by two branches")
                table[spelling] = result

        def level_of(raw):
            # The template: empty -> unknown, compare lowercased, else unknown.
            if not raw:
                return "unknown"
            return table.get(str(raw).lower(), "unknown")

        cases = {
            # C# game server (JSON logger of the gameserver-json-logs task)
            "TRACE": "trace", "DEBUG": "debug", "INFO": "info",
            "WARN": "warn", "ERROR": "error", "CRITICAL": "fatal",
            "NONE": "unknown",
            # Go gateway (slog)
            # (INFO WARN ERROR DEBUG are covered above)
            # Nakama (zap)
            "info": "info", "warn": "warn", "error": "error", "debug": "debug",
            "dpanic": "fatal", "panic": "fatal", "fatal": "fatal",
            # aliases, not what any service prints today
            "Information": "info", "Warning": "warn",
            # anything else must not mint a stream per value
            "": "unknown", "7": "unknown", "notice": "unknown", "INFO+2": "unknown",
        }
        for raw, expected in cases.items():
            with self.subTest(level=raw):
                self.assertEqual(level_of(raw), expected)
        self.assertEqual(set(table.values()) | {"unknown"}, LEVEL_VALUES)

    def test_lines_are_never_rewritten_or_dropped(self):
        forbidden = {"stage.output", "stage.replace", "stage.drop", "stage.regex",
                     "stage.limit", "stage.sampling", "stage.multiline", "stage.decolorize"}
        used = {b.kind for b in walk(self.blocks)}
        self.assertFalse(used & forbidden, f"stages that change or drop lines: {used & forbidden}")
        for block in walk(self.blocks):
            if block.kind == "stage.match":
                self.assertNotIn("action", block.attrs)


class LokiConfigTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.config = yaml.safe_load(LOKI_CONFIG.read_text(encoding="utf-8"))

    def test_retention_is_seven_days(self):
        self.assertEqual(self.config["limits_config"]["retention_period"], "168h")

    def test_compactor_enforces_retention(self):
        compactor = self.config["compactor"]
        self.assertIs(compactor["retention_enabled"], True)
        self.assertTrue(compactor.get("delete_request_store"),
                        "Loki refuses to start without delete_request_store")
        self.assertTrue(compactor["working_directory"].startswith("/data/"),
                        "compactor state must live on the lgtm-data volume")

    def test_replaces_the_image_config_at_the_path_it_reads(self):
        services, _, _ = load_compose(False)
        targets = [t for s, t, m in volume_mappings(services["lgtm"])
                   if s == "./monitoring/loki-config.yaml"]
        self.assertEqual(targets, ["/otel-lgtm/loki-config.yaml"])

    def test_loki_listens_on_the_published_container_port(self):
        self.assertEqual(self.config["server"]["http_listen_port"], 3100)
        self.assertIs(self.config["auth_enabled"], False)


class EnvironmentRegistryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        rows = []
        for line in ENVIRONMENTS.read_text(encoding="utf-8").splitlines():
            if not line.strip() or line.startswith("#") or line.startswith("environment\t"):
                continue
            cols = line.split("\t")
            rows.append({"environment": cols[0], "ports": [int(p) for p in cols[4].split(",")]})
        cls.rows = rows

    def test_registry_has_the_three_environments(self):
        self.assertEqual([r["environment"] for r in self.rows], ["dev", "staging", "production"])

    def test_each_environment_reserves_one_loki_port(self):
        loki = {}
        for row in self.rows:
            ports = [p for p in row["ports"] if 3100 <= p <= 3199]
            self.assertEqual(len(ports), 1, f"{row['environment']}: expected one 31xx Loki port")
            loki[row["environment"]] = ports[0]
        self.assertEqual(len(set(loki.values())), len(loki), f"Loki ports collide: {loki}")

    def test_dev_loki_port_is_the_compose_default(self):
        services, _, _ = load_compose(False)
        host = next(h for _, h, c, _ in port_mappings(services["lgtm"]) if c == "3100")
        default = int(re.search(r":-(\d+)\}", host).group(1))
        dev = next(r for r in self.rows if r["environment"] == "dev")
        self.assertIn(default, dev["ports"])

    def test_no_port_is_repeated_within_or_across_rows(self):
        seen = {}
        for row in self.rows:
            self.assertEqual(len(row["ports"]), len(set(row["ports"])),
                             f"{row['environment']} repeats a port")
            for port in row["ports"]:
                self.assertNotIn(port, seen,
                                 f"port {port} is in both {seen.get(port)} and {row['environment']}")
                seen[port] = row["environment"]


class HelperTests(unittest.TestCase):
    """The interpolator and the config parser carry the other tests; pin them."""

    def test_interpolate_nested_defaults(self):
        expr = "${GAMESERVER_ID:-gs-dotnet-${GAMESERVER_MAP_ID:-map_01}}"
        self.assertEqual(interpolate(expr, {}), "gs-dotnet-map_01")
        self.assertEqual(interpolate(expr, {"GAMESERVER_MAP_ID": "m2"}), "gs-dotnet-m2")
        self.assertEqual(interpolate(expr, {"GAMESERVER_ID": "x"}), "x")
        self.assertEqual(interpolate("${A:-d}", {"A": ""}), "d")
        self.assertEqual(interpolate("${A-d}", {"A": ""}), "")

    def test_parser_rejects_broken_configs(self):
        for broken in ('a "x" {\n b = "unterminated\n}\n', 'a "x" {\n b = [1, 2\n}\n',
                       'a "x" {\n b = 1\n', 'a "x" {\n b = 1\n}\n}\n'):
            with self.subTest(config=broken):
                with self.assertRaises(ConfigError):
                    parse_alloy(broken)

    def test_parser_ignores_comment_markers_inside_strings(self):
        blocks, _ = parse_alloy('a "x" {\n u = "http://h:1/p" // trailing\n}\n')
        self.assertEqual(strings_in(blocks[0].attrs["u"]), ["http://h:1/p"])


if __name__ == "__main__":
    unittest.main()
