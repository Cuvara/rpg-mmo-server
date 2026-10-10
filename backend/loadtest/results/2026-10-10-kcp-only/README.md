# 2026-10-10 — KCP/UDP-only gameplay load sweep

First load sweep after realtime gameplay moved to KCP/UDP only (ADR-32).

## Setup

- **Server:** `develop` at 6409d43 (KCP-only) and later.
  - Arm A ran on 6409d43, then on the `perf/realtime/kcp-output-buffer` branch (merged in #448) for `armA-buf`.
  - The `armB2`, `armB3`, `armB50` and `percap` runs ran on 52d8725 (#448 merged), which has the receive-thread metrics.
  - Local compose stack (`deploy/stack.sh`) in WSL2, NAT networking, 20 vCPU.
  - `TRANSPORT_KEY` set (AES datagrams) and `GAMESERVER_SEALED=off`.
  - The game server advertised the WSL VM address (`stack.sh` WSL2-NAT handling).
- **Load generator:** `loadtest.exe` on the Windows host. Every bot shares one source IP, the Windows side of the WSL NAT.
  - `-join direct -movement cluster -ramp 20 -duration 60s`.
  - `run-level.sh` restarts `rpg-gameserver` before every level, so each level starts from a fresh world.
  - It samples `docker stats` for the server and `Get-Process loadtest` for the generator every 5 s (`*.samples`).
- **Host UDP buffers:** `net.core.rmem_max` was 212992 for `armB/kcpB-p95`, and 8 MiB from `armB-rmem` on.
  - The server logs the effective value: `KCP socket buffers: receive=8388608`.
- **Arm A:** `GAMESERVER_ENEMIES=false`. It measures transport, AOI and tick without AI.
- **Arm B:** AI on. Enemy caps scale per player (30 + 45/player), so `-baseline-entities` declares `30+45P+10`.

```bash
export KEY_FILE=... GS_ADDR=<wsl-ip>:9000 LOADTEST_BIN=./loadtest.exe
./run-level.sh 95 kcpB-p95 outdir -baseline-entities 4315
```

## Results

### Arm A: AI off (`armA/`)

| players | joined | tick p99 | over budget | ack p50 / p99 | KCP retx | server CPU | alloc | GC pause | verdict |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 1 | 0.49 ms | 0% | 33.5 / 65.1 ms | 0% | 0.02 cores | 0.08 MB/s | 0.01% | ok |
| 16 | 16 | 0.50 ms | 0% | 33.8 / 65.5 ms | 0% | 0.14 | 1.8 MB/s | 0.07% | ok |
| 17 | 17 | 0.50 ms | 0% | 33.2 / 65.0 ms | 0% | 0.08 | 2.0 MB/s | 0.05% | ok |
| 50 | 50 | 0.50 ms | 0% | 36.6 / 68.4 ms | 0% | 1.18 | 11.4 MB/s | 0.66% | ok |
| 95 | 95 | 0.99 ms | 0% | 43.7 / 78.8 ms | 0.02% | 3.50 | 33.9 MB/s | 1.76% | ok |
| 95 (`armA-buf`, #448 output buffer reuse) | 95 | 0.99 ms | 0% | 42.3 / 76.7 ms | 0% | 3.34 | 29.9 MB/s | 1.50% | ok |

- Snapshot interval p50 is about 64.7 ms against the 66.7 ms period, at every level.
- Zero sessions were rejected and zero datagrams dropped at every level. All clients share one source IP; the local default per-IP cap is 4096.

### Per-IP cap (`percap/`)

- `GAMESERVER_KCP_MAX_SESSIONS_PER_IP=16`, 17 clients from one IP.
- 16 joined. The 17th was refused: `kcp_rejected_per_ip_cap_window=7`, the opener retransmits.
- The refused client failed with a join timeout.

### Arm B: AI on

| players | runs | outcome |
|---|---|---|
| 16 | 1 | ok, tick p99 1.4 ms |
| 50 | 4 (`armB/`, `armB50/`) | **4/4 ok**: 0 `RcvbufErrors` in the 3 runs where it was read, KCP retx ≈ 0, tick p99 5.0–6.6 ms, 0.08–0.22% of ticks over budget, receive thread 11% busy |
| 95 | 10 (`armB`, `armB-rmem`, `diag`, `armB2`, `armB3`, `armB-svrgc`) | **collapse in 7/10**: 135k–276k UDP `RcvbufErrors` in the game-server container, KCP retx 5–40%, 6–39 sessions closed `dead_link`. Tick over budget 3.2–11.4% in every run, including the clean ones |

### Attribution of the "8.27% over budget" seen earlier at 95 players

- **It is the AI, not the transport.** With AI off, 95 players run 0% over budget (tick p99 0.99 ms). With AI on (about 4,300 enemies at 95 players), 3–11% of 16.7 ms ticks run over in every run.
- GC pause share is 0.6–1.2%, so GC does not explain it.
- Separately, the KCP receive path collapses at 95 players with AI on. See Cuvara/rpg-mmo-server#449, which covers the investigation:
  - the host `rmem_max` cap;
  - deferring the full flush;
  - Server GC;
  - none of these removed it.

**Do not quote more than 50 players per game server with AI enemies on until #449 is fixed and re-measured with the load generator on separate hardware (ADR-7).**

## Files

Each `<arm>/` directory holds:

- `<label>.json`: the machine-readable result. It includes `client.kcp` (kcp-go SNMP), `server.cpu_cores`, `gc_*`, `kcp_*` and `kcp_receive_busy_ratio` from 52d8725 on.
- `<label>.txt`: the summary table.
- `<label>.samples`: the 5 s resource samples.
