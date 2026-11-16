# Per-game app tier for a non-default game (ADR-33 leg 5)

The default game, `cuvara`, keeps `deploy/k8s/app/` exactly as it is: namespace
`rpg-k8s-realtime`, unprefixed fleets, no `game-id` key. Every **other** game
gets its own namespace and fleets, rendered from `k8s/app/` by `render.py` in
this directory. **No second game is instantiated in the repo**: this directory
holds the renderer and this README, nothing rendered.

Read first: ADR-33 D2 (option S1, game-qualified server ids), D3, D6 and D7 in
`backend/docs/ARCHITECTURE-DECISIONS.md`.

## What a render gives you

`--nakama-tls` is required when rendering manifests or using `--out` (which writes
all three outputs). Standalone `--print secret-template` and `--print gateway-env`
do not contain Nakama settings, so they can omit the flag.

```bash
cd backend/deploy/k8s/games
python render.py --game ember --nakama-tls off                          # manifests on stdout
python render.py --game ember --nakama-tls off --print secret-template  # the Secret TEMPLATE
python render.py --game ember --nakama-tls off --print gateway-env      # the gateway env snippet
python render.py --game ember --nakama-tls off --out /tmp/ember         # all three as files (keep out of git)
#   [--env k8s] [--stack-namespace rpg-k8s-realtime]
#   [--image rpg-mmo/gameserver-dotnet:ember-develop]
#   [--nakama-url URL] [--nakama-tls-pin PATH]
```

| Object | Name (`--game ember`, defaults) | Derived from |
|---|---|---|
| Namespace | `rpg-k8s-realtime-ember` = `<stack namespace>-<game>` (ADR-33 D7) | — |
| ResourceQuota | `game-quota`: `pods`, `count/gameservers.agones.dev`, `requests.cpu`, `requests.memory` | the fleets' requests (see below) |
| LimitRange | `sidecar-default-requests`: default **requests** only (30m / 32Mi) | Agones 1.59.0 sidecar defaults |
| ServiceAccount + RoleBinding | `agones-sdk`, `agones-sdk-access` | `app/05-agones-sdk-rbac.yaml` |
| ConfigMap | `gameserver-config`, with `game-id: ember` added; carries `log-level: Information` and `log-format: json` like cuvara's | `app/20-configmaps.yaml` |
| Role + RoleBinding | `rpg-gateway-allocator`: `create` on `allocation.agones.dev/gameserverallocations` **only**, bound to ServiceAccount `rpg-gateway` of the **stack** namespace | `app/10-rbac.yaml` (rule copied) |
| Role + RoleBinding | `rpg-log-collector`: `get`/`list`/`watch` on `pods` and `get` on `pods/log` **only** (no ClusterRole, no secrets, no configmaps), bound to ServiceAccount `rpg-log-collector` of namespace `rpg-k8s-logging` | fixed in `render.py` (`LOG_COLLECTOR_*` constants) |
| Fleet | `ember-map-servers-dotnet-k8s` = `<game>-map-servers-dotnet-<env>` | `app/50-fleet-map.yaml` |
| Fleet | `ember-dungeon-servers-dotnet-k8s` = `<game>-dungeon-servers-dotnet-<env>` | `app/60-fleet-dungeon.yaml` |
| FleetAutoscaler | `ember-dungeon-servers-dotnet-k8s-buffer` | `app/70-fleetautoscaler-dungeon.yaml` |
| Secret **template** (separate output) | `rpg-app-secrets` in the game namespace, placeholders only | `app/30-secret-template.yaml` minus `jwt-secret` |
| Gateway env (separate output) | `ALLOCATOR_FLEET_MAP_EMBER`, `ALLOCATOR_FLEET_DUNGEON_EMBER`, `ALLOCATOR_NAMESPACE_EMBER`, `JOIN_TOKEN_SECRET_EMBER`; its header carries the `gateway-config` patch that sets `games` (no `GAMES` env entry, see below) | gateway `cmd/gateway/main.go`, `registry.FleetEnvName` |

**The `k8s/app/` files are the source of truth.** The renderer loads them and
renames, re-namespaces, re-labels (`rpg-mmo/game: <game>`), re-images, and sets Nakama TLS configuration,
so a game cannot drift from cuvara's fleet shape: a change to
`app/50-fleet-map.yaml` reaches every game on the next render. Another semantic
change is `GAMESERVER_GAME_ID`: in `k8s/app` it reads `gameserver-config` key
`game-id` with `optional: true` (absent means cuvara); here the key is set to
the game and the reference is **required**, because a non-default game's pod
running without its id would run as cuvara.

