# Tick-statistic variance at a fixed level

Six runs of the same configuration — 200 players, JSON encoding, 35s window —
taken back to back on an otherwise-quiet host, to answer "how reproducible is a
tick ceiling?" rather than "what is the ceiling?".

These are the measurements behind the criterion change in
[`backend/docs/BENCHMARK.md`](../../../docs/BENCHMARK.md) and the amendment to
[ADR-7](../../../docs/ARCHITECTURE-DECISIONS.md).

| statistic | range | spread |
|---|---|--:|
| tick p99 | 67.41 – 70.84ms (median 69.48) | 5.1% |
| tick mean | 36.51 – 38.68ms (median 37.81) | 5.9% |
| KB/s per client | 243.7 – 244.3 | **0.3%** |

Two conclusions:

1. **Bandwidth is an order of magnitude more reproducible than either tick
   statistic**, which is why bandwidth-motivated work is judged on bandwidth.
2. **p99 and the mean are equally stable on a quiet box.** The mean's advantage
   appears only under contention (it moved 1.7× where p99 moved 3.3× with a
   deploy sharing the host). An earlier revision claimed p99 was *tighter* than
   the mean; that came from two runs and did not survive six.

`host.load_avg_1` is recorded in each file. It rose from 9.00 to 15.29 across the
six runs as the generator warmed up, and p99 tracked it — which is the reason the
figure is recorded per run rather than assumed constant.
