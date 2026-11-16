# Monitoring — dev observability stack

One container gives the whole backend a metrics UI:

```bash
cd backend/deploy
make monitoring-up          # docker compose --profile monitoring up -d
```

| URL | What |
|-----|------|
| http://localhost:3000 | Grafana (`admin` / `localdev`, override with `GRAFANA_USER` / `GRAFANA_ADMIN_PASSWORD`) |
| http://localhost:9090/targets | Bundled Prometheus — scrape health |
| `localhost:4317` / `localhost:4318` | OTLP gRPC / HTTP ingest (traces, logs, metrics — for later) |
| `127.0.0.1:3100` | Bundled Loki query API (`LOKI_PORT`), loopback only, no authentication. Container logs (compose) and pod logs (k8s) are shipped here by Alloy — see "Logs" below |

`make monitoring-down` stops it, `make monitoring-logs` tails it, `make monitoring-targets` prints target health from the CLI.

> **Port already in use?** Grafana's 3000 is a popular port (Cocos Creator, Node dev
> servers…). Set `GRAFANA_PORT=3001` in `backend/deploy/.env` and re-run
> `make monitoring-up`. Docker Desktop silently drops a host binding it cannot
> take, so a missing mapping in `docker ps` is the symptom.

---

## Why `grafana/otel-lgtm` and not a hand-rolled stack

The obvious build is `prometheus` + `grafana` services with our own provisioning
files, and that was the first attempt. It was dropped in favour of the official
`grafana/otel-lgtm` image because:

- **One container instead of two-plus.** Grafana, Prometheus, Loki, Tempo,
  Pyroscope and an OpenTelemetry Collector, already wired to each other
  (datasources provisioned, exemplars linked Prometheus→Tempo→Loki).
- **Nothing to maintain.** Datasource UIDs, versions and cross-links are the
  image's problem. We own a handful of small files (see below).
- **OTLP is already listening.** The moment the C# game server or the gateway
  emits traces or structured logs, `:4317`/`:4318` accept them — no second
  round of infrastructure work. A hand-rolled Prometheus would have needed
  Loki + Tempo + a collector bolted on later.
- **It is Grafana's own dev-stack image**, so the graduation path to Grafana
  Cloud (below) is the documented one.

Trade-off: it is a *dev* stack. Single binary set, single node, no HA. Loki's
retention is the one policy we did tune (7 days, see "Logs"); Prometheus, Tempo
and Pyroscope keep the image's defaults. Production goes to Grafana Cloud or
kube-prometheus-stack — see the graduation paths at the bottom.

## What we own

Files under `backend/deploy/monitoring/`. Four are mounted into the `lgtm` image
and one into the `alloy` container; two more are the check and its helper:

| File | Mounted at | Purpose |
|------|-----------|---------|
| `prometheus.yaml` | `/otel-lgtm/prometheus.yaml` | Scrape config (the documented override point of the image) |
| `grafana-dashboards.yaml` | `/otel-lgtm/grafana/conf/provisioning/dashboards/rpg-dashboards.yaml` | Extra dashboard provider, added *alongside* the image's own |
| `dashboards/rpg-gameplay.json` | `/otel-lgtm/dashboards-rpg/` | The "RPG Gameplay" dashboard |
| `loki-config.yaml` | `/otel-lgtm/loki-config.yaml` | Replaces the image's Loki config to turn on 7-day retention (see "Logs") |
| `alloy/config.alloy` | `/etc/alloy/config.alloy` (in `alloy`) | Log collector: discovery, labels, push to Loki (see "Logs") |
| `check-log-shipping.sh` | not mounted | Live check of log shipping against a running stack |

The `alloy` and `docker-socket-proxy` services in `docker-compose.yml`
(profile `monitoring`) are ours too, and `tests/test_log_shipping.py` holds the
offline tests for all of the above. The k8s collector is a second Alloy under
`k8s/logging/` (see "Logs on k8s"); its `level` stage is the block of
`alloy/config.alloy` between the `shared level pipeline` markers, so editing that
block means regenerating `k8s/logging/40-alloy-config.yaml`.

Plus the `lgtm-data` volume on `/data` — every bundled component persists there,
so restarts keep the TSDB, Grafana state and Loki chunks.

### How extra scrape targets work

`grafana/otel-lgtm` documents two override points: mount your own
`/otel-lgtm/prometheus.yaml` (or `/otel-lgtm/otelcol-config.yaml`), or pass
`PROMETHEUS_EXTRA_ARGS` / `OTELCOL_EXTRA_ARGS`. We mount `prometheus.yaml` —
simplest working path: pull-based scraping of processes that already speak
Prometheus, with no collector pipeline in between.

The mount **replaces** the image's file, so `monitoring/prometheus.yaml` starts
with a verbatim copy of the image's `otlp:` and `storage:` blocks (OTLP push
keeps working) and only adds `scrape_configs`. When bumping
`OTEL_LGTM_VERSION`, re-copy those two blocks:

```bash
docker run --rm --entrypoint sh grafana/otel-lgtm:<tag> -c 'cat /otel-lgtm/prometheus.yaml'
```

Targets:

| Job | Target | Notes |
|-----|--------|-------|
| `nakama` | `nakama:9100` | compose service, `--metrics.prometheus_port 9100` |
| `gateway` | `host.docker.internal:9102` | host-run `go run ./cmd/gateway/` |
| `gameserver` | `host.docker.internal:9101` | host-run C# `dotnet run` |
| `gateway-container` | `gateway:9102` | up with `--profile realtime` (i.e. `DEPLOY_MODE=containers`) |
| `gameserver-container` | `gameserver-dotnet:9101` | same profile |

`host.docker.internal` resolves inside the container because the service
declares `extra_hosts: host.docker.internal:host-gateway` (needed on a plain
Linux engine; Docker Desktop provides it anyway). Targets for processes that are
not running show as **DOWN** — that is normal in a partial dev stack, not a
misconfiguration.

## Metrics contract

### Gateway (Go, `backend/gateway/metrics`)

Exposed on a **separate listener** from the realtime port — `--metrics-addr`
(env `METRICS_ADDR`), default `:9102`; `off`/`none`/empty disables it.
`/metrics` is promhttp, `/healthz` returns 200 while the process lives
(liveness probe; use it for k8s `livenessProbe`).

| Metric | Type | Labels | Meaning |
|--------|------|--------|---------|
| `gateway_connections_active` | gauge | — | client sockets currently held |
| `gateway_auth_total` | counter | `game`, `result=ok\|fail` | MsgAuth outcomes |
| `gateway_enter_world_total` | counter | `game`, `result=ok\|fail` | MsgEnterWorld / map assignment outcomes |
| `gateway_allocations_total` | counter | `game`, `result=ok\|fail` | allocator (Agones) requests |
| `gateway_session_checks_total` | counter | `game`, `result=ok\|expired\|store_error` | session validations; `store_error` is Redis failing, not a session ending |
| `gateway_relay_events_total` | counter | — | cross-server events delivered by the relay |

Plus further unlabelled or non-game series (`gateway_rate_limited_total{reason}`,
`gateway_kick_publish_total{result}`, `gateway_redis_up`, `gateway_relay_up`,
`gateway_transport_*`, ...; `backend/gateway/metrics/metrics.go` is the list)
and the standard `go_*` and `process_*` collectors.

**`game` (ADR-33 D3)** is a game id from the gateway's `GAMES` list (`cuvara`
when `GAMES` is unset) taken from a **verified** token claim only. Anything
else, including a failed auth whose claim could not be verified, is counted
under `game="other"`, so a forged `gid` cannot mint series. Every
`(game, result)` pair of a configured game is pre-created at startup so
`rate()` has a zero baseline instead of a series that pops into existence on
the first failure; `other` is not pre-created.

