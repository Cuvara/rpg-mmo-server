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
  image's problem. We own exactly three small files (see below).
- **OTLP is already listening.** The moment the C# game server or the gateway
  emits traces or structured logs, `:4317`/`:4318` accept them — no second
  round of infrastructure work. A hand-rolled Prometheus would have needed
  Loki + Tempo + a collector bolted on later.
- **It is Grafana's own dev-stack image**, so the graduation path to Grafana
  Cloud (below) is the documented one.

Trade-off: it is a *dev* stack. Single binary set, single node, no HA, no
retention policy tuning. Production goes to Grafana Cloud or
kube-prometheus-stack — see the graduation paths at the bottom.

## What we own

Three files under `backend/deploy/monitoring/`, mounted into the image:

| File | Mounted at | Purpose |
|------|-----------|---------|
| `prometheus.yaml` | `/otel-lgtm/prometheus.yaml` | Scrape config (the documented override point of the image) |
| `grafana-dashboards.yaml` | `/otel-lgtm/grafana/conf/provisioning/dashboards/rpg-dashboards.yaml` | Extra dashboard provider, added *alongside* the image's own |
| `dashboards/rpg-gameplay.json` | `/otel-lgtm/dashboards-rpg/` | The "RPG Gameplay" dashboard |

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
| `gateway-container` | `gateway:9102` | only up with `--profile realtime` |
| `gameserver-container` | `gameserver-dotnet:9101` | only up with `--profile realtime-dotnet` |

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
| `gateway_auth_total` | counter | `result=ok\|fail` | MsgAuth outcomes |
| `gateway_enter_world_total` | counter | `result=ok\|fail` | MsgEnterWorld / map assignment outcomes |
| `gateway_allocations_total` | counter | `result=ok\|fail` | allocator (Agones) requests |
| `gateway_relay_events_total` | counter | — | cross-server events delivered by the relay |

Plus the standard `go_*` and `process_*` collectors. Both `result` label values
are pre-created at startup so `rate()` has a zero baseline instead of a series
that pops into existence on the first failure.

### Game server (C#, `:9101`)

Owned by the game server module. The dashboard consumes
`gameserver_tick_duration_seconds` (histogram), `gameserver_players_online`
(gauge) and `gameserver_save_errors_total` (counter). Panels stay empty until
that exporter is live.

### Nakama (`:9100`)

Built-in Prometheus endpoint; useful series are `nakama_api_count`,
`nakama_socket_count`, `nakama_db_*`.

## Dashboard — "RPG Gameplay"

Provisioned (uid `rpg-gameplay`), deliberately minimal — the five numbers that
tell you whether the game is healthy:

| Panel | Query | Read it as |
|-------|-------|-----------|
| Tick duration p99 | `histogram_quantile(0.99, sum by (le, map_id) (rate(gameserver_tick_duration_seconds_bucket[5m])))` | Must stay far below the tick budget (66ms at 15Hz). Crossing it = the sim is late, clients rubber-band. |
| Players online | `sum by (map_id) (gameserver_players_online)` | Load per map + total CCU. |
| Gateway connections active | `sum by (instance) (gateway_connections_active)` | Should track players online; a growing gap means sockets leak or clients stall before EnterWorld. |
| Auth / EnterWorld failure ratio | `rate(gateway_auth_total{result="fail"}[5m]) / rate(gateway_auth_total[5m])` | Sustained >10% = JWT secret mismatch with Nakama, expired tokens, or (EnterWorld) no server has capacity. |
| Save errors + allocation failures | `rate(gameserver_save_errors_total[5m])`, `rate(gateway_allocations_total{result="fail"}[5m])` | Anything non-zero is player progress at risk / Agones unable to give us pods. |
| Scrape targets up | `max by (job) (up)` | Which exporters are alive. |

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
3. The env-file step writes the `GRAFANA_*` / `PROMETHEUS_*` / `OTLP_*` values
   into `$RPG_DEPLOY_DIR/deploy/.env` (mode 0600).
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
| var | `OTEL_LGTM_VERSION` | `0.11.15` | Re-copy the `otlp:`/`storage:` blocks into `prometheus.yaml` when bumping. |

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
per environment — never the `localdev` compose default. Prometheus (`:9090`) and
OTLP (`:4317`/`:4318`) have **no authentication whatsoever**; leave them on
loopback.

## Graduation paths

**Grafana Cloud free tier (VPS / soft-launch).** Keep the exact same exporters;
replace the local stack with **Grafana Alloy** on the host: Alloy scrapes
`:9100/:9101/:9102`, remote-writes to Grafana Cloud Prometheus, ships logs to
Loki. The dashboard JSON imports unchanged (datasource is a `prometheus` uid).
Free tier covers ~10k series / 50GB logs — enough through soft launch.

**k3s + Agones (growth).** Install `kube-prometheus-stack` (Prometheus Operator
+ Grafana + Alertmanager) and add a `PodMonitor` per workload:

- gateway pods → port `9102`
- game server pods → port `9101` (Agones fleets; keep `map_id`/`server_id` as labels)
- Agones ships its own controller metrics + a published Grafana dashboard set

Alerting starts there too: tick p99 over budget, allocation failure rate,
save errors > 0, gateway auth failure spike.
