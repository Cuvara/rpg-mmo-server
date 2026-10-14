# 2026-10-14 — KCP receive collapse at 95 players (#449): bulk CFB fix

Re-measurement of the collapse recorded in [`../2026-10-10-kcp-only/`](../2026-10-10-kcp-only/README.md) (Arm B, 95 players, AI on). Design write-up: `gameserver-dotnet/docs/DESIGN.md`, section "KCP datagram encryption cost and the 95-player receive collapse".

## Builds

Every image was built from `backend/` with `docker build -f deploy/docker/Dockerfile.gameserver-dotnet`.

| Image (`rpg-mmo/gameserver-dotnet:`) | Image ID | Source | What it is |
|---|---|---|---|
| `dev-pre449` | `145ac94be4d5` | `develop` 4de2292 | Baseline: per-block AES, synchronous send under the session lock |
| `cfb449` | `7f2228c3e56b` | 6d44653 = 4de2292 + the `KcpCrypto` / `KcpCryptoCfbTests` change in this PR | **The fix**: bulk CFB, synchronous send unchanged |
| `fix449f` | `9770394ba767` | fbe8dd1 (local branch `fix/realtime/kcp-send-off-lock`, not merged) | Rejected alternative: bulk CFB plus a bounded send queue drained by 4 `kcp-send` threads, plus send-cost counters |
| `fix449d` | `1c71c7210ce6` | same code as fbe8dd1, built before it was committed | As `fix449f` |
| `fix449e` | `c98f909ebd8c` | fbe8dd1 with the `SendThreads` default edited to 1 (not committed) | Send queue with 1 send thread |

The code in this PR is the code of 6d44653. The only change since is the cost figure in one `KcpCrypto.cs` doc comment.

## Setup

Same harness as 2026-10-10:
- Local compose stack (`deploy/stack.sh up --no-build`) in WSL2 with NAT networking, 20 vCPU, `net.core.rmem_max` 8 MiB.
- `TRANSPORT_KEY` set (AES datagrams), `GAMESERVER_SEALED=off`.
- `loadtest.exe` on the Windows host, with every bot on one source IP.
- `-join direct -movement cluster -ramp 20 -duration 60s`.
- `run-level.sh` restarts the game server before every level.

The only variables are the image, the player count and `GAMESERVER_ENEMIES`. The runs were interleaved: build A, build B, build A, and so on, never all of one build first.

```bash
export KEY_FILE=... KEY_FILE_WSL=... GS_ADDR=<wsl-ip>:9000 LOADTEST_BIN=./loadtest.exe \
       DEPLOY_ENV=../../deploy/.env DEPLOY_DIR=/mnt/c/.../backend/deploy OUT=out
./ab.sh cfb449     x-1-cfb449-p95-aitrue     95 true  >> summary.txt
./ab.sh dev-pre449 x-3-dev-pre449-p95-aitrue 95 true  >> summary.txt
./ab.sh cfb449     x-12-cfb449-p95-aifalse   95 false >> summary.txt
./ab.sh cfb449     x-16-cfb449-p50-aitrue    50 true  >> summary.txt
```

- `RcvbufErrors` is read from `/proc/net/snmp` inside the container after the run.
- ACK latency is client-side: input sent to the snapshot that acknowledges it. Failed clients contribute no samples, so a collapsed run can still show a low ACK p99. Read it together with `failed`.

## Results

`summary.txt` holds one line per run. `<label>.json`, `.txt` and `.samples` are the raw outputs.

### 95 players, AI on (4,315 baseline entities)

| Build | Run | Failed | `RcvbufErrors` | Sessions closed | KCP retx | ACK p50 / p95 / p99 (ms) | Tick p99 | Ticks over budget | Recv thread busy | CPU (cores) |
|---|---|---|---|---|---|---|---|---|---|---|
| develop | x-3 | **37** | 168,119 | 35 | 27.5% | 43.8 / 75.9 / 90.0 | 18.0 ms | 2.1% | 21% | 2.62 |
| develop | x-6 | **41** | 239,406 | 37 | 34.1% | 47.1 / 8,843 / 13,817 | 23.3 ms | 5.8% | 23% | 2.59 |
| develop | x-8 | **29** | 241,559 | 29 | 41.3% | 46.9 / 5,513 / 16,064 | 24.7 ms | 8.6% | 23% | 2.60 |
| **fix** | x-1 | 0 | 0 | 0 | 0.005% | 43.4 / 76.2 / 91.5 | 45.0 ms | 8.7% | 11% | 1.75 |
| **fix** | x-4 | 0 | 0 | 0 | 0 | 42.8 / 73.4 / 84.4 | 33.6 ms | 8.5% | 11% | 1.56 |
| **fix** | x-7 | 0 | 0 | 0 | 0 | 42.6 / 72.8 / 83.6 | 31.4 ms | 7.4% | 10% | 1.44 |
| **fix** | x-9 | 0 | 0 | 0 | 0 | 42.5 / 72.8 / 83.5 | 34.7 ms | 8.2% | 10% | 1.51 |
| **fix** | x-11 | 0 | 0 | 0 | 0.013% | 42.7 / 73.4 / 84.2 | 24.7 ms | 7.9% | 10% | 1.40 |
| queue, 4 threads | x-2 | 0 | 0 | 0 | 0.93% | 49.9 / 156.4 / 775.4 | 47.0 ms | 11.8% | 16% | 2.33 |
| queue, 4 threads | x-5 | 0 | 0 | 0 | 0.004% | 44.0 / 75.7 / 88.3 | 42.3 ms | 8.8% | 15% | 1.91 |
| queue, 4 threads | x-10 | 0 | 0 | 0 | 0 | 43.7 / 74.8 / 85.7 | 39.9 ms | 8.5% | 15% | 1.81 |

