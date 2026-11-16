"""The canonical `service` values, pinned on the compose side.

docs/MONITORING.md ("Canonical service values") is the one table. The Studio hub
filters logs by `service`, so the compose collector and the k8s collector must
produce exactly those values for the same role. This file checks the compose half
(the k8s half is CanonicalServiceTests in k8s/logging/tests):

  * the table says what the contract says (the core roles and their sources)
  * every compose service, base file and override, has a row, and every compose
    name in the table is a service that exists
  * the relabel rules of monitoring/alloy/config.alloy, run by the small evaluator
    of test_log_shipping.py, turn each compose service into its canonical value
  * the second map server (`gameserver-dotnet-map02`) gets the SAME service as the
    first and a DIFFERENT server_id

No Docker, no Alloy binary:

    python -m unittest discover -s backend/deploy/tests -p "test_*.py"
"""

import unittest

import test_log_shipping as CT

PROJECT = "rpg-mmo-meta"
ENV = {"COMPOSE_PROJECT_NAME": PROJECT, "DEPLOY_ENVIRONMENT": "local"}

# The documented core roles and where each one comes from on compose. A change
# here is a change of the contract with the Studio hub.
EXPECTED_COMPOSE = {
    "gameserver": ["gameserver-dotnet", "gameserver-dotnet-map02"],
    "gateway": ["gateway"],
    "nakama": ["nakama"],
    "redis": ["redis"],
    "postgres": ["postgres"],
    "postgres-game": ["postgres-game"],
    "lgtm": ["lgtm"],
    "docker-socket-proxy": ["docker-socket-proxy"],
    "alloy": ["alloy"],
}


def docker_target(service, labels=None):
    target = {
        "__meta_docker_container_label_com_docker_compose_project": PROJECT,
        "__meta_docker_container_label_com_docker_compose_service": service,
    }
    for key, value in (labels or {}).items():
        target["__meta_docker_container_label_" + "".join(c if c.isalnum() else "_" for c in key)] = value
    return target


class CanonicalServiceComposeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.table = CT.canonical_service_table()
        cls.services, _, _ = CT.load_compose(True)
        blocks, _ = CT.parse_alloy(CT.ALLOY_CONFIG.read_text(encoding="utf-8"))
        cls.rules = CT.rules_of(CT.component(blocks, "discovery.relabel", "containers"))

    def labels(self, service, labels=None):
        return CT.stream_labels(self.rules, docker_target(service, labels), ENV)

    def test_the_table_parses_and_services_are_unique(self):
        services = [row["service"] for row in self.table]
        self.assertGreaterEqual(len(services), len(EXPECTED_COMPOSE))
        self.assertEqual(len(services), len(set(services)), "a `service` value appears in two rows")

    def test_the_documented_core_roles_and_their_compose_sources(self):
        compose = {row["service"]: row["compose"] for row in self.table if row["compose"]}
        for service, sources in EXPECTED_COMPOSE.items():
            self.assertEqual(compose.get(service), sources, service)

    def test_core_roles_exist_on_both_platforms_under_the_same_value(self):
        both = {row["service"] for row in self.table if row["compose"] and row["k8s"]}
        for service in ("gameserver", "gateway", "nakama", "redis", "postgres", "postgres-game", "alloy"):
            self.assertIn(service, both)

    def test_every_compose_service_has_a_row_and_every_row_names_a_real_service(self):
        listed = {name for row in self.table for name in row["compose"]}
        self.assertEqual(set(self.services) - listed, set(),
                         "a compose service with no row in the canonical service table")
        self.assertEqual(listed - set(self.services), set(),
                         "a row names a compose service that does not exist")

    def test_each_row_is_what_the_collector_produces(self):
        for row in self.table:
            for name in row["compose"]:
                with self.subTest(service=row["service"], compose_service=name):
                    self.assertEqual(self.labels(name)["service"], row["service"])

    def test_game_server_services_are_gameserver_and_the_name_is_not_a_label_value(self):
        for name in ("gameserver-dotnet", "gameserver-dotnet-map02"):
            self.assertEqual(self.labels(name)["service"], "gameserver")
        # the compose service name does not leak through for any service of the table
        values = {self.labels(n)["service"] for row in self.table for n in row["compose"]}
        self.assertNotIn("gameserver-dotnet", values)
        self.assertNotIn("gameserver-dotnet-map02", values)

    def test_the_second_map_server_has_the_same_service_and_a_different_server_id(self):
        game_servers = {n: s for n, s in self.services.items()
                        if "GAMESERVER_MAP_ID" in s["environment"]}
        self.assertEqual(set(game_servers), {"gameserver-dotnet", "gameserver-dotnet-map02"})
        result = {}
        for name, svc in game_servers.items():
            labels = {k: CT.interpolate(v, {}) for k, v in svc["labels"].items()}
            result[name] = self.labels(name, labels)
        first, second = result["gameserver-dotnet"], result["gameserver-dotnet-map02"]
        self.assertEqual(first["service"], second["service"])
        self.assertEqual(first["service"], "gameserver")
        self.assertEqual((first["server_id"], second["server_id"]), ("gs-dotnet-map_01", "gs-dotnet-map_02"))
        self.assertEqual(first["game"], second["game"])

    def test_non_game_server_streams_carry_neither_game_nor_server_id(self):
        for row in self.table:
            if row["service"] == "gameserver":
                continue
            for name in row["compose"]:
                with self.subTest(compose_service=name):
                    self.assertEqual(set(self.labels(name)), {"env", "service"})


if __name__ == "__main__":
    unittest.main()
