# Deployment verification suite

One command that answers, for any deployment of this system, **"is it actually
working?"** — with a pass/fail and, on failure, the observed value, the expected
value and where to look.

It exists because the move to k3s/Agones must not be judged by eyeball. Every
claim it makes is a check that can fail; a check that cannot fail is noise, and
a check that did not run says so.

```
./verify.sh --target dev-agones
```

## Design rules

- **Every check states what it proves and what it cannot.** `./verify.sh --list`
  prints both for all of them; a failure prints the "proves" line next to the
  diagnosis.
- **Skips are loud.** A check that could not run is `SKIP`, listed again in the
  summary under *"NOT verified — absence of coverage, not evidence of health"*,
  and turns the whole run red under `--strict`. A check function that returns
  without stating a verdict is recorded as **FAIL**, not PASS — this project has
  a documented history of a soft `return` reading as a green line.
- **The target is configuration.** Namespaces, addresses, map id and the exec
  prefixes used to reach Postgres/Redis all live in `targets/*.env`, so the same
  suite runs against dev-on-Agones today and the in-cluster stack tomorrow.
- **Read-only.** It starts nothing, restarts nothing, reconfigures nothing and
  deletes no volume or PVC. The single exception is opt-in and named:
  `refusal.unknown_map` (`--allow-allocation`).

## Layout

```
verify.sh            entry point: target loading, layer selection, summary
lib/common.sh        check framework (register / pass / fail / skip / warn)
lib/checks_*.sh      the checks, one file per layer
targets/*.env        per-deployment configuration
probe/               small Go module: the two protocol-level observations
                     curl and redis-cli cannot make
```

`probe/` is its own Go module (`github.com/duycuong/rpg-mmo/deployverify`) with a
`replace` onto `../../../../shared`. It does not touch the existing modules and
is built on demand into `$TMPDIR`.

## The checks

### Layer 1 — cluster invariants (`kubectl`, always `--context $KUBE_CONTEXT`)

| id | proves | cannot prove |
|----|--------|--------------|
| `cluster.reachable` | kubectl reaches the API server on the expected context | nothing about workload health |
| `cluster.namespaces` | the declared namespaces exist | nothing about their contents |
| `cluster.workloads` | every Deployment/StatefulSet/DaemonSet in those namespaces has all declared replicas Ready | readiness is the probe's opinion; a workload with no readiness probe passes while broken |
| `cluster.pvcs` | each declared PVC is `Bound` | nothing about the data on it, nor about PVCs nobody declared |
| `cluster.fleet` | the Agones fleet exists and carries ≥ `VERIFY_FLEET_MIN_REPLICAS` GameServers | nothing about which map those pods serve — that is layer 3 |
| `cluster.autoscaler` | no `FleetAutoscaler` targets a fleet whose pod template pins one `GAMESERVER_MAP_ID` for every replica | nothing about fleets it does not name, and nothing about a map id supplied per pod through `valueFrom` — that case is reported as unpinned and the rule stands down |
| `cluster.restarts` | no container in the namespaces has `restartCount > 0` | the window is pod lifetime, not a fixed period: a pod recreated a minute ago hides yesterday's crash loop |
| `cluster.secrets` | each declared Secret exists and every key decodes to a non-empty value | nothing about the value being *correct*. Values are never printed — only key names and byte lengths |

`cluster.fleet` reports **WARN** when the fleet is at size but has zero Ready
replicas (everything Allocated) **and** the fleet does not pin a fleet-wide
`GAMESERVER_MAP_ID`. On a fleet that does pin one — the map fleet — `ready=0` is
the designed steady state rather than a shortfall, and the check passes saying
so. Warning about the correct state trains the reader to reach for the fix that
breaks it. Either way `refusal.unknown_map` cannot reach the branch it tests in
that state, and its own SKIP says so.

`cluster.autoscaler` is a **FAIL**, not a warning, and it is the one check here
that guards a change nothing else can see. A buffer `FleetAutoscaler` on the map
fleet applies cleanly, brings a pod to `Ready`, and makes `cluster.fleet` look
*better* — while the spare pod self-registers as a second live server for
`map_01`, because the C# server registers at startup rather than on allocation.
Measured on k3d 2026-08-18: `1 -> 2` replicas put a second member into
`servers:map:map_01` 5.4 s later with no allocation involved. See **ADR-18** and
`backend/deploy/docs/K3S.md`. A fleet with a per-pod map id does not trip this
check, which is the point — it must stop being an error the moment the real fix
lands.

Keys that are legitimately empty are named one at a time in
`VERIFY_SECRETS_ALLOW_EMPTY="ns/name:key,key"` and print as `empty-by-config`.
The exemption is per key on purpose: an unset `REDIS_PASSWORD` looks exactly
like a forgotten one.