### The C# metrics endpoint binds a wildcard (fixed 2026-08-06)

`METRICS_ADDR=:9101` (also `0.0.0.0:9101` / `*:9101`) binds `http://+:9101/` and
answers on **any** `Host`, so the in-network scrape (`gameserver-dotnet:9101`),
the host-mode scrape (`host.docker.internal:9101`) and a plain host probe
(`127.0.0.1:9101`) all work:

```bash
curl http://127.0.0.1:9101/healthz   # ok — no Host header needed
curl http://127.0.0.1:9101/metrics   # gameserver_* series
```

Historical note, kept because the workaround outlived the bug by weeks: OTel's
`PrometheusHttpListener` used to build its prefix with
`new UriBuilder("http", Host, Port)`, and `UriBuilder` rejects the HttpListener
wildcards `+` / `*` (`UriFormatException: Invalid URI: The hostname could not be
parsed`), so the endpoint never started. The process kept serving the game — only
observability was lost — so the symptom was just a scrape target stuck **DOWN**,
indistinguishable from "the process is not running". Compose worked around it by
setting `GAMESERVER_METRICS_ADDR` to the compose service name, which made probing
from the host require `curl -H 'Host: gameserver-dotnet:9101'`.
`MetricsEndpoint.cs` now rewrites the listener prefix in `ConfigureHttpListener`
(which runs before `Start`), so the workaround is gone. If a wildcard bind ever
regresses, fix it there — do **not** re-pin `GAMESERVER_METRICS_ADDR` to a name.

### C# metric names arrive with dots unless the scrape says otherwise

The C# exporter serves **OpenTelemetry instrument names**, which contain dots
(`gameserver.tick.duration`). Prometheus 3 negotiates UTF-8 metric names by
default, so it accepts and stores them verbatim — and every dashboard panel,
which queries `gameserver_tick_duration_seconds_bucket`, returns *No data*
against a target that reads perfectly **UP**. A green target list is therefore
not proof the dashboard works.

`monitoring/prometheus.yaml` pins `metric_name_escaping_scheme: underscores` on
both game server jobs, which makes the exporter emit the underscore form the
dashboard expects. Verify after any Prometheus or exporter bump:

```bash
curl -s http://127.0.0.1:9090/api/v1/label/__name__/values | grep -o 'gameserver[^"]*' | head
# want gameserver_players_online, NOT gameserver.players.online
```

### Game server (C#, `:9101`)

Owned by the game server module (`GameServer/Observability/GameMetrics.cs`
lists every instrument). The dashboard consumes
`gameserver_tick_duration_seconds` (histogram), `gameserver_players_online`
(gauge) and `gameserver_player_saves_total{status="error"}` (counter); the
alerts consume `gameserver_nakama_reward_outcomes_total{outcome}` and
`gameserver_players_online`. Panels stay empty until that exporter is live.

**Every `gameserver_*` series carries `map_id` and `game`** (ADR-33 D3), from
one base tag set, so no instrument can drop either. `game` is the process's
`GAMESERVER_GAME_ID` (`cuvara` when unset). One process serves one game, so
the label adds no series; it exists because a `map_id` is only unique within a
game (two games may both have a `map_01`).

> Until 2026-09-13 this said `gameserver_save_errors_total`, which **has never
> existed** — the instrument is `gameserver.player.saves`, exported as
> `gameserver_player_saves_total` with a `status` label. The save-errors panel
> therefore read "No data" for its whole life, and "no data" on an error panel
> looks exactly like "no errors". Checked against a running pod's `/metrics`:
> every other metric named in this file does exist.

### Nakama (`:9100`)

Built-in Prometheus endpoint; useful series are `nakama_api_count`,
`nakama_socket_count`, `nakama_db_*`.

## Dashboard — "RPG Gameplay"

Provisioned (uid `rpg-gameplay`), deliberately minimal — the five numbers that
tell you whether the game is healthy:

| Panel | Query | Read it as |
|-------|-------|-----------|
| Tick duration p99 | `histogram_quantile(0.99, sum by (le, game, map_id) (rate(gameserver_tick_duration_seconds_bucket[5m])))` | Must stay far below the tick budget (66ms at 15Hz). Crossing it = the sim is late, clients rubber-band. |
| Players online | `sum by (game, map_id) (gameserver_players_online)` | Load per map of each game + total CCU across games. |
| Gateway connections active | `sum by (instance) (gateway_connections_active)` | Should track players online; a growing gap means sockets leak or clients stall before EnterWorld. |
| Auth / EnterWorld failure ratio | `rate(gateway_auth_total{result="fail"}[5m]) / rate(gateway_auth_total[5m])` | Sustained >10% = JWT secret mismatch with Nakama, expired tokens, or (EnterWorld) no server has capacity. |
| Save errors + allocation failures | `rate(gameserver_player_saves_total{status="error"}[5m])`, `rate(gateway_allocations_total{result="fail"}[5m])` | Anything non-zero is player progress at risk / Agones unable to give us pods. |
| Scrape targets up | `max by (job) (up)` | Which exporters are alive. |

**Per-map means per `(game, map_id)`** (ADR-33 D3). A map id is only unique
within a game, so every panel and alert rule that splits by map groups by
`(game, map_id)`, never by `map_id` alone: `by (le, game, map_id)` for the tick
histogram, `by (game, map_id)` elsewhere, `on (game, map_id)` for the join in
`NakamaRewardPathSilent`, and legends and alert annotations print
`<game>/<map_id>`. Grouping by `map_id` alone would sum two games' `map_01`
into one line, and the reward alerts would compare one game's failures with
another game's traffic. With only cuvara deployed every series reads
`cuvara/<map_id>`. The gateway panels are whole-gateway ratios and do not split
by map; split them by `game` ad hoc (`sum by (game) (...)`) when needed.
Follow the same rule for any new panel or rule.

Editing: change it in Grafana, then **Export → Save to file** back into
`monitoring/dashboards/rpg-gameplay.json` (provisioned dashboards are read-only
on disk — UI edits are lost on container recreate).

### Infra dashboards (import by ID, not bundled)

Host/DB/cache dashboards are community-maintained and version-churn a lot, so
they are not vendored. Once the matching exporter exists, import in Grafana via
**Dashboards → New → Import**:

| ID | Dashboard | Requires |
|----|-----------|----------|
| 1860 | Node Exporter Full | `node_exporter` on the VPS |
| 763 | Redis Dashboard for Prometheus Redis Exporter | `redis_exporter` |
| 9628 | PostgreSQL Database | `postgres_exporter` |
| 12740 | Kubernetes / Agones-friendly cluster view | kube-state-metrics (k3s tier) |

## Logs — container stdout to Loki

The `monitoring` profile also ships the container logs of the compose stack to
the Loki bundled in `lgtm`, where Grafana's Loki datasource (provisioned by the
image) reads them. This section says how a line gets there, what was decided and
why, and how to query it. It covers compose; the k8s deployment has its own
subsection, "Logs on k8s", after "The check and the offline tests".