**Logs.** The log collector (Alloy DaemonSet in `rpg-k8s-logging`) can read a
namespace only through the `rpg-log-collector` Role above, so applying a game's
render is what makes its pods collectable; its `game` label is the namespace
suffix (`rpg-k8s-realtime-ember` is `ember`). Both fleets forward
`GAMESERVER_LOG_LEVEL` / `GAMESERVER_LOG_FORMAT` from `gameserver-config`
(`optional: true`, like the neighbouring knobs). `dev-up.sh` adds the namespace of
every game the gateway serves to the collector's `log-namespaces`, and warns (it
does not stop) when that namespace has no `rpg-log-collector` Role yet, because
the render has not been applied. The values and env entries come
from `app/`; the renderer refuses a source that lost either. The server logs JSON
by default; `log-format: text` gives plain lines, which ship with `level="unknown"`.

No game-server pod gets `JWT_SECRET` (ADR-8, ADR-33 D7): the source fleets do
not mount it, the renderer refuses to copy one that does, and the Secret
template has no `jwt-secret` key.

## Refused inputs (exit 2, with the reason)

- a game id not matching `^[a-z][a-z0-9]{1,23}$` (same rule as
  `shared/gameid.ValidGameID` and `db/provision-gamestate.sh`);
- the reserved ids `gs`, `map`, `dungeon`, `other`;
- the default game `cuvara` (it keeps `k8s/app/`);
- an `--env` or `--stack-namespace` that is not an RFC 1123 label, or a stack
  namespace that already ends in `-<game>`;
- any fleet name over **51** characters, and any namespace or other name over
  **63**;
- a Nakama URL with whitespace, no host, or a scheme inconsistent with the TLS
  decision; a pin with whitespace; TLS on with an empty or relative pin; or
  TLS off with any pin.

`validate-manifests.py` (no file arguments) renders `ember` and the longest
legal id (24 characters) in memory, validates every rendered document and
asserts each of the refusals above.

## Name budget: 12 characters of Agones suffix, confirmed for 1.59.0

A non-default game's server id is its pod name (`POD_NAME` via fieldRef), and
the pod is named by Agones after the fleet. ADR-33 D2 budgeted 12 characters
for that and left the confirmation to leg 5. Confirmed against Agones
**release-1.59.0** (the version pinned in `k3s/setup-dev.sh` and
`k3s/validate-manifests.py` `DEFAULT_AGONES_VERSION`):

| Step | Source | Name |
|---|---|---|
| Fleet → GameServerSet | `pkg/apis/agones/v1/fleet.go:128` | `GenerateName = fleet + "-"` |
| GameServerSet → GameServer | `pkg/apis/agones/v1/gameserverset.go:156` | `GenerateName = gss + "-"` |
| GameServer → Pod | `pkg/apis/agones/v1/gameserver.go:871` | pod name = GameServer name |
| `generateName` expansion | kube-apiserver `names.SimpleNameGenerator` | appends 5 random characters, truncating the base to 58 |

So GameServer = pod = `fleet + "-" + 5 + "-" + 5` = **fleet + 12**. A fleet of
at most 51 characters keeps the pod within the 63-character RFC 1123 label
limit with no truncation. `<game>-dungeon-servers-dotnet-k8s` is
`len(game) + 27`, so a 24-character id with `--env k8s` lands on exactly 51;
a longer `--env` needs a shorter id, and the renderer says so. Re-check these
lines when the Agones pin moves.

## The quota is arithmetic, not a capacity figure

`pods` = 2 × (map `replicas` + dungeon autoscaler `maxReplicas`) = 2 × (1 + 6)
= 14, and the cpu/memory requests are the same pods' requests (container plus
the sidecar's 30m/32Mi) times the same factor. The factor 2 is headroom for a
fleet rolling update, where old Allocated GameServers and new Ready ones
coexist. It bounds one game's reservation on a shared node (ADR-33 D7); it
says nothing about players per server, which stays unknown (ADR-7).

The quota covers **requests only**. Agones 1.59.0 injects the SDK sidecar with a
30m CPU request and no memory request and no limits (`SIDECAR_*` in its
`install.yaml`). A quota on `requests.memory` refuses a pod with any container
that declares none, so the LimitRange fills in the sidecar's missing requests;
it sets no default limit, because a limit on a sidecar nobody has measured
would be an OOM kill waiting to happen.

## Gateway side (another file, same names)

The gateway allocates in the game's namespace with the env the render prints
(merge it into `k8s/app/40-gateway.yaml`).

`GAMES` is **not** in that snippet. `40-gateway.yaml` already declares `GAMES`
once, read from ConfigMap `gateway-config` key `games` (`optional: true`, so
absent means cuvara only), and a second `GAMES` variable in the same container
would duplicate it. The snippet's header prints the patch that sets the key
instead, every served game comma-separated:

