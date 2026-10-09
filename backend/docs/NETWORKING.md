# Networking — transports, ports and how a client reaches a game server

Normative decision: [ADR-32 — Realtime gameplay transport is KCP/UDP only](ARCHITECTURE-DECISIONS.md#adr-32--realtime-gameplay-transport-is-kcpudp-only).
Wire format (frames, messages): `backend/gameserver-dotnet/docs/API.md`. This page is the
operator's view: which hop uses what, which ports must be open with which protocol, and what
a failure looks like.

## Transports at a glance

| Hop | Transport | Purpose |
|---|---|---|
| Client -> Nakama | **HTTP/RPC** (HTTPS when the meta-hop TLS flag is on, ADR-24) | auth, `gateway_token`, economy, social, leaderboard. Nakama's WebSocket is **not** used for gameplay |
| Client -> Gateway | **TCP**, optionally **TLS** (ADR-23) | auth + redirect only (ADR-3). Never carries gameplay |
| Client -> Game server | **KCP over UDP — the only realtime gameplay transport** | movement, combat, snapshots. No TCP fallback |
| Client -> content | **HTTP** (`/content` on the game server's metrics port, ADR-19) | game data download |
| Game server -> Nakama | HTTP (`NAKAMA_URL`, server-to-server key) | reward RPCs |

There is no transport switch any more. The gateway always listens TCP; the game server always
listens KCP/UDP; the only value of `transport` anywhere (Redis registry, `EnterWorldResponse`)
is `"kcp"`.

## Gateway flow (TCP)

1. The client authenticates with Nakama over HTTP and calls the `gateway_token` RPC.
2. It opens a TCP connection (TLS if the gateway terminates it) to the gateway and sends
   `MsgAuth{JWT}`; the gateway verifies the JWT locally.
3. `MsgEnterWorld{MapID}` -> the gateway finds (or allocates, under Agones) the server for the
   map, checks that its registry `transport` is `kcp` (anything else, empty included, is an
   assignment error: no join token is minted), mints a 30 s join token and answers
   `MsgEnterWorldResp{ServerAddr, JoinToken, Transport="kcp"}`.
4. The client leaves the gateway connection for auth/session duties and dials `ServerAddr`
   over KCP/UDP. The gateway is not in the data path.

## KCP connection flow (UDP)

1. The client creates a KCP session (stream mode; profile nodelay 1/10/2/1, window 128/128,
   MTU 1350, no FEC) to `ServerAddr`. If `TRANSPORT_KEY` is configured, every datagram is
   AES-256 encrypted kcp-go-style with that pre-shared key; client and server must hold the
   same key.
2. Framing inside the KCP stream is unchanged: 4-byte big-endian length + Protobuf (legacy
   JSON told apart by the first byte).
3. **First frame: `MsgJoinToken{token}`.** Anything else is refused. The server verifies the
   token with `JOIN_TOKEN_SECRET` and answers `MsgJoinTokenResp`.
4. **Sealed handshake** when `GAMESERVER_SEALED=require` (ADR-22/25): the session is then
   carried in sealed frames (ChaCha20-Poly1305, per-session key, strict replay counter). This
   is the authenticated confidentiality layer; `TRANSPORT_KEY` is not.
5. Gameplay: `MsgInput` up, `MsgSnapshot` down, per tick.
6. **Idle / dead link.** KCP has no FIN. A session with no inbound datagram for **60 s** is
   closed by the server's idle sweep and its entity enters the normal reconnect hold. A peer
   that restarts with a new conversation id from the same endpoint replaces the old session.
7. **Flood limits** (all counted on `/metrics`): live sessions (4096), sessions per source IP
   (16, loopback exempt), new sessions per second (200, burst 2x), datagrams per second per
   session (500, burst 2x). Knobs: `GAMESERVER_KCP_MAX_SESSIONS`,
   `GAMESERVER_KCP_MAX_SESSIONS_PER_IP` (raise behind carrier NAT),
   `GAMESERVER_KCP_NEW_SESSIONS_PER_SEC`, `GAMESERVER_KCP_DATAGRAMS_PER_SEC`.

## Ports

| Port | Protocol | Component | Notes |
|---|---|---|---|
| 7350 | TCP | Nakama HTTP/RPC | 7349 gRPC, 7351 console, 9100 metrics also TCP |
| 8000 | **TCP** | Gateway client port | auth + redirect, optional TLS. Compose publishes `GATEWAY_CONTAINER_PORT` (8100 offset default) |
| 9102 | TCP | Gateway metrics, `/healthz`, `/readyz` | |
| 9000 | **UDP** | Game server game port (in-container / host-mode listen) | KCP only. Compose publishes `GAMESERVER_CONTAINER_PORT` (9200 default) as `/udp` |
| 9101 | TCP | Game server metrics, `/healthz`, `/status`, `/content` | liveness for every shell check |
| 7000 / 7001 | TCP | k3d: gateway / Nakama hostPorts | dev cluster |
| 7010-7100 | **UDP** | k3d: Agones dynamic game ports (staging 7210-7300) | must be published `/udp` |
| 7000-8000 | **UDP** | real k3s node: Agones default `minPort`-`maxPort` | open in the node / cloud firewall |

## Deployment requirements

- **Docker / compose.** Publish the game port as `host:9000/udp`
  (`docker-compose.yml`, `docker-compose.override.yml`). The Dockerfile declares
  `EXPOSE 9000/udp` and `EXPOSE 9101/tcp`; the gateway image `EXPOSE 8000/tcp`.
- **Agones / k8s.** Fleet ports named `game` declare `protocol: UDP`, `portPolicy: Dynamic`
  (`deploy/agones/fleet-map-dotnet-dev.yaml`, `deploy/k8s/app/50-fleet-map.yaml`,
  `60-fleet-dungeon.yaml`). The name `game` is a contract with the gateway's allocator.
  `TRANSPORT_KEY` comes from the Secret key `transport-key` (`optional: true`).
- **k3d.** The serverlb must publish the Agones range as UDP and the infrastructure ports as
  TCP:

  ```
  k3d cluster create rpg-dev --api-port 127.0.0.1:6550 \
    --port "7000-7009:7000-7009@loadbalancer" \
    --port "7010-7100:7010-7100/udp@loadbalancer"
  ```

  Staging: `--port "7200-7209:7000-7009@loadbalancer" --port "7210-7300:7210-7300/udp@loadbalancer"`.
  A cluster created with the old TCP-only `7000-7100` mapping needs
  `k3d cluster edit <name> --port-add "7010-7100:7010-7100/udp@loadbalancer"` (recreates the
  serverlb, interrupting clients once) or a recreate. `deploy/k8s/dev-up.sh` checks the
  serverlb's publish table and stops if the UDP range is missing.
- **Advertised address.** The address the gateway hands out is used verbatim by the client,
  so it must be the **client-reachable host** and a **UDP** port:
  - `GAMESERVER_PUBLIC_ADDR` (compose / host mode): `<public-host-or-ip>:<published port>`.
    `127.0.0.1:<port>` only when the client runs on the same machine; never a bare `:port` on
    a VPS.
  - `GAMESERVER_ADVERTISE_HOST` / ConfigMap `advertise-host` (Agones): host only, composed
    with the Agones-assigned port. Never the pod IP or the node's private address; `127.0.0.1`
    only on a single-host k3d with the client on that host. Multi-node clusters have no single
    correct value (ADR-16).
- **VPS firewall.** `scripts/bootstrap-vps.sh` opens the gateway port as TCP and the game port
  as **UDP only**. Mirror both in the provider firewall (security group), which sits in front
  of ufw. Under Agones on a real node, open the Agones range as UDP.

## Local development

- `backend/deploy/stack.sh up` starts the compose stack; its summary prints
  `game server udp localhost:<port>` and whether a KCP key is in force.
  `docker-compose.override.yml` maps the canonical `8000/tcp` and `9000/udp`.
- Unity client: point it at the gateway (`127.0.0.1:8000`). If the stack sets
  `TRANSPORT_KEY`, launch the client with the same value: `-cuvara-transport-key <hex>` or
  env `CUVARA_TRANSPORT_KEY`. Empty on both sides = plaintext datagrams (dev).
- Go tools read env `TRANSPORT_KEY` (smoketest, loadtest, `deploy/k8s/verify/probe`,
  integration tests). `verify.sh` passes `VERIFY_TRANSPORT_KEY` (k8s targets read it from the
  cluster Secret).
- **WSL2.** WSL2's default NAT networking forwards `localhost` from Windows into the VM as a
  TCP mechanism; do not assume UDP reaches a stack inside WSL2 via Windows' `127.0.0.1`.
  If a Windows client's join times out while a client inside WSL2 (the smoketest) passes,
  either set `networkingMode=mirrored` in `%UserProfile%\.wslconfig` (then `wsl --shutdown`),
  or advertise the WSL VM's address (`hostname -I` inside WSL) via `GAMESERVER_PUBLIC_ADDR`
  / `advertise-host` and point the client's gateway host at it too. This was not measured on
  this box at the time of writing; confirm with a real client before relying on either.

## Configuration variables

| Variable | Where | Meaning |
|---|---|---|
| `TRANSPORT_KEY` | game server; Go tools | Optional KCP datagram PSK, 32-byte hex (`openssl rand -hex 32`). Empty = plaintext + startup WARN. Not read by the gateway |
| `CUVARA_TRANSPORT_KEY` / `-cuvara-transport-key` | Unity client | Same value as the server's `TRANSPORT_KEY` |
| `GAMESERVER_PUBLIC_ADDR` | game server (no Agones) | Advertised `host:port`; UDP port |
| `GAMESERVER_ADVERTISE_HOST` | game server (Agones) | Advertised host; port from Agones |
| `GAMESERVER_SEALED` | game server | `require` / `off` — sealed session on the gameplay hop |
| `GAMESERVER_KCP_*` | game server | KCP flood limits (above) |
| `GAMESERVER_TRANSPORT` | game server | Unset or `kcp` only. **Any other value (including `tcp`) is fatal at startup** |
| `GATEWAY_TRANSPORT` | gateway | **Removed.** Unset or `tcp` is tolerated; anything else stops the gateway |
| `ALLOCATOR_TRANSPORT` | gateway | **Removed.** Ignored with a WARN; delete it from `.env` files and CI variables |

## Troubleshooting

`EnterWorld` succeeding and the join then timing out is the signature of every UDP problem:
there is no handshake to refuse, so nothing names the cause.

| Symptom | Likely cause | Check |
|---|---|---|
| Join timeout, every client | UDP blocked: host/cloud firewall allows only TCP for the game port | ufw / security group rule is `udp`; `ss -lun` on the host shows the port |
| Join timeout, compose | Port published without `/udp` (TCP mapping) | `docker ps` shows `...->9000/udp` |
| Join timeout, k3d | serverlb publishes the Agones range as TCP | `docker port k3d-<cluster>-serverlb 7010/udp`; `dev-up.sh` reports it |
| Join timeout, Agones | Fleet port `protocol: TCP` | `kubectl get gs -o yaml` -> `ports[].protocol: UDP` |
| Join timeout, one network only | Advertised host not reachable from the client's network (loopback, pod IP, private IP) | registry `HGET servers:id:<id> addr`; `GAMESERVER_PUBLIC_ADDR` / `advertise-host` |
| Join timeout, works without a key | `TRANSPORT_KEY` mismatch between client and server | compare the server env / Secret with the client flag; datagrams with the wrong key decrypt to noise and are dropped silently |
| Join timeout, Windows client vs WSL2 stack | WSL2 NAT localhost forwarding not carrying UDP | see Local development |
| Gateway answers an assignment error | The server registered a transport other than `kcp` (an old image) | registry `HGET servers:id:<id> transport`; redeploy the KCP-only image |
| Game server exits at startup naming `GAMESERVER_TRANSPORT` | `GAMESERVER_TRANSPORT=tcp` (or other) left in an env file | remove it |
| Gateway exits at startup naming `GATEWAY_TRANSPORT` | `GATEWAY_TRANSPORT=kcp` (or other) left in an env file | remove it |
| WebGL build cannot join | Browsers cannot open UDP sockets; WebGL has no KCP path | not supported (ADR-32 follow-up) |

How reachability is proven (shell checks cannot): the smoketest (`stack.sh check`, CD's
post-deploy smoke, `verify.sh` layer 4 `flow.smoke`) and the verify probe
(`registry.addr_kcp_join`, `probe enterworld` printing `kcp_join=ok`) perform a real KCP join.
Liveness checks use the game server's HTTP `/healthz`; a TCP connect to the game port is always
refused, and `nc -u` "succeeds" against anything.