> **Status of verification.** Everything below describes what the files in this
> directory do. The offline tests (`tests/test_log_shipping.py`) check the
> compose file, the Alloy config, `loki-config.yaml`, `environments.tsv` and the
> CD file list without Docker. **The live check
> (`monitoring/check-log-shipping.sh`) has not been run**: no stack was started
> while this was written, so no line has been seen arriving in Loki, the bundled
> Loki (v3.5.7 in `grafana/otel-lgtm` 0.11.15) has not been observed loading our
> `loki-config.yaml`, and Alloy v1.11.3 has not been observed loading
> `config.alloy`. Run the check on a real stack before relying on any of it (see
> "The check and the offline tests").
>
> **First live run: what has to be observed.**
>
> 1. Alloy v1.11.3 loads `config.alloy` and a gateway line reaches Loki with
>    `env`, `service` and `level`.
> 2. The bundled Loki loads `loki-config.yaml` and `GET /config` shows
>    `retention_period` 1w and `retention_enabled: true`.
> 3. The copied blocks of `loki-config.yaml` are identical to
>    `/otel-lgtm/loki-config.yaml` inside the image `grafana/otel-lgtm:0.11.15`
>    (`docker run --rm --entrypoint sh grafana/otel-lgtm:0.11.15 -c 'cat
>    /otel-lgtm/loki-config.yaml'`). Until then they are only compared with the
>    repository file at the tag.
> 4. A game server line carries a real `level`. This needs a build that includes
>    the `gameserver-json-logs` task; without it the check FAILs on purpose.
> 5. The proxy's behaviour on `archive`, `export` and `attach/ws` (the residual
>    risk below) is observed, not assumed.

### How a line flows

```
container stdout/stderr
  -> Docker engine (json-file logs)
  -> docker-socket-proxy   read-only filter on the Docker API (GET only)
  -> alloy                 discovery.docker + loki.source.docker + loki.process
  -> lgtm : Loki :3100     /loki/api/v1/push, inside the compose network
  -> Grafana (Explore, Loki datasource)   or   curl 127.0.0.1:${LOKI_PORT}
```

- **`docker-socket-proxy`** (`tecnativa/docker-socket-proxy`, version in
  `DOCKER_SOCKET_PROXY_VERSION`, default `0.3.0`) is the only container that
  mounts `/var/run/docker.sock`. It publishes no port and sits on the
  `log-collector` network, which is `internal: true` and joined only by the proxy
  and `alloy`.
- **`alloy`** (`grafana/alloy`, version in `ALLOY_VERSION`, default `v1.11.3`)
  mounts **no** Docker socket. It reads `monitoring/alloy/config.alloy`
  (bind-mounted read-only), keeps its read positions in the `alloy-data` volume
  so a restart continues instead of re-shipping, publishes no port (its UI on
  `:12345` stays inside the network), and starts after `lgtm` is healthy.
- **Loki** is the one inside `lgtm`. Alloy pushes to `http://lgtm:3100` on the
  compose network; the host port exists only for queries from the host.

### Decision: a socket proxy, not the log files

Two ways to give a collector the logs were considered. The collector must not
hold a writable Docker socket: anything that can `POST` to the socket can start a
privileged container, which is root on the host.

| | Read the log files | Read-only socket proxy (chosen) |
|---|---|---|
| Mechanism | bind-mount `/var/lib/docker/containers` read-only into Alloy | the proxy mounts the socket; Alloy speaks to the proxy over TCP |
| Consequence | The hosts here run Docker Desktop through WSL. The engine's container directory lives in the engine's VM and cannot be bind-mounted from the deploy directory, so this would work on a plain Linux VPS and not on the dev box. The files also carry no compose labels, so `service`, `game` and `server_id` would have to be guessed from container ids. | Works the same on Docker Desktop/WSL and on a Linux VPS. Discovery returns the compose labels, which is where `service`, `game` and `server_id` come from. |

**What the proxy allows.** `POST=0`, so no write verb passes whatever the path.
Of the read sections only `CONTAINERS`, `NETWORKS`, `PING` and `VERSION` are on:
container list, inspect and logs (what `discovery.docker` and
`loki.source.docker` call), the network list (discovery resolves addresses
through it), and the ping and version calls the Docker client makes to negotiate
an API version. `EVENTS` is off because Alloy polls (`refresh_interval =
"10s"`). Every other section (`INFO`, `AUTH`, `BUILD`, `COMMIT`, `CONFIGS`,
`DISTRIBUTION`, `EXEC`, `GRPC`, `IMAGES`, `PLUGINS`, `SECRETS`, `SERVICES`,
`SESSION`, `SWARM`, `SYSTEM`, `TASKS`, `VOLUMES`, `ALLOW_START`, `ALLOW_STOP`,
`ALLOW_RESTARTS`) is set to `0` explicitly, so a default that changes in a later
image tag cannot widen the surface silently. The `:ro` on the socket mount is
**not** the protection (a read-only mount of a socket can still be written to);
the proxy's method and path filter is. The offline tests assert these settings,
that the proxy publishes no port, and that its network is internal.

**Residual risk, stated plainly.**

- **Container inspect exposes each container's environment to the collector,
  secrets included** (database passwords, the JWT and join-token secrets, the
  Grafana admin password). Alloy's config does not use them and drops the
  discovery meta labels, but a compromised Alloy could read them.
- The proxy filters by method and path section, so with `CONTAINERS=1` every
  `GET` under `/containers/` passes, not only list, inspect and logs. By the
  proxy's design that includes endpoints that read a container's filesystem
  (`archive`, `export`) and `GET /containers/{id}/attach/ws`, the websocket
  attach, which can carry stdin. Both are properties of the image that were not
  tested here (the attach endpoint stays untested); treat the collector as able
  to read everything about the stack's containers.
- What limits it: the proxy has no published port and sits on an internal network
  that only it and Alloy join, and Alloy has no published port, so nothing outside
  the compose project can reach the proxy. Anything that can run a container on
  that network already has the Docker daemon.
- What the proxy buys: a compromised Alloy cannot start, stop or create
  containers, and cannot talk to the socket directly.

### Which containers are collected

Only the containers of **this compose project**. Several stacks (dev, staging,
production, scratch) can share one Docker daemon, so discovery is filtered on
`com.docker.compose.project=$COMPOSE_PROJECT_NAME`, and a relabel `keep` on the
same label backs it up. `COMPOSE_PROJECT_NAME` (default `rpg-mmo-meta`) and
`DEPLOY_ENVIRONMENT` (default `local`) are passed into the Alloy container from
`deploy/.env`: CD writes both, and `./stack.sh up --scratch` writes the scratch
values (`COMPOSE_PROJECT_NAME=<scratch project>`, `DEPLOY_ENVIRONMENT=scratch`,
`LOKI_PORT=4100`). The project name has to be written into the env file because
the project is set with `-p`, which a container cannot see. Every container of
the project is shipped, including Alloy's own and the proxy's.

### Label contract

This is the contract with the Studio hub's log view. The stream labels are
exactly these, enforced by a final `stage.label_keep`:

| Label | On | Source |
|---|---|---|
| `env` | every stream | `DEPLOY_ENVIRONMENT`, the identity in `environments.tsv` (`local` on a bare local stack) |
| `service` | every stream | the compose service name (`com.docker.compose.service`): `gateway`, `nakama`, `gameserver-dotnet`, `gameserver-dotnet-map02`, `lgtm`, `alloy`, … |
| `level` | every stream | parsed from the line, see below |
| `game` | game server streams only | compose label `cuvara.log.game` = `${GAMESERVER_GAME_ID:-cuvara}`: the default game is `cuvara`, also when the variable is set but empty |
| `server_id` | game server streams only | compose label `cuvara.log.server_id` = the service's own server id (`${GAMESERVER_ID:-gs-dotnet-${GAMESERVER_MAP_ID:-map_01}}`; the override's second server is the literal `gs-dotnet-map_02`) |

Rules:

