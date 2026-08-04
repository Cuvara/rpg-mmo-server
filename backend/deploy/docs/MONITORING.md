# Monitoring — Prometheus + Grafana

Local observability stack for the backend. Ships as a **compose profile**, so the
everyday dev loop (`make up`) is unchanged until you opt in.

```bash
cd backend/deploy
make monitoring-up      # start prometheus + grafana
make monitoring-targets # one-line-per-target scrape health
make monitoring-logs    # tail both
make monitoring-down    # stop just these two (volumes kept)
```

| Service | URL | Auth |
|---|---|---|
| Grafana | http://localhost:3000 | anonymous **Viewer**; `admin` / `localdev` to edit |
| Prometheus | http://localhost:9090 | none |
| Prometheus targets | http://localhost:9090/targets | none |
| Prometheus alerts | http://localhost:9090/alerts | none |

Ports come from `.env` (`GRAFANA_PORT`, `PROMETHEUS_PORT`, `GRAFANA_ADMIN_PASSWORD`) —
see `.env.example`.

> **Port 3000 is a common collision** (Node dev servers, other Grafanas). Docker
> Desktop can fail the bind *silently*: the container comes up healthy but
> `docker ps` shows `3000/tcp` with no `0.0.0.0:3000->` mapping. If
> `curl localhost:3000/api/health` 404s, that is what happened — set
> `GRAFANA_PORT=3001` in `.env` and `make monitoring-up` again.

---

## Architecture

```
                       ┌─────────────────────────── compose network ──┐
                       │                                              │
  host :9101 ◄─────────┼── host.docker.internal ──┐                   │
  gameserver /metrics  │                          │                   │
                       │                     ┌────▼─────┐    ┌────────▼────┐
  host :9102 ◄─────────┼── host.docker.internal──► Prometheus ──► Grafana  │
  gateway /metrics     │                     │  :9090   │    │   :3000     │
                       │                     └────▲─────┘    └─────────────┘
                       │   rpg-nakama:9100 ───────┘                        │
                       └───────────────────────────────────────────────────┘
```

Two scrape paths, and the split matters:

- **Nakama** runs *inside* the compose network → scraped by container name
  (`rpg-nakama:9100`). Nakama exports Prometheus natively via
  `--metrics.prometheus_port 9100`; nothing extra to install.
- **Gateway and gameserver** normally run on the **host** (`go run`, for instant
  rebuilds — see `RUNBOOK-local-dev.md`) → scraped through
  `host.docker.internal`, which `docker-compose.yml` maps to `host-gateway` so
  plain Linux engines resolve it too.

If you instead run them containerized (`--profile realtime`), point those two
scrape targets at `rpg-gateway:9102` / `rpg-gameserver:9101` in
`monitoring/prometheus/prometheus.yml`.

### Files

```
monitoring/
  prometheus/
    prometheus.yml        # scrape config, 15s interval, env=dev labels
    alert-rules.yml       # 4 starter alerts
  grafana/
    provisioning/
      datasources/prometheus.yml   # uid rpg-prometheus, default, read-only
      dashboards/dashboards.yml    # loads ./dashboards into folder "RPG MMO"
    dashboards/
      rpg-overview.json            # the dashboard (14 panels)
```

Dashboards are **code**. `allowUiUpdates: false` — edit the JSON in git and
Grafana reloads within 30s. Editing in the browser and hitting Save will fail;
that is deliberate, so the repo never drifts from what is on screen.

---

## Metric contract

The gateway and gameserver expose `/metrics` **and** `/healthz` on dedicated
listeners. This stack scrapes these names:

| Metric | Type | Owner | Meaning |
|---|---|---|---|
| `gameserver_tick_duration_seconds` | histogram | gameserver | Wall time of one simulation tick. Labels: `map_id` |
| `gameserver_players_online` | gauge | gameserver | Players currently simulated. Labels: `map_id` |
| `gameserver_snapshots_sent_total` | counter | gameserver | AOI snapshots broadcast |
| `gameserver_saves_total` | counter | gameserver | Async batch persistence flushes |
| `gameserver_save_errors_total` | counter | gameserver | Failed flushes |
| `gateway_connections_active` | gauge | gateway | Live client sessions |
| `gateway_auth_total` | counter | gateway | `MsgAuth` attempts. Labels: `result="ok"\|"fail"` |
| `gateway_enter_world_total` | counter | gateway | `MsgEnterWorld` attempts. Labels: `result` |

Nakama's names are its own (`nakama_presences`, `nakama_overall_latency_ms_*`,
`nakama_db_*`, `nakama_Rpc_count`, …) and are not ours to choose.

**A red target on `/targets` is information, not a bug.** `gateway` and
`gameserver` read DOWN whenever those processes are not running — which is the
normal state of a fresh checkout, and exactly the signal you want.

---

## Dashboard guide — "RPG MMO — Overview"

**Top row (at-a-glance):**

| Panel | Read it for |
|---|---|
| Scrape targets (`up`) | Who is alive. Colour-mapped UP/DOWN. Check this first, always. |
| Players online | Realtime CCU (`sum(gameserver_players_online)`) |
| Gateway connections | Live sessions. Much higher than players online ⇒ sessions stuck before EnterWorld |
| Snapshots / sec | Outbound realtime throughput. Should track `players × tick rate` |
| Nakama presences | Meta-channel CCU — independent of the realtime path |

**Realtime:**

- **Tick duration p50/p95/p99** — `histogram_quantile` over the tick histogram.
  The single most important panel: 15Hz means a **66ms** budget per tick, drawn
  as a red threshold. p50 should sit far below it; p99 touching the line means
  the sim is about to slip.