The same-day runs before this batch, recorded in `summary-earlier.txt` (ACK p99 only), were consistent with it:
- develop collapsed in 2 of 3 (20 and 55 failed clients).
- `fix449d` (queue, 4 threads): ACK p99 89 and 109 ms.
- `fix449e` (queue, 1 thread): ACK p99 125 and 96 ms.
- The send queue with the old per-block cipher (`fix449`, `fix449b`, `fix449c`): ACK p99 3.6-14 s, because the queue sat full.

Send-cost counters from the queue builds give the cost of one seal (`gameserver_kcp_send_busy_seconds_total{phase="seal"}` / `gameserver_kcp_datagrams_sent_total`, over the sustained window):

| Build | Seal time | Datagrams | Per datagram |
|---|---|---|---|
| per-block AES (`fix449c`, `scrape-12.txt`) | 116.1 s | 341,944 | **~340 us** |
| bulk CFB (`fix449f`, `scrape-x-10-…`) | 12.9 s | 379,468 | **~34 us** |

### 95 players, AI off

| Build | Run | Failed | `RcvbufErrors` | ACK p50 / p95 / p99 (ms) | Tick p99 | CPU (cores) |
|---|---|---|---|---|---|---|
| develop | x-13 | 0 | 0 | 40.7 / 69.5 / 74.2 | 0.92 ms | 2.10 |
| develop | x-15 | 0 | 0 | 41.5 / 70.9 / 75.8 | 0.85 ms | 2.18 |
| **fix** | x-12 | 0 | 0 | 35.2 / 65.0 / 68.4 | 0.95 ms | 0.36 |
| **fix** | x-14 | 0 | 0 | 35.1 / 64.9 / 68.1 | 0.95 ms | 0.35 |

### 50 players, AI on (2,290 baseline entities)

| Build | Run | Failed | `RcvbufErrors` | ACK p50 / p95 / p99 (ms) | Tick p99 | Ticks over budget | CPU (cores) |
|---|---|---|---|---|---|---|---|
| develop | x-17 | 0 | 0 | 41.5 / 71.5 / 79.4 | 5.0 ms | 0.14% | 1.75 |
| develop | x-19 | 0 | 0 | 42.4 / 73.8 / 83.0 | 9.3 ms | 0.34% | 2.18 |
| **fix** | x-16 | 0 | 0 | 37.8 / 67.6 / 71.9 | 4.9 ms | 0.06% | 0.48 |
| **fix** | x-18 | 0 | 0 | 37.6 / 67.6 / 71.4 | 7.7 ms | 0.11% | 0.54 |

## Conclusions

- **The collapse is fixed in this harness.** At 95 players with AI on:
  - develop: 3 of 3 interleaved runs collapsed.
  - fix: 5 of 5 ran clean, with 0 failed clients, 0 `RcvbufErrors`, 0 sessions closed and ACK p99 83-92 ms.
- **The fix lowers CPU at every load.**
  - 95 players, AI off: 2.1-2.2 cores down to 0.35.
  - 50 players, AI on: 1.75-2.18 down to 0.48-0.54.
  - Most of the old server CPU was the cipher.
- **The send queue is not needed.**
  - With the bulk cipher, the queue (1 or 4 threads) matched the fix at best, cost more CPU, and had one 775 ms ACK p99 outlier.
  - With the old cipher, the queue made ACK latency worse.
  - It is not shipped. See the DESIGN.md section for the reasoning.
- **What this does not fix:**
  - The tick budget at 95 players with AI on: tick p99 25-45 ms, 7-9% of ticks over 16.7 ms, in every run of every build. This is enemy AI cost; the loadtest verdict for these runs stays `degraded`.
  - There is still no capacity rating. The generator shares the host, every bot uses one source IP, the runs last 60 s, and there was no soak run. The ADR-7 separate-hardware measurement is still outstanding.