### Layer 2 — data tier reachability

| id | proves | cannot prove |
|----|--------|--------------|
| `data.pg_meta` | the meta instance answers, on the expected database, with a populated public schema (Nakama migrated it) | nothing about the content, nor about Nakama's own connection to it |
| `data.pg_game` | the game-state instance answers on the expected database, `schema_migrations` is at the expected version, `player_states` exists | nothing about rows being written — `flow.smoke` proves that |
| `data.redis_ping` | the shared Redis is reachable and accepting commands | nothing about contents or durability |
| `data.redis_policy` | `maxmemory-policy = noeviction` (**ADR-4**) | nothing about `maxmemory` itself or RDB/AOF |
| `data.nakama_health` | the Nakama process serves HTTP | **nothing about the Go plugin** — Nakama returns 200 with no plugin loaded |
| `data.nakama_plugin` | the `gateway_token` RPC exists, returns a signed token, and that token verifies locally under the shared `JWT_SECRET` | nothing about the gateway accepting it |

`data.redis_policy` is load-bearing rather than hygienic: this Redis is the
server registry and the event stream, not a cache. Any other policy silently
drops live registrations under memory pressure, and the symptom ("the map has no
server") appears far from the cause.

`data.nakama_plugin` exists because process liveness is not plugin liveness. It
is the difference between "Nakama is up" and "clients can obtain a token signed
with the secret the gateway verifies with".

### Layer 3 — the registry contract

| id | proves | cannot prove |
|----|--------|--------------|
| `registry.one_server` | exactly one non-expired `servers:id:*` hash carries `map_id = $VERIFY_MAP_ID` (**ADR-2**) | nothing about an in-memory registry (`--backend=memory` is invisible from outside), nothing about other maps |
| `registry.addr_qualified` | the advertised address carries a real host — a hostless `:9000`, `0.0.0.0:…` or `[::]:…` fails | it does not prove the host is reachable from the *client's* network |
| `registry.addr_dialable` | something accepts TCP on that address from where the suite runs | not that it speaks the game protocol |

Liveness is read from the `servers:id:*` hashes, never from the
`servers:map:{map_id}` index — the index has no TTL and outlives dead servers,
so it is pruned lazily and would otherwise over-count.

`registry.addr_qualified` is the check that must never regress: a listen-style
address is what made the whole Agones path unusable, and it *works by accident*
on a single host because the client rewrites it to loopback. A **loopback**
address is accepted only where `VERIFY_ADDR_ALLOW_LOOPBACK=1` (k3d node ports
mapped onto the host), and even then only as a **WARN** that names the
limitation. On the k8s target the flag is `0`, so loopback fails.

### Layer 4 — the flow

| id | proves | cannot prove |
|----|--------|--------------|
| `flow.smoke` | Nakama auth → `gateway_token` → `MsgAuth` → `MsgEnterWorld` → a **direct dial of the advertised address** → input/snapshot → the `player_states` write → the reload after the reconnect hold | one client on one map: nothing about concurrency, capacity, or any other map |

This runs the existing `backend/smoketest` — it does not reimplement it — with
the two flags that make it strict:

- `--strict-addr`: a listen-style `ServerAddr` is a hard failure instead of
  being silently rewritten to loopback. That rewrite once hid the exact defect
  this suite is here to catch.
- `--require-db`: a persistence check that cannot run **fails** instead of
  skipping, so "no DSN configured" cannot read as green. With
  `VERIFY_GAME_DB_URL` unset the check still runs the realtime flow but reports
  **WARN**, naming what was not proven.

The binary is taken from `VERIFY_SMOKETEST_BIN`, or built from
`backend/smoketest` on demand. A missing binary is a property of the runner, not
of the deployment, so it is never allowed to become a coverage gap.

### Layer 5 — the client

| id | proves | cannot prove |
|----|--------|--------------|
| `client.playmode` | the real Unity client, with its real netcode package, completed its live-backend PlayMode tests against **this** gateway and Nakama — asserted from the NUnit XML | nothing if the XML is stale or was produced against a different backend; the suite prints the file mtime so that is visible |

**This suite never launches Unity.** The Editor for `IndieRPGMMOAdventure` has
exactly one driver; a second Unity process on the same project fights over the
`Library` lock and produces fake package-resolution errors. So the contract is:
the suite prints the exact invocation, the operator runs it, and the suite
asserts on the resulting XML (`total` / `passed` / `failed` on the root element,
`test-case` elements for the failures). No XML → visible SKIP, never a pass.

The invocation, with the deployment's own addresses substituted:

```
CUVARA_GATEWAY_HOST=127.0.0.1  CUVARA_GATEWAY_PORT=8000
CUVARA_NAKAMA_HOST=127.0.0.1   CUVARA_NAKAMA_PORT=7350
CUVARA_NAKAMA_SERVER_KEY=defaultkey  CUVARA_MAP_ID=map_01
Unity.exe -batchmode -projectPath E:\SecretProject\IndieRPGMMOAdventure \
  -runTests -testPlatform PlayMode \
  -testResults <abs>\playmode.xml -logFile <abs>\playmode.log
```

Then:

```
./verify.sh --target dev-agones --layer 5 --unity-results /abs/playmode.xml
```

`VERIFY_UNITY_MIN_TESTS` guards the other direction: a run where the filter
matched almost nothing still reports `failed=0`, so set it once the expected
PlayMode count is stable and a shrunken run fails instead of passing.

### Layer 6 — the refusals

A deployment that only serves the happy path is not verified.

| id | proves | cannot prove |
|----|--------|--------------|
| `refusal.unknown_map` | a map no fleet hosts gets the **terminal** refusal `"map is not available"`, not the retryable `"server is starting, retry shortly"` | it costs one unreclaimable GameServer to run, and is **INCONCLUSIVE** when the fleet has no Ready replica |
| `refusal.alloc_wait` | the gateway binary exits rather than starting with an allocation wait that outlives the client heartbeat | it tests the **image**, not the running gateway's configured value |
| `refusal.split_world` | at most one live server per `map_id`, and that the gateway's own duplicate-detection warning agrees with the registry | it cannot manufacture a split, so a clean run proves the detector was correctly silent, not that it fires when it should |

**`refusal.unknown_map` is opt-in** (`--allow-allocation` /
`VERIFY_ALLOW_ALLOCATION=1`) and skips loudly otherwise. Probing an unserved map
makes the gateway attempt one Agones allocation; Agones has no un-allocate and
the gateway has no `Deallocate`, so that GameServer is consumed permanently. On
a `replicas: 1` fleet that is the whole fleet. Run it against a scratch
deployment, or one with spare Ready replicas. It distinguishes four outcomes and
only one of them is a pass:

| gateway answer | verdict |
|---|---|
| `RESULT=ok` (admitted) | FAIL — the gateway served a map no fleet hosts |
| `"map is not available"` | PASS |
| `"server is starting, retry shortly"` | FAIL — retryable refusal; every retry leaks a GameServer |
| `"no server available for map"` | SKIP (inconclusive) — the fleet had nothing to allocate, so the branch under test was never reached |

`refusal.alloc_wait` runs a throwaway container: `--network none`, no published
ports, `--backend=memory`, expected to die on startup. It cannot touch the
running deployment.

`refusal.split_world` separates two different failures: a split world, and a
split world nobody noticed. With `VERIFY_GATEWAY_LOG_CMD` unset it reports
**WARN** — the absence of a split is proven, the detector is not.

## Running it

```bash
# whole suite
JWT_SECRET=<the deployment's secret> ./verify.sh --target dev-agones

# one layer at a time (1..6)
./verify.sh --target dev-agones --layer 3 --layer 4

# with the Unity results the operator produced
./verify.sh --target dev-agones --unity-results /abs/playmode.xml

# skips count as failures — use this as the release gate
./verify.sh --target dev-agones --strict

# what every check proves and cannot prove
./verify.sh --list
```

Exit code `0` = `VERIFY=PASS`, `1` = `VERIFY=FAIL`, `2` = the suite could not
run (bad target, missing `JWT_SECRET`).

`JWT_SECRET` is required and never stored in a target file. The value the
running gateway uses:

```bash
docker inspect rpg-gateway --format '{{range .Config.Env}}{{println .}}{{end}}' | grep '^JWT_SECRET='
```

`docker exec <c> printenv` does **not** work on the distroless gateway image: it
returns the exec error text, which reads exactly like a value.

Requirements on the runner: `kubectl`, `curl`, `python3`, `bash` ≥ 4, plus `go`
(to build `probe/` and, if needed, `smoketest`) and whatever the target's exec
prefixes need (`docker` for the compose data tier).

## Adding a target

Copy `targets/k8s-dev.env` and fill it in. Every value is deliberately explicit
rather than defaulted, so an unfinished migration surfaces as a failing check
instead of a silent default. The data-tier checks take **exec prefixes** rather
than DSNs:

```bash
VERIFY_REDIS_EXEC="docker exec rpg-redis"                                  # compose
VERIFY_REDIS_EXEC="kubectl --context k3d-rpg-dev exec -n rpg-data sts/redis --"  # in-cluster
```

Only the prefix changes between deployments; the assertions do not.
