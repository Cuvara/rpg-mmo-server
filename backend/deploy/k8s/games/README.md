# Per-game app tier for a non-default game (ADR-33 leg 5)

The default game, `cuvara`, keeps `deploy/k8s/app/` exactly as it is: namespace
`rpg-k8s-realtime`, unprefixed fleets, no `game-id` key. Every **other** game
gets its own namespace and fleets, rendered from `k8s/app/` by `render.py` in
this directory. **No second game is instantiated in the repo**: this directory
holds the renderer and this README, nothing rendered.

Read first: ADR-33 D2 (option S1, game-qualified server ids), D3, D6 and D7 in
`backend/docs/ARCHITECTURE-DECISIONS.md`.

## What a render gives you

```bash
cd backend/deploy/k8s/games
python render.py --game ember                          # manifests on stdout
python render.py --game ember --print secret-template  # the Secret TEMPLATE
python render.py --game ember --print gateway-env      # the gateway env snippet
python render.py --game ember --out /tmp/ember         # all three as files (keep out of git)
#   [--env k8s] [--stack-namespace rpg-k8s-realtime]
#   [--image rpg-mmo/gameserver-dotnet:ember-develop]
```

| Object | Name (`--game ember`, defaults) | Derived from |
|---|---|---|
| Namespace | `rpg-k8s-realtime-ember` = `<stack namespace>-<game>` (ADR-33 D7) | — |
| ResourceQuota | `game-quota`: `pods`, `count/gameservers.agones.dev`, `requests.cpu`, `requests.memory` | the fleets' requests (see below) |
| LimitRange | `sidecar-default-requests`: default **requests** only (30m / 32Mi) | Agones 1.59.0 sidecar defaults |
| ServiceAccount + RoleBinding | `agones-sdk`, `agones-sdk-access` | `app/05-agones-sdk-rbac.yaml` |
| ConfigMap | `gameserver-config`, with `game-id: ember` added | `app/20-configmaps.yaml` |
| Role + RoleBinding | `rpg-gateway-allocator`: `create` on `allocation.agones.dev/gameserverallocations` **only**, bound to ServiceAccount `rpg-gateway` of the **stack** namespace | `app/10-rbac.yaml` (rule copied) |
| Fleet | `ember-map-servers-dotnet-k8s` = `<game>-map-servers-dotnet-<env>` | `app/50-fleet-map.yaml` |
| Fleet | `ember-dungeon-servers-dotnet-k8s` = `<game>-dungeon-servers-dotnet-<env>` | `app/60-fleet-dungeon.yaml` |
| FleetAutoscaler | `ember-dungeon-servers-dotnet-k8s-buffer` | `app/70-fleetautoscaler-dungeon.yaml` |
| Secret **template** (separate output) | `rpg-app-secrets` in the game namespace, placeholders only | `app/30-secret-template.yaml` minus `jwt-secret` |
| Gateway env (separate output) | `GAMES`, `ALLOCATOR_FLEET_MAP_EMBER`, `ALLOCATOR_FLEET_DUNGEON_EMBER`, `ALLOCATOR_NAMESPACE_EMBER`, `JOIN_TOKEN_SECRET_EMBER` | gateway `cmd/gateway/main.go`, `registry.FleetEnvName` |

**The `k8s/app/` files are the source of truth.** The renderer loads them and
only renames, re-namespaces, re-labels (`rpg-mmo/game: <game>`) and re-images,
so a game cannot drift from cuvara's fleet shape: a change to
`app/50-fleet-map.yaml` reaches every game on the next render. The one semantic
change is `GAMESERVER_GAME_ID`: in `k8s/app` it reads `gameserver-config` key
`game-id` with `optional: true` (absent means cuvara); here the key is set to
the game and the reference is **required**, because a non-default game's pod
running without its id would run as cuvara.

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
  **63**.

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
(merge it into `k8s/app/40-gateway.yaml`):

| Env | Value |
|---|---|
| `GAMES` | every served game, comma-separated, e.g. `cuvara,ember` |
| `ALLOCATOR_FLEET_MAP_<GAME>` | `<game>-map-servers-dotnet-<env>` |
| `ALLOCATOR_FLEET_DUNGEON_<GAME>` | `<game>-dungeon-servers-dotnet-<env>` |
| `ALLOCATOR_NAMESPACE_<GAME>` | `<stack namespace>-<game>` (also the gateway's default when unset) |
| `JOIN_TOKEN_SECRET_<GAME>` | Secret `rpg-app-secrets` key `join-token-secret-<game>` in the **stack** namespace |

`join-token-secret-<game>` in the stack namespace and `join-token-secret` in
the game namespace hold the **same value**, which must differ from every other
game's (ADR-33 D7: a game server can forge join tokens for any server sharing
its join secret; the gateway warns when two games share a key). The gateway's
own ServiceAccount, Role and RoleBinding in `k8s/app/10-rbac.yaml` are
unchanged; the game namespace's Role/RoleBinding is the only new grant.

## Bringing a game up (human-run; nothing here is applied by a script)

1. Provision its database: `db/provision-gamestate.sh <game>` creates database
   and role `gamestate_<game>` (human-run, db-schema-write gate).
2. Build its image `rpg-mmo/gameserver-dotnet:<game>-<version>` from its content
   directory (ADR-33 leg 5 image work; CI builds one image per listed game).
3. Render with `--out` to a directory **outside** the repo.
4. Fill the Secret template there (or `kubectl create secret generic`), with
   `game-db-url` for `gamestate_<game>`, `join-token-secret`, `nakama-http-key`
   (same value as cuvara's) and the optional keys. Create `registry-creds` in
   the game namespace if the image is pulled from the registry
   (`k8s/registry/README.md`).
5. Add `join-token-secret-<game>` to the stack namespace's `rpg-app-secrets`
   and merge the gateway env snippet, then roll the gateway.
6. Apply the manifests; apply the FleetAutoscaler only after the dungeon
   fleet's image is pinned, for the reason in `app/70-fleetautoscaler-dungeon.yaml`.

`GAMESERVER_MAP_ID` stays the source fleet's `map_01`; a game whose content has
no `map_01` must change the source contract first, not hand-edit a render.
