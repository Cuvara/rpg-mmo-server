#!/usr/bin/env python3
"""Render the encoding sweep results as the comparison tables used in BENCHMARK.md.

Usage: scripts/encoding-report.py [results/encoding]
"""
import json
import pathlib
import sys

ARMS = ["baseline-json", "new-json", "new-proto"]
KB = 1024.0


def load(d):
    out = {}
    for f in sorted(pathlib.Path(d).glob("*.json")):
        arm, _, players = f.stem.rpartition("-")
        if arm not in ARMS:
            continue
        out.setdefault(arm, {})[int(players)] = json.load(f.open())
    return out


def row(r):
    c, s = r["client"], r["server"]
    return {
        "kbps": c["rx_bytes_per_sec_per_player"] / KB,
        "tx_kbps": c["tx_bytes_per_sec_per_player"] / KB,
        "total_mbps": c["rx_bytes_per_sec_total"] / KB / KB,
        "p50": s["tick_p50_sec"] * 1000,
        "p99": s["tick_p99_sec"] * 1000,
        "mean": s["tick_mean_sec"] * 1000,
        "over": s["tick_over_budget_ratio"] * 100,
        "tps": s["ticks_per_sec"],
        "snap_p99": c["snapshot_interval"]["p99_ms"],
        "ack_p99": c["ack_latency"]["p99_ms"],
        "joined": c["players_joined"],
        "failed": c["players_failed"],
        "recv": c["snapshots_received_ratio"],
    }


def main():
    d = sys.argv[1] if len(sys.argv) > 1 else "results/encoding"
    data = load(d)
    levels = sorted({p for arm in data.values() for p in arm})

    print("### Per-arm results (cluster, worst-case density)\n")
    for arm in ARMS:
        if arm not in data:
            continue
        print(f"**{arm}**\n")
        print("| Players | tick p50 | tick p99 | tick mean | over budget | ticks/s "
              "| snap p99 | ack p99 | KB/s/client | MB/s total | joined | fail |")
        print("|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|")
        for p in levels:
            if p not in data[arm]:
                continue
            r = row(data[arm][p])
            print(f"| {p} | {r['p50']:.2f}ms | {r['p99']:.2f}ms | {r['mean']:.2f}ms "
                  f"| {r['over']:.2f}% | {r['tps']:.2f} | {r['snap_p99']:.1f}ms "
                  f"| {r['ack_p99']:.1f}ms | {r['kbps']:.1f} | {r['total_mbps']:.1f} "
                  f"| {r['joined']} | {r['failed']} |")
        print()

    print("### Head to head\n")
    print("| Players | KB/s/client json | KB/s/client proto | bandwidth saved "
          "| tick p99 json | tick p99 proto | tick mean json | tick mean proto |")
    print("|--:|--:|--:|--:|--:|--:|--:|--:|")
    for p in levels:
        if p not in data.get("new-json", {}) or p not in data.get("new-proto", {}):
            continue
        j, q = row(data["new-json"][p]), row(data["new-proto"][p])
        saved = 100 * (j["kbps"] - q["kbps"]) / j["kbps"]
        print(f"| {p} | {j['kbps']:.1f} | {q['kbps']:.1f} | **{saved:.1f}%** "
              f"| {j['p99']:.2f}ms | {q['p99']:.2f}ms | {j['mean']:.2f}ms | {q['mean']:.2f}ms |")

    print("\n### Baseline vs new JSON (isolates the JSON-path cleanup, not the encoding)\n")
    print("| Players | KB/s/client baseline | KB/s/client new-json | tick mean baseline | tick mean new-json |")
    print("|--:|--:|--:|--:|--:|")
    for p in levels:
        if p not in data.get("baseline-json", {}) or p not in data.get("new-json", {}):
            continue
        b, n = row(data["baseline-json"][p]), row(data["new-json"][p])
        print(f"| {p} | {b['kbps']:.1f} | {n['kbps']:.1f} | {b['mean']:.2f}ms | {n['mean']:.2f}ms |")

    # 50 KB/s per client is ADR-7's acceptance threshold.
    print("\n### ADR-7 bandwidth threshold (< 50 KB/s per client)\n")
    for arm in ARMS:
        if arm not in data:
            continue
        pts = sorted((p, row(r)["kbps"]) for p, r in data[arm].items())
        ceiling = None
        for (p0, k0), (p1, k1) in zip(pts, pts[1:]):
            if k0 <= 50 <= k1:
                ceiling = p0 + (p1 - p0) * (50 - k0) / (k1 - k0)
                break
        if ceiling is None:
            ceiling = f"> {pts[-1][0]}" if pts[-1][1] < 50 else f"< {pts[0][0]}"
        else:
            ceiling = f"~{ceiling:.0f}"
        print(f"- **{arm}**: {ceiling} players")


if __name__ == "__main__":
    main()