- **A stream without `game` is stack-wide** (gateway, Nakama, Redis, Postgres,
  lgtm, Alloy). Only game servers carry `game` and `server_id`. Every game server
  service in `docker-compose.yml` and `docker-compose.override.yml` must carry
  both compose labels; the offline tests fail when one is missing or disagrees
  with the service's `GAMESERVER_GAME_ID` / `GAMESERVER_ID`.
- **Never a high-cardinality label.** Every distinct label set is a Loki stream.
  Nothing is built from a container id or name, a user id, a connection id or a
  player. Discovery's `__meta_docker_*` labels are dropped and `label_keep`
  removes anything else (`container`, `filename`, …). Put per-request detail in
  the line and filter it with LogQL (`|= "..."`, `| json`), not in a label.
- **`level`** is read only from a line that looks like a JSON object (first
  non-space character `{`), from its `level` field, and normalised to one
  spelling per level: `trace` (trace, trc), `debug` (debug, dbg), `info` (info,
  inf), `warn` (warn, wrn), `error` (error, err), `fatal` (fatal, critical, crit,
  panic, dpanic). `information` and `warning` are also accepted, as harmless
  aliases: no service here prints them. What the services really write:

  | Service | Format | `level` spellings written |
  |---|---|---|
  | `gateway` | Go slog, JSON | `INFO` `WARN` `ERROR` `DEBUG` |
  | `nakama` | zap, JSON | `info` `warn` `error` `debug` `dpanic` `panic` `fatal` |
  | `gameserver-dotnet*` | one JSON object per line: `ts`, `level`, `category`, `msg`, optional `exception`, `game`, `map_id`, `server_id` | `TRACE` `DEBUG` `INFO` `WARN` `ERROR` `CRITICAL` (`NONE` exists but is never a logged level) |

  **Anything else is `unknown`**: a plain line, a line that does not parse, a
  missing field, a number, a custom word. The closed set is deliberate: an open
  one would mint a stream per distinct value. This change does not alter what the
  services print.
