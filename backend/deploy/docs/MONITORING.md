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
| `127.0.0.1:3100` | Bundled Loki query API (`LOKI_PORT`), loopback only, no authentication. Container logs are shipped here by Alloy — see "Logs" below |

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
offline tests for all of the above.

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
why, and how to query it.

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
  (`archive`, `export`). That is a property of the image that was not tested
  here; treat the collector as able to read everything about the stack's
  containers.
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
  information, inf), `warn` (warn, warning, wrn), `error` (error, err), `fatal`
  (fatal, critical, crit, panic, dpanic). That covers the gateway's slog (`INFO`,
  `WARN`, …), Nakama's zap (`info`, `dpanic`, …) and the .NET names
  (`Information`, `Warning`, `Critical`). **Anything else is `unknown`**: a plain
  line, a line that does not parse, a missing field, a number, a custom word. The
  closed set is deliberate: an open one would mint a stream per distinct value.
  This change does not alter what the services print; a service that prints plain
  text is `level="unknown"`.
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
  `pattern_ingester`, `ingester`, `frontend`, `query_scheduler`.
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
logql '{env="local", service="gameserver-dotnet"} | json | level="Warning"'   # a field inside a JSON body
```

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
  JSON line (`level="warn"`, body unchanged) and once as a plain line
  (`level="unknown"`); and, when the `realtime` profile is up, that a gateway
  stream and a game server stream exist with `env`, `service`, `level` and, for
  the game server, `game` and `server_id` matching its compose labels (SKIP when
  that profile is down). Output is `PASS:`/`FAIL:`/`SKIP:` lines and the exit
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
Loki. The compose stack already runs an Alloy for logs only ("Logs" above); the
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