```bash
kubectl -n rpg-k8s-realtime patch configmap gateway-config   --type=merge -p '{"data":{"games":"cuvara,ember"}}'
```

Once set, cuvara is served only if listed, so the value is `cuvara,<game>`
(plus any game already served), never `<game>` alone. Nakama's own list
(`k8s/data/nakama.yaml`, ConfigMap `nakama-config` key `games`) must agree.

| Env | Value |
|---|---|
| `ALLOCATOR_FLEET_MAP_<GAME>` | `<game>-map-servers-dotnet-<env>` |
| `ALLOCATOR_FLEET_DUNGEON_<GAME>` | `<game>-dungeon-servers-dotnet-<env>` |
| `ALLOCATOR_NAMESPACE_<GAME>` | `<stack namespace>-<game>` (also the gateway's default when unset) |
| `JOIN_TOKEN_SECRET_<GAME>` | Secret `rpg-app-secrets` key `join-token-secret-<game>` in the **stack** namespace |

`join-token-secret-<game>` in the stack namespace and `join-token-secret` in
the game namespace hold the **same value**, which must differ from every other
game's (ADR-33 D7: a game server can forge join tokens for any server sharing
its join secret; the gateway refuses to start if keys are shared). The gateway's
own ServiceAccount, Role and RoleBinding in `k8s/app/10-rbac.yaml` are
unchanged; the game namespace's Role/RoleBinding is the only new grant.

## Bringing a game up (human-run; nothing here is applied by a script)

1. Provision its database: `db/provision-gamestate.sh <game>` creates database
   and role `gamestate_<game>` (human-run, db-schema-write gate).
2. Build its image `rpg-mmo/gameserver-dotnet:<game>-<version>` from its content
   directory `backend/content/<game>` (`--build-arg CONTENT_DIR=content/<game>`
   on `docker/Dockerfile.gameserver-dotnet`). CI builds one image per game in
   its games list and tags it `<game>-<short sha>` and `<game>-<extra tag>`
   (`<game>-develop` from develop, which is render.py's default `--image`);
   add the game to that list in both `publish-images.yml` and `cd.yml`
   (`docs/CICD.md`, "Images per game").
3. Read `tls-cert-path` in `rpg-k8s-data/nakama-config`: a set value means TLS
   is on; otherwise it is off. Render with the required `--nakama-tls on` or
   `--nakama-tls off` and `--out` to a directory **outside** the repo. The
   default URL uses the source ConfigMap host with `https` when on and `http`
   when off. The default pin is `/etc/nakama-tls/tls.crt` when on and empty
   when off. `--nakama-url` and `--nakama-tls-pin` override these defaults;
   TLS on requires an `https://` URL and non-empty absolute pin path, while
   off requires an `http://` URL and empty pin. URLs require a host, and neither
   input may contain whitespace.
   With TLS on, the game namespace needs Secret `nakama-tls-pin` containing
   only the public certificate under `tls.crt`, copied from
   `rpg-k8s-realtime/nakama-tls-pin`. The manifests header gives a pipe command
   that copies it without printing its value. `dev-up.sh` provisions this Secret
   and repairs `gameserver-config` in every game namespace
   `rpg-k8s-realtime-<game>` listed in `gateway-config`'s `games`.
4. Fill the Secret template there (or `kubectl create secret generic`), with
   `game-db-url` for `gamestate_<game>`, `join-token-secret`, `nakama-http-key`
   (same value as cuvara's) and the optional keys. Create `registry-creds` in
   the game namespace if the image is pulled from the registry
   (`k8s/registry/README.md`).
5. Before the gateway can serve the game, add `join-token-secret-<game>` to
   `rpg-k8s-realtime/rpg-app-secrets` with its own keyring, sharing no keys
   with `jwt-secret` or another game's join keyring. The game namespace's
   `rpg-app-secrets` must exist with `join-token-secret` equal to it.
   `dev-up.sh` refuses deployment before applying workloads otherwise. The
   gateway refuses to start if any key is shared; a missing per-game key only
   warns and refuses EnterWorld for that game. Merge the gateway env snippet,
   set `games` in `gateway-config` (the patch
   in the snippet header) and in `rpg-k8s-data`'s `nakama-config` (the recipe
   in `k8s/data/nakama.yaml`) to the same list, then roll the gateway and
   Nakama.
6. Apply the manifests; apply the FleetAutoscaler only after the dungeon
   fleet's image is pinned, for the reason in `app/70-fleetautoscaler-dungeon.yaml`.

`GAMESERVER_MAP_ID` stays the source fleet's `map_01`; a game whose content has
no `map_01` must change the source contract first, not hand-edit a render.