- **Dependency: the game server's `level` needs the JSON-logs task.** On the
  branch that holds this change alone, the C# game server still logs plain text
  (the .NET simple console formatter: a header line such as `info: Category[0]`
  followed by indented message lines). The task `gameserver-json-logs` of the
  same plan (branch `feat/server/log-shipping-gameserver-json-logs`, integrated
  with this one into `feat/server/log-shipping`) makes the game server log JSON
  by default. Until it is in the build, and whenever
  `GAMESERVER_LOG_FORMAT=text` is set to keep the plain output, **every game
  server line ships with `level="unknown"`** (`env`, `service`, `game` and
  `server_id` are unaffected, they come from compose labels). There is no regex
  for the plain header on purpose: it would split one event over two streams, the
  header and its indented message lines. Compose does not forward
  `GAMESERVER_LOG_LEVEL` or `GAMESERVER_LOG_FORMAT`; that is that task's decision
  and this one does not change it. The k8s ConfigMaps do carry them (see "Log
  level and format knobs on k8s").
- **Lines are shipped as written.** Non-JSON lines are not rewritten, dropped or
  wrapped, and JSON lines are only read, never changed.

### Retention: 7 days, enforced

The image starts Loki as `./loki/loki --config.file=./loki-config.yaml` from
`/otel-lgtm` and has no extra-args hook for Loki (Prometheus and the collector
have one), so no flag or variable sets retention. The only way to get a retention
period out of the bundled Loki is to own its config file: `lgtm` mounts our
`monitoring/loki-config.yaml` read-only over `/otel-lgtm/loki-config.yaml`, the
same pattern as `prometheus.yaml`.

- **Copied verbatim** from `grafana/docker-otel-lgtm` v0.11.15
  (`docker/loki-config.yaml`), the tag that matches `OTEL_LGTM_VERSION`:
  `auth_enabled`, `server`, `common`, `schema_config`, `ruler`,
  `pattern_ingester`, `ingester`, `frontend`, `query_scheduler`. The copy was
  compared with the repository file at that tag (fetched from
  `raw.githubusercontent.com` on 2026-11-16, and the copied blocks match it;
  upstream has no `compactor` and no `limits_config` block, so nothing of
  upstream is replaced by ours). It was **not** compared with the file inside the
  image `grafana/otel-lgtm:0.11.15`; that comparison is on the first-live-run
  list below.
- **Ours**, marked `OURS` in the file: `compactor` (`retention_enabled: true`,
  `delete_request_store: filesystem`, working directory under `/data/loki`, i.e.
  the `lgtm-data` volume) and `limits_config.retention_period: 168h`. Loki deletes
  through the compactor, so both are needed; `retention_period` alone is accepted
  and silently never applied.
- **On an `OTEL_LGTM_VERSION` bump, re-copy the copied blocks**, like
  `prometheus.yaml`, then re-apply the two `OURS` blocks. Otherwise Loki runs a
  config written for an older image:

  ```bash
  docker run --rm --entrypoint sh grafana/otel-lgtm:<tag> -c 'cat /otel-lgtm/loki-config.yaml'
  ```

- Deletion lags the cut-off by `retention_delete_delay` (Loki's default is 2h)
  plus one compaction pass, so expect data slightly older than 7 days.

**How to check it on a running stack.** Loki prints its effective configuration:

```bash
curl -s http://127.0.0.1:${LOKI_PORT:-3100}/config | grep -A3 -E '^(compactor|limits_config):'
# want  limits_config -> retention_period: 1w   (Loki may print 168h as 1w)
#       compactor     -> retention_enabled: true
```

`check-log-shipping.sh` does the same and accepts `168h`, `1w` or `7d`. The
offline tests only prove that the file says 168h and enables the compactor; they
do not prove that the running Loki loaded it, and that has not been observed yet
(see the status note above).

### Loki is loopback only, without authentication

Loki has no authentication in this stack and none is added, so nothing may
listen beyond loopback. The `lgtm` port mapping is `127.0.0.1:${LOKI_PORT}:3100`
with a **literal** `127.0.0.1`: there is deliberately no `LOKI_BIND` variable, so
no setting can widen it, and the override file keeps it that way. The offline
tests assert the literal in the base file and with the override; the live check
asserts it with `docker port`.

The k8s collector does not change this: it reaches the same Loki over the
cluster's Docker network and publishes no port (see "Decision: how the DaemonSet
reaches Loki"), at the price of the residual risk recorded there.

`LOKI_PORT` differs per environment so that stacks sharing a host do not
collide, and `environments.tsv` reserves it like the other ports (the isolation
preflight checks it):

| Environment | `LOKI_PORT` |
|---|---|
| dev (variable unset) | 3100 |
| production | 3110 |
| staging | 3120 |
| scratch stack (`stack.sh up --scratch`) | 4100 |

### How to query

In Grafana: **Explore**, choose the Loki datasource, pick labels or paste LogQL.
From the host, against the loopback port:

```bash
LOKI=http://127.0.0.1:${LOKI_PORT:-3100}
logql() { curl -s -G "$LOKI/loki/api/v1/query_range" --data-urlencode "query=$1" --data-urlencode limit=20; }

logql '{env="local", service="gateway"}'                       # one service
logql '{env="local", level="error"}'                           # errors anywhere in the stack
logql '{env="local", service="nakama", level=~"warn|error"}'   # a severity range
logql '{game="cuvara"}'                                        # every game server of a game
logql '{game="cuvara", server_id="gs-dotnet-map_02"}'          # one game server
logql '{env="local", game=""}'                                 # stack-wide streams only (no game label)
logql '{env="local", service="gateway"} |= "timeout"'          # text filter inside the lines
logql '{service="gameserver-dotnet", level="warn"}'            # game server warnings: filter on the level label
logql '{service="gameserver-dotnet"} | json | msg=~"(?i).*disconnect.*"'   # a field inside the JSON body
```

The `level` label is the way to filter by severity, for every service. The last
example needs the game server to log JSON (see the dependency note above) and
uses the `msg` field of its JSON line; the pattern is only an example. On a game
server that logs plain text, `| json` finds nothing and `level` is `unknown`;
use a text filter (`|= "..."`) there.

`env` is the value of `DEPLOY_ENVIRONMENT` (`dev`, `staging`, `production`,
`local`, `scratch`). Without `start`/`end`, `query_range` looks at the last hour.
Label names: `curl -s $LOKI/loki/api/v1/labels`.

### The check and the offline tests

- **`monitoring/check-log-shipping.sh`** travels to hosts with `monitoring/` and
  needs bash, curl and docker only. Against a **running** `monitoring` profile it
  checks that Loki's host port is bound to `127.0.0.1` only; that Loki reports
  retention of 168h and the compactor enabled; that a marker line written by a
  short-lived container (started from the host's Docker CLI with the compose
  project and a service label set by hand, so no stack container needs a socket)
  reaches Loki with `env`, `service` and `level` and no id-like label, once as a
  JSON line (`{"level":"WARN",...}`, expected `level="warn"`, body unchanged) and
  once as a plain line (`level="unknown"`); and, when the `realtime` profile is
  up, that a gateway stream and a game server stream exist with `env`,
  `service`, `level` and, for the game server, `game` and `server_id` matching
  its compose labels (SKIP when that profile is down). The game server stream
  must carry a **real** level, one other than `unknown`: the check FAILs for a
  game server that logs plain text, and says that the game server must log JSON
  (`GAMESERVER_LOG_FORMAT=json`, a build that includes the JSON-logs task). Output is `PASS:`/`FAIL:`/`SKIP:` lines and the exit
  status is 1 on any FAIL. Inputs: `COMPOSE_PROJECT_NAME`, `DEPLOY_ENVIRONMENT`,
  `LOKI_PORT`, `CHECK_TIMEOUT`. **NOT_RUN**: starting the stack needs the owner's
  go-ahead.

  ```bash
  cd backend/deploy
  docker compose --profile monitoring --profile realtime up -d
  bash monitoring/check-log-shipping.sh
  ```

- **`tests/test_log_shipping.py`** (Python unittest with PyYAML; no Docker, no
  Alloy binary; CI runs it in the `test-deploy-offline` job):
  `python -m unittest discover -s backend/deploy/tests -p "test_*.py"`. It covers
  pinned images, no Docker socket on `alloy`, a read-only proxy with writes off,
  an internal proxy network and no published port, the Loki port on a literal
  `127.0.0.1` with and without the override, every monitoring bind mount existing
  and listed in `cd.yml`'s required files, the Alloy config's structure and label
  rules, the game server compose labels, the 168h retention and compactor, and one
  distinct Loki port per row of `environments.tsv`. Alloy's syntax is checked only
  structurally (balanced braces and quotes, resolvable component references);
  Alloy itself is not run.

### Logs on k8s (`DEPLOY_MODE=k8s`)

When the realtime tier runs in a k3d cluster, the pod logs of the cluster go to
the **same Loki**, through a second collector: an Alloy **DaemonSet** in its own
namespace `rpg-k8s-logging` (`k8s/logging/`). The compose collector above only
sees containers of its own compose project, and in k8s mode the gateway, Nakama,
the data tier and the game servers are pods, not compose containers.

> **Status of verification.** Everything in this section describes what the files
> in `backend/deploy/k8s/logging/` do. **Nothing was applied to a cluster and no
> line was seen arriving in Loki**, so every run-time behaviour below is **not yet
> observed**. The offline tests (`k8s/logging/tests/`, `k3s/tests/`,
> `k8s/verify/tests/log_collector_test.sh`) read the manifests and run
> `connect-loki.sh` against fakes. "First live run on k8s" at the end of this
> section lists what has to be observed.

#### How a line flows on k8s

```
container stdout/stderr
  -> kubelet / container runtime          keeps the log of each container
  -> kube-apiserver  GET .../pods/<pod>/log     (pods/log)
  -> alloy DaemonSet (rpg-k8s-logging)    discovery.kubernetes + loki.source.kubernetes + loki.process
  -> Service loki (rpg-k8s-logging)       selector-less, port 3100
  -> EndpointSlice loki-lgtm              the address of the lgtm container on the Docker network k3d-<cluster>
  -> lgtm : Loki :3100                    /loki/api/v1/push, the same Loki as compose
  -> Grafana (Explore, Loki datasource)   or   curl 127.0.0.1:${LOKI_PORT}
```

- Alloy reads logs through the Kubernetes API. It mounts **no hostPath** and
  cannot read any file of the node.
- It runs once per node and ships only the pods of its own node (`NODE_NAME` from
  the downward API, a server-side field selector plus a second `keep` rule).
- The image is `grafana/alloy:v1.11.3`, the same version as the compose default
  `ALLOY_VERSION`; a test asserts the two are equal. Its read positions live in an
  `emptyDir`, so what happens to the lines around a pod restart is not yet
  observed.
- `k8s/logging/` holds `00-namespace.yaml`, `10-serviceaccount.yaml` (account
  `rpg-log-collector`), `20-rbac.yaml`, `30-alloy-env.yaml` (ConfigMap
  `alloy-env`), `40-alloy-config.yaml` (generated ConfigMap `alloy-config`),
  `50-daemonset.yaml`, `60-loki-service.yaml` (Service `loki`) and
  `connect-loki.sh`, which writes the EndpointSlice. The namespace is labelled
  Pod Security `restricted` in `warn` and `audit` mode (not `enforce`), so a
  violation is reported at apply time and a Kubernetes upgrade cannot be what
  stops logging.
- The `level` stage is **the compose one**, not a copy kept by hand.
  `k8s/logging/generate-config.py` copies the block between the markers
  `// BEGIN shared level pipeline` and `// END shared level pipeline` of
  `monitoring/alloy/config.alloy` into the k8s half
  (`k8s/logging/config-k8s.alloy`) and writes the result into
  `40-alloy-config.yaml`. That file is generated: do not edit it. After changing
  either source, run `python3 backend/deploy/k8s/logging/generate-config.py`;
  `generate-config.py --check` exits 1 on drift, and CI runs it.

#### Decision: how the DaemonSet reaches Loki (option A)

In k8s mode the monitoring stack still runs under compose on the host. Loki is
the one inside the `lgtm` container, published only on a literal `127.0.0.1`. A
pod cannot reach the host's loopback, and widening that bind is exactly what must
not happen (see "Loki is loopback only, without authentication"). Three ways were
considered:

| Option | What it does | Verdict |
|---|---|---|
| **A. Attach `lgtm` to the cluster's Docker network** | `connect-loki.sh` runs `docker network connect k3d-<cluster> <prefix>-lgtm` when it is not connected yet, reads the container's address on that network, and writes it into an EndpointSlice of the selector-less Service `loki`. Alloy pushes to `http://loki.rpg-k8s-logging.svc.cluster.local:3100/loki/api/v1/push`. | **Chosen** by the run lead. |
| A + NetworkPolicy | A, plus an egress policy in every `rpg-k8s-*` namespace that blocks the Loki address for everything except the collector. | Considered, **not done**: it would put the egress of every app pod under policy for the first time, on a path (Agones, hostPort, KCP/UDP) that nobody could test live, and the address in the policy changes whenever `lgtm` is recreated. |
| B. Authenticated push-only proxy | A small reverse proxy in compose in front of Loki that accepts only `POST /loki/api/v1/push` with a bearer token and joins the k3d network instead of `lgtm`. | **Recorded follow-up**, not built: it needs a new image and service in `docker-compose.yml`, a new secret on both sides and `cd.yml` changes. |

Why A: **no port is published anywhere.** There is no new host port, the
loopback bind of `lgtm` is unchanged, and a Docker bridge network is not routable
from outside the host. Production runs compose, so this concerns dev and staging
k3d only. The residual risk is not wider than what the host has today, where any
process on the host can already reach Loki on loopback.

**Residual risk, stated plainly.**

- **Loki stays unauthenticated.** After the attach, every container on the Docker
  network `k3d-<cluster>` can reach it, and that includes **every pod of that
  cluster**. A pod can both read and write that environment's logs.
- So **a compromised pod of one game could read another game's logs** on that
  cluster, and the gateway's, Nakama's and the databases' logs too. The logs are
  as exposed as the least trusted pod of the cluster.
- Nothing is published beyond the host, and the loopback bind is unchanged. The
  attach is per cluster: it joins the `lgtm` of one environment to the network of
  one cluster.
- **A recreated `lgtm` loses the attachment** and gets a new address until
  `connect-loki.sh` or `dev-up.sh` runs again. While the Service `loki` has no
  address, or a stale one, Alloy's pushes fail and retry, and its buffer is
  eventually dropped. `cluster.log_collector` WARNs when the address is missing.
- The durable form of the attachment is an external network in
  `docker-compose.yml`, which this change does not own.
- **Option B is the recorded follow-up** (the collector would hold a push-only
  credential, and no pod could read logs). A NetworkPolicy was considered and not
  done (table above).

What `connect-loki.sh` does, so that its power can be judged:

- It runs `docker network connect` and one `docker inspect` whose `--format` reads
  only `.NetworkSettings.Networks` and prints one IPv4 address. There is no plain
  `docker inspect` in the file, so no container Config or Env is read or printed.
  It publishes no port, starts and stops nothing, and refuses names with
  characters outside `[A-Za-z0-9_.-]`.
- It is idempotent. It resolves the network from the kube context (`k3d-rpg-dev`
  gives network `k3d-rpg-dev` and environment `dev`; `k3d-rpg-stg` gives
  `k3d-rpg-stg` and `staging`) and the container as `<name_prefix>-lgtm` from the
  `environments.tsv` row (`rpg-lgtm`, `rpg-stg-lgtm`). `KUBE_CONTEXT`,
  `DEPLOY_ENVIRONMENT`, `LOKI_CONTAINER` and `LOKI_NETWORK` override that, and
  `--dry-run` prints the plan and changes nothing.
- When `lgtm` is absent or not running, or has no address after the attach, it
  prints a WARN, **deletes** the EndpointSlice so that no stale address is left,
  and exits 0: logs are not in the client path, so this never fails a deploy. It
  exits 1 only when the cluster API refuses the EndpointSlice.

#### What the collector's RBAC allows

There is **no ClusterRole and no ClusterRoleBinding**, and **no hostPath volume**
(also no `hostNetwork`, `hostPID`, `hostPort` or privileged container). The
ServiceAccount `rpg-log-collector` of `rpg-k8s-logging` is bound by a namespaced
**Role** `rpg-log-collector` plus a RoleBinding of the same name in **each
collected namespace**: `rpg-k8s-realtime`, `rpg-k8s-data` and `rpg-k8s-logging`
(`k8s/logging/20-rbac.yaml`), and `rpg-k8s-realtime-<game>` for every non-default
game (rendered by `k8s/games/render.py`, never copied by hand). Every Role has
exactly two rules:

| Rule | Why |
|---|---|
| `pods`: `get`, `list`, `watch` | `discovery.kubernetes` lists and watches the pods of each collected namespace, to learn which containers exist and to build the label set. |
| `pods/log`: `get` | `loki.source.kubernetes` tails each container through `GET .../pods/<pod>/log`, which is the container's stdout and stderr as `kubectl logs` shows it. |

What a pod object reveals, and what it does not. The collector can read the pod
**object** (spec and status). That shows **literal environment values**
(`env: value:`). For a value that comes from a Secret it shows only the
`secretKeyRef` **name and key**, never the **value**: the value is not in the pod
object, and there is no `secrets` rule to read the Secret itself. It cannot read a
ConfigMap either. It cannot create, patch or delete a pod, and it has no
`pods/exec` or `pods/attach`.

Not granted, on purpose: secrets, configmaps, nodes, events, namespaces, any
resource outside the core `pods` group, any verb beyond those above, any
cluster-scoped kind. A namespace without such a Role cannot be collected, so
collecting a new namespace is a reviewed change. The DaemonSet runs as non-root
(uid 473, the `alloy` user of the image) with a read-only root filesystem, all
capabilities dropped, no privilege escalation and the `RuntimeDefault` seccomp
profile. Its only writable path is the `emptyDir` for read positions. The API
token of the ServiceAccount is mounted, and no Secret is referenced anywhere in
`k8s/logging/`.

Alloy's HTTP port `12345` (UI and `/-/ready`) listens on `0.0.0.0` so that the
kubelet can probe it on the pod IP. No Service exposes it, but any pod of the
cluster that knows the pod IP can open the Alloy UI (component graph and config);
it serves nothing that writes.

`k3s/logging_contract.py` enforces these rules offline: no ClusterRole, a Role of
exactly the two rules, a hardened DaemonSet, a selector-less Service `loki`, and
the per-game Role and binding. `k3s/validate-manifests.py` runs it as part of its
no-argument run.

#### Labels on k8s

The stream labels are the contract of "Label contract" above, enforced by the same
final `stage.label_keep`: exactly `env`, `service`, `level`, plus `game` and
`server_id` on game server pods.

| Label | On | Source on k8s |
|---|---|---|
| `env` | every stream | `deploy-environment` in ConfigMap `alloy-env`. `dev-up.sh` sets it to `DEPLOY_ENVIRONMENT`, or `dev` for context `k3d-rpg-dev` and `staging` for `k3d-rpg-stg`; any other context must set `DEPLOY_ENVIRONMENT`, or `dev-up.sh` stops before it applies anything. The bare default in the repository file is `local`. |
| `service` | every stream | the **container name**, with the real values of the manifests: `gateway`, `gameserver` (every fleet, every game), `agones-gameserver-sidecar` (the SDK sidecar that Agones adds to every game server pod), `nakama`, `migrate` (init container of Nakama), `postgres` (both StatefulSets), `redis`, `provision` and `psql` (the one-off Jobs), `alloy`. |
| `level` | every stream | the shared pipeline: read from JSON lines only, see "Label contract" above. |
| `game` | game server pods only (Agones label `agones.dev/role=gameserver`, both containers) | the **namespace**: `rpg-k8s-realtime` gives `cuvara` (the default game), `rpg-k8s-realtime-<game>` gives `<game>`. |
| `server_id` | game server pods only | the **pod name**. The fleets set `POD_NAME` from `metadata.name` and the server resolves its id from `POD_NAME` (a fleet has no `GAMESERVER_ID`), so the pod name is the id the Redis registry uses. |

- **The gateway has no `game`.** It shares the default game's namespace
  `rpg-k8s-realtime`, but only pods with the Agones role label get `game`, and a
  stream without `game` is stack-wide. The **data namespace** (`rpg-k8s-data`:
  Nakama, Postgres, Redis) and the collector's own namespace carry neither `game`
  nor `server_id`.
- **`server_id` values turn over.** Dungeon pods come and go, so a new `server_id`
  value appears for each new game server pod and old ones stop receiving lines.
  The number of values is bounded by the number of game server pods that exist
  over the retention window, and no other label is built from a pod name, a UID or
  an IP.
- Pods outside `rpg-k8s-*` are never shipped, whatever `log-namespaces` lists: a
  `keep` rule on the namespace backs the list.
- `service` is a container name here and a compose service name there
  (`gameserver` against `gameserver-dotnet`, `gameserver-dotnet-map02`), so a
  query that has to cover both writes `service=~"gameserver.*"`.

The collected namespaces are the fixed three plus every game namespace.
`dev-up.sh` builds the list (`log-namespaces` in `alloy-env`) from the games the
gateway serves, with the same mapping that its game TLS repair uses. A game
namespace in the list without the collector's Role is not collected: Alloy's pod
list for it is refused (a `403` in Alloy's own log). `dev-up.sh` warns about that,
carries on, and prints the `render.py` command that applies the game.

#### Log level and format knobs on k8s

| ConfigMap | Key | Env of the container | Default | Values |
|---|---|---|---|---|
| `gateway-config` | `log-level` | `LOG_LEVEL` (gateway) | `info` | `debug`, `info`, `warn`, `error` |
| `gameserver-config` | `log-level` | `GAMESERVER_LOG_LEVEL` | `Information` | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` |
| `gameserver-config` | `log-format` | `GAMESERVER_LOG_FORMAT` | `json` | `json`, `text` |

All three env entries are `optional: true`, so a cluster whose ConfigMap predates a
key keeps starting. A non-default game's `gameserver-config` is rendered from the
same source and carries the same two keys and defaults, and `render.py` refuses a
source that lost them. **The game server reads its two knobs only once the
`gameserver-json-logs` change is in the image**; on a build without it they are
ignored, and the server logs its old plain format, whose lines ship with
`level="unknown"` (see the dependency note under "Label contract"). The gateway
already reads `LOG_LEVEL` and logs JSON. A pod reads its ConfigMap at start, so
changing a value needs a restart of the workload (the runbook has the commands).

#### Querying on k8s

The queries are those of "How to query"; only `env` and `service` differ. `env`
is `dev` or `staging` here, and the Loki port is `LOKI_PORT` of that environment
(dev 3100, staging 3120):

```bash
LOKI=http://127.0.0.1:${LOKI_PORT:-3100}
logql() { curl -s -G "$LOKI/loki/api/v1/query_range" --data-urlencode "query=$1" --data-urlencode limit=20; }

logql '{env="dev", service="gateway"}'                          # the gateway pod
logql '{env="dev", service="nakama", level=~"warn|error"}'      # Nakama warnings and errors
logql '{env="dev", game="cuvara"}'                              # every game server pod of the default game
logql '{env="dev", game="ember"}'                               # every game server pod of the game "ember"
logql '{env="dev", service="gameserver", server_id="<pod name>"}'   # one game server; server_id is the pod name
logql '{env="dev", service="agones-gameserver-sidecar"}'        # the Agones SDK sidecar of the game servers
logql '{env="dev", game=""}'                                    # stack-wide streams: gateway, Nakama, data tier, collector
logql '{env="dev", service="alloy"}'                            # the collector's own log
```

List the real `server_id` values with
`curl -s $LOKI/loki/api/v1/label/server_id/values`, or the pod names with
`kubectl --context k3d-rpg-dev get pods -n rpg-k8s-realtime`.

#### Known gap (not fixed here): CD does not run `lgtm` in `DEPLOY_MODE=k8s`

In `DEPLOY_MODE=k8s`, `cd.yml` neither starts nor updates `lgtm` nor the compose
collector. `dev-up.sh` does not stop `lgtm` (it is not in its list of compose dev
containers), so one that is already running stays up, but nothing guarantees that
one runs. On a k8s environment this decides whether logs arrive at all: with no
`lgtm`, `connect-loki.sh` warns and `cluster.log_collector` WARNs. `cd.yml` and
`docker-compose.yml` were outside the files this change could edit, so this is
recorded for a follow-up, together with the external network and option B above.

#### First live run on k8s: what has to be observed

Nothing below has been observed. The first run on a dev cluster (it needs the
owner's go-ahead) has to show, and record, that:

1. `kubectl apply` accepts the manifests, the `rpg-k8s-logging` namespace raises no
   Pod Security warning, and `daemonset/alloy` becomes Ready on the node.
2. Alloy v1.11.3 loads the generated `config.alloy`: its own log shows no config
   error and no `403` for any collected namespace.
3. **A pod reaches a sibling container's address on the k3d network**, here `lgtm`
   at the address `connect-loki.sh` wrote, on port 3100. This is the main
   unverified assumption of option A.
4. A gateway line reaches Loki with `env`, `service="gateway"` and `level`.
5. A game server line carries `game="cuvara"` and a `server_id` equal to its pod
   name, which is the id in the Redis registry, and a pod of a rendered
   non-default game carries its own `game`.
6. Nothing from `rpg-k8s-data` or the gateway carries `game`, and no label other
   than the five of the contract appears.
7. `docker network connect` and `docker inspect` behave as the script assumes under
   WSL (the script asserts on state, not on exit status, because the Docker shim
   there can exit non-zero after succeeding).
8. After `lgtm` is recreated, `cluster.log_collector` WARNs, and
   `connect-loki.sh` restores the flow.
9. Changing a log level in a ConfigMap and restarting the workload changes what is
   logged (for the game server only with a build that includes the JSON-logs
   change).
10. `cluster.log_collector` PASSes on a healthy cluster and FAILs with the collector
    deleted. Offline it is shown against fixtures only.

## Deploying to a VPS

The monitoring stack is **not** a localhost-only convenience — `cd.yml` deploys
it to every environment, exactly like the meta stack. There is nothing to run by
hand on the box.

### How it ships

1. **bundle** stages `backend/deploy/monitoring/` (and `db/`) into the artifact
   alongside `docker-compose.yml`. Every bind-mount source travels with the
   compose file; a missing one would make Docker create an empty *directory*
   where a config file was expected, and Grafana/Prometheus would start with
   defaults instead of failing loudly.
2. **deploy** installs them into `$RPG_DEPLOY_DIR/deploy/` (replacing the
   previous copies wholesale, so a file deleted in git disappears on the host).
3. The env-file step writes the `GRAFANA_*` / `PROMETHEUS_*` / `OTLP_*` /
   `LOKI_PORT` values (and `ALLOY_VERSION` when set) into
   `$RPG_DEPLOY_DIR/deploy/.env` (mode 0600).
4. The compose step runs `docker compose --profile monitoring up -d
   --remove-orphans`.

Getting monitoring on **staging or production is therefore just setting the
environment secret** — no new workflow, no manual `make monitoring-up`.

### Secrets & variables per environment

| Kind | Name | Default | Notes |
|------|------|---------|-------|
| **secret** | `GRAFANA_ADMIN_PASSWORD` | *(none — required)* | `GF_SECURITY_ADMIN_PASSWORD`. The deploy **fails** with `::error` if unset while monitoring is enabled. |
| var | `MONITORING_ENABLED` | `true` | Set to exactly `false` to deploy the plain meta stack; `--remove-orphans` then tears the running lgtm container down. |
| var | `GRAFANA_USER` | `admin` | |
| var | `GRAFANA_ANONYMOUS` | `false` | See "Anonymous access" below — leave off unless the stack is unreachable from outside the host. |
| var | `GRAFANA_PORT` | `3000` | dev uses `3001` — `3000` is a popular port. |
| var | `GRAFANA_BIND` | `0.0.0.0` | Host interface for the published Grafana port. See firewall guidance below. |
| var | `PROMETHEUS_PORT` / `PROMETHEUS_BIND` | `9090` / `127.0.0.1` | Loopback-only: the bundled Prometheus has no auth at all. |
| var | `OTLP_GRPC_PORT` / `OTLP_HTTP_PORT` / `OTLP_BIND` | `4317` / `4318` / `127.0.0.1` | Loopback-only — nothing off-box pushes OTLP yet. Widen `OTLP_BIND` only when a remote collector needs it, and put TLS + auth in front first. |
| var | `LOKI_PORT` | `3100` | Host port of Loki, always on `127.0.0.1` (no `LOKI_BIND` exists). **Set it per environment before its next deploy**: staging `3120`, production `3110` (the dev value is the default). The isolation preflight compares it with `environments.tsv`. |
| var | `OTEL_LGTM_VERSION` | `0.11.15` | Re-copy the `otlp:`/`storage:` blocks into `prometheus.yaml` and the copied blocks of `loki-config.yaml` when bumping. |
| var | `ALLOY_VERSION` | *(empty: compose's default, `v1.11.3`)* | Image tag of `grafana/alloy`. Empty keeps the variable out of `deploy/.env`, so the default in `docker-compose.yml` is the single source. |

CD also writes `DEPLOY_ENVIRONMENT` and `COMPOSE_PROJECT_NAME` into
`deploy/.env`; Alloy reads them to scope and label what it ships. The socket
proxy's tag (`DOCKER_SOCKET_PROXY_VERSION`, default `0.3.0`) is not passed through
by CD, so a deploy always uses the compose default.

The healthcheck in `scripts/deploy-local.sh` curls Grafana's `/api/health` and
is **warn-only by design**: observability is not on the gameplay critical path,
so a dead Grafana must never fail a deploy that otherwise put a healthy game
stack on the box. Gateway/gameserver stay hard failures.

### Two Grafana gotchas this stack pins down

Both were found by actually deploying the stack and probing it — neither is
obvious from the compose file.

**1. The image ships anonymous *Admin* access.** `otel-lgtm`'s
`run-grafana.sh` does:

```sh
if [ -z "${GF_AUTH_ANONYMOUS_ENABLED:-}" ]; then
    export GF_AUTH_ANONYMOUS_ENABLED=true
    export GF_AUTH_ANONYMOUS_ORG_ROLE=Admin
fi
```

So an unset variable means *anyone who can reach the port is an org admin* —
the login page is decoration. Convenient on a laptop, an open admin console on a
VPS. `docker-compose.yml` therefore always sets
`GF_AUTH_ANONYMOUS_ENABLED: ${GRAFANA_ANONYMOUS:-false}`. Verify after any image
bump:

```bash
curl -s -o /dev/null -w '%{http_code}\n' http://127.0.0.1:${GRAFANA_PORT}/api/org   # want 401
```

**2. `GF_SECURITY_ADMIN_PASSWORD` only applies on first boot.** Grafana reads it
when it *creates* the admin user in an empty `grafana.db`. The `lgtm-data`
volume persists that DB, so **changing the secret and redeploying does not
change the password** — the old one keeps working and the deploy looks fine.

To rotate on an existing environment, change it *in Grafana* (Profile →
Change password) and update the `GRAFANA_ADMIN_PASSWORD` secret to match, or —
if the current password is lost — drop the Grafana DB and let the next deploy
recreate it from the secret:

```bash
cd "$RPG_DEPLOY_DIR/deploy"
docker compose --profile monitoring stop lgtm
docker compose --profile monitoring run --rm --entrypoint sh lgtm -c 'rm -f /data/grafana/data/grafana.db'
docker compose --profile monitoring up -d lgtm
```

Only Grafana's own state (users, ad-hoc dashboards, preferences) is lost — the
provisioned "RPG Gameplay" dashboard and datasources come back from files, and
the Prometheus/Loki/Tempo data under `/data` is untouched. `grafana cli admin
reset-admin-password` is **not** a working alternative here: it reports success
against this image's DB but the resulting hash does not authenticate.

### Firewall / exposure

`GRAFANA_BIND=0.0.0.0` publishes Grafana on every interface, which on a public
VPS means the whole internet sees a login page. Pick one of these before the
first non-dev deploy — in rough order of preference:

1. **SSH tunnel, nothing published.** Set `GRAFANA_BIND=127.0.0.1` and reach it
   with `ssh -L 3000:127.0.0.1:3000 user@vps`. Zero attack surface, no TLS to
   manage. Best default for a single-operator project.
2. **Reverse proxy + TLS.** Keep `GRAFANA_BIND=127.0.0.1` and terminate TLS in
   front (Caddy gives automatic Let's Encrypt with a two-line Caddyfile:
   `grafana.example.com { reverse_proxy 127.0.0.1:3000 }`). Required if
   non-admins need dashboards. Add Grafana OAuth once there is more than one
   viewer. *(Not implemented here — this is guidance, not a shipped component.)*
3. **Firewall allowlist.** If the port must be published directly, restrict it
   to the admin IP:

   ```bash
   sudo ufw default deny incoming
   sudo ufw allow OpenSSH
   sudo ufw allow from <ADMIN_IP> to any port 3000 proto tcp   # Grafana
   sudo ufw enable
   ```

   Note that Docker's `iptables` rules normally **bypass** ufw's `INPUT` chain —
   a published port stays reachable despite a `deny` rule. Either bind to
   loopback (options 1/2, which sidesteps the problem entirely) or add the rule
   in `DOCKER-USER`:

   ```bash
   sudo iptables -I DOCKER-USER -p tcp --dport 3000 ! -s <ADMIN_IP> -j DROP
   ```

Whatever the exposure, `GRAFANA_ADMIN_PASSWORD` must be a real generated secret
per environment — never the `localdev` compose default. Prometheus (`:9090`),
OTLP (`:4317`/`:4318`) and Loki (`:3100`) have **no authentication whatsoever**;
leave them on loopback. Loki cannot be widened by a setting: its bind address is
a literal in the compose file.

## Graduation paths

**Grafana Cloud free tier (VPS / soft-launch).** Keep the exact same exporters;
replace the local stack with **Grafana Alloy** on the host: Alloy scrapes
`:9100/:9101/:9102`, remote-writes to Grafana Cloud Prometheus, ships logs to
Loki. The compose stack already runs an Alloy for logs only ("Logs" above), and
the k8s deployment an Alloy DaemonSet ("Logs on k8s"); the
move would point its `loki.write` at Grafana Cloud's Loki endpoint (with
credentials) and keep the label contract unchanged. The dashboard JSON imports unchanged (datasource is a `prometheus` uid).
Free tier covers ~10k series / 50GB logs — enough through soft launch.

**k3s + Agones (growth).** Install `kube-prometheus-stack` (Prometheus Operator
+ Grafana + Alertmanager) and add a `PodMonitor` per workload:

- gateway pods → port `9102`
- game server pods → port `9101` (Agones fleets, one namespace per non-default
  game: `rpg-k8s-realtime-<game>`; keep `game`/`map_id`/`server_id` as labels)
- Agones ships its own controller metrics + a published Grafana dashboard set

Alerting starts there too: tick p99 over budget, allocation failure rate,
save errors > 0, gateway auth failure spike.