- **Players online per map** — stacked by `map_id`; shows which map a spike came from.
- **Gateway: auth & enter_world rate** — split by `result`, failures forced red.
- **Gateway: active connections** — a sawtooth that never drains ⇒ sessions not
  reaped on disconnect.
- **Snapshot broadcast rate** — per instance.
- **Persistence: saves vs save errors** — errors are red; anything sustained is data loss.

**Nakama:** request rate by API, mean API latency, PostgreSQL connection pool.

> Nakama exports latency as a **summary in milliseconds**, not a histogram — so
> the latency panel is a ratio-of-rates (`_sum / _count` = mean), not a
> percentile. True p99 for meta RPCs is not obtainable from this exporter.

---

## Alerting

Rules live in `monitoring/prometheus/alert-rules.yml` and evaluate every 15s.
**Alertmanager is intentionally not included** — routing without a destination
is dead config. Alerts are visible at http://localhost:9090/alerts. Add
Alertmanager (or Grafana unified alerting) once a notification channel is
chosen — Slack or Discord webhook is the likely first stop for an indie team;
that is a one-service compose addition plus an `alertmanagers:` block in
`prometheus.yml`.

| Alert | Fires when | Severity |
|---|---|---|
| `TickBudgetExceeded` | p99 tick > 66ms for 5m | critical |
| `SaveErrors` | any save-error rate for 5m | critical |
| `HighAuthFailureRate` | >25% of auth attempts fail for 10m | warning |
| `ServiceDown` | `up == 0` for 2m | critical |

### TickBudgetExceeded

The simulation loop is not finishing inside its 66ms slot. Downstream: snapshots
arrive late and jittery, client interpolation buffers starve, players rubber-band.

1. Which map? The alert carries `map_id`.
2. Check **Players online per map** — is it simply population, or did p99 rise
   with flat population (⇒ an algorithmic regression, likely AOI)?
3. AOI is brute-force O(n²) in the MVP. A crowded map is the expected first
   offender; the fix is the spatial grid, not more CPU.
4. Short term: reduce fleet map capacity or shard the map.

### SaveErrors

Async batch persistence is failing — **player state is being lost**, silently,
because persistence deliberately never blocks the tick loop.

1. `docker compose logs postgres-game`; is it healthy?
2. `make psql-game` — connectivity and disk.
3. Check the gameserver's `GAME_DB_URL`.
4. Do not restart the gameserver until the DB is reachable: unflushed state dies with the process.

### HighAuthFailureRate

Most often **JWT secret drift** — `JWT_SECRET` must be identical across Nakama
(`--session.encryption_key`), gateway, and gameserver. Otherwise: a mass token
expiry, or credential stuffing. Confirm by comparing the `ok` and `fail` series
on the gateway panel — a fail line rising *with* ok flat is drift; both rising
is traffic.

### ServiceDown

Fires for any job including Prometheus itself. Expected for `gateway`/`gameserver`
when they aren't running locally — if that noise gets old, comment the target out
rather than weakening the rule.

---

## Why Prometheus + Grafana, and not Zabbix or Nagios

Zabbix and Nagios are host-and-service monitors, built for a world of long-lived
machines you check on: is the box up, is the disk full, does the port answer.
They are agent/check-push shaped, their data model is host-centric, and
dimensional queries over labels — "p99 tick duration by `map_id`" — are not
something they express naturally. Our questions are almost entirely the second
kind. Prometheus is pull-based over a plain HTTP `/metrics` endpoint, which means
an ephemeral Agones game server pod needs no agent, no registration, and no
tear-down step; it is discovered, scraped while it lives, and forgotten. The
label-based data model plus PromQL is what makes `histogram_quantile(...)` by map
a one-liner instead of a custom script. Decisively, the stack we already run
speaks it natively: **Nakama** exports Prometheus on `:9100` today, **Agones**
exports fleet and allocation metrics, and **k3s** components do too — so
Prometheus is the format our dependencies emit whether we choose it or not, and
choosing anything else would mean writing exporters to translate *into* the other
tool. It is also the k8s-native default, so the graduation path (below) is
adopting a chart rather than migrating a monitoring philosophy. All of it is
open-source and $0, matching the tier budgets.

---

## Graduating to k3s

This compose stack is **local dev only**. Do not port `prometheus.yml` to the
cluster — static scrape targets are the wrong model there.

On k3s, install [`kube-prometheus-stack`](https://github.com/prometheus-community/helm-charts)
(Prometheus Operator + Grafana + node-exporter + kube-state-metrics, one chart).
Then:

- Replace static targets with **`ServiceMonitor` / `PodMonitor`** CRDs so Agones
  fleet pods are discovered as they scale, with no config edits.
- Scrape **Agones** itself (`agones-controller` exposes fleet counts, allocation
  latency, and player capacity) — the numbers that tell you whether the
  autoscaler buffer is sized right.
- Add **`postgres_exporter`** and **`redis_exporter`** sidecars; the local stack
  skips them because compose healthchecks already cover dev needs.
- Move these alert rules into a **`PrometheusRule`** CRD, unchanged — the PromQL
  ports over as-is, which is the point of not inventing a local dialect.
- Keep `rpg-overview.json` and provision it the same way via the chart's
  `dashboardProviders`. Same dashboard, same panel queries; only discovery changes.

Retention here is 7 days on a local volume. For beta+, either give Prometheus a
PVC with real retention or ship to Grafana Cloud's free tier via `remote_write`.
