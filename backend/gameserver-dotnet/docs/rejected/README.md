# Rejected approaches

Work that was written, evaluated, and **not** taken. Kept because the reasoning
behind a rejection is worth more than the diff, and because the next person to
hit the same problem will reach for the same idea — this directory is how they
find out it was already tried and what went wrong.

**Nothing here applies to the current tree, and nothing here should be applied.**
Each patch is a snapshot against the commit it was written on. They are a record,
not a backlog. If one of these ideas becomes right again, the argument for it has
to be re-made against what the code does *now*, not resurrected by `git apply`.

Each entry is `<date>-<slug>.patch` plus a section below stating what it did, why
it was rejected, and what landed instead.

---

## `2026-08-19-slow-client-rescale-and-skip.patch`

**Problem it addressed:** #154 — `SlowClientMovementTests` was flaky. A pair of
snapshot samples spanning a lost frame was read as a single step and landed on
exactly `2.00x`, the value the test rejects, so a busy CI runner reported a
repaid pause against a server that had done nothing wrong.

**What it did.** Two things, and it is the first one that sank it:

1. **Rescaled** multi-interval samples instead of discarding them —
   `StepLog.MovingIntervals(sample)` divided a sample's distance by the number of
   world intervals its tick span covered, crediting only time after the resume.
2. Retried the scenario up to three times and **skipped** the case if every
   attempt stalled, via `[SkippableTheory]`.

**Why it was rejected.**

The rescaling is the substantive half, and note that this was not a hypothetical:
`3951c40` ("normalize SlowClient distance by frame gap") **landed on `develop`
first**, and `dd316ca` ("discard multi-gap samples instead of normalizing") then
took it back out. So the approach in this patch was tried in the mainline and
reverted, and the reason is now written into the code it was reverted from:

> Samples spanning a lost frame are discarded by `SampleStepsAsync` rather than
> rescaled — see there for why rescaling silently hid the very repayment this
> asserts.
> — `GameServer.Tests/Server/SlowClientMovementTests.cs:187`

> A pair spanning more than one world interval is discarded rather than rescaled
> — it is a scheduling artifact, not a measurement of a single step, and
> rescaling hides the repayment that `ASilentClientsRepayment` exists to see.
> — `GameServer.Tests/Server/SlowClientMovementTests.cs:313`

That is the whole argument. A sample spanning a lost frame is not a small
measurement of one step; it is not a measurement of one step at all. Dividing it
down produces a number in the plausible range, which is worse than a number that
is obviously wrong, because the assertion the test exists for — that a silent
client's owed time *is* repaid — is exactly what the division averages away. The
test would keep passing while losing the ability to fail.

The skip half is a weaker objection and worth stating accurately rather than
overstating: `[SkippableTheory]` is **not** banned in this suite — `KcpInteropTests`
uses it legitimately, to skip when an external interop probe is genuinely absent.
The objection is to skipping on *timing*: a case that skips when the runner is
busy converts the flake into a silent absence of coverage under exactly the load
that would expose the defect. This repo treats a skip as "not verified", never as
a pass; `backend/deploy/k8s/verify` prints that distinction on every run for the
same reason.

**What landed instead.** `3951c40` then `dd316ca` — discard multi-gap samples at
the source, keep only single-interval pairs, no skip. Issue #154 is closed.

**Provenance.** Recovered from the uncommitted working tree of a worktree at
`.claude/worktrees/task-flaky-tick`, branch
`fix/gameserver-dotnet/slow-client-sampler`, during the branch cleanup of
2026-08-19. The branch itself carried no commits beyond `develop`; the work
existed only as uncommitted changes and would have been destroyed by a
`git worktree remove --force`. Written against `1dfdc3b`, and it does **not**
apply to `develop` — both files it touches have since changed.

---

## `2026-08-19-slow-client-burst-three.patch`

**Problem it addressed:** #175 F1 — the two bursty-client cases in
`SlowClientMovementTests` assert `travelled > expected * 0.85` and were reported
as having almost no margin. The arithmetic in the issue is correct as far as it
goes: `MeasureAsync(..., sendHz: 15, burst: 4)` idles `4 * (1000/15) = 264 ms`
between bursts, and on the single-rate `Rates(15, 15, 5)` case the server's
banking cap is `MaxBankedMovementTicks(15) = 4` ticks of `66.67 ms` = `266.7 ms`.
That is 2.7 ms of design margin, and every millisecond `Task.Delay` overshoots is
time the server discards by design.

**What it did.** Changed `burst: 4` to `burst: 3` in both cases, on the reasoning
that a 198 ms gap against a 266.7 ms cap buys 68 ms of jitter headroom instead of
2.7 ms.

**Why it was rejected: measured, it makes the test *more* likely to fail, not
less.** 42 runs of each variant on a 12-core box under 8 added spinner processes
(load average 11-16), values harvested from TRX by forcing the assertion to
report its numbers:

| variant | case | min | median | max | runs under 0.85 |
|---|---|---|---|---|---|
| `burst: 4` (current) | multi-rate 60/15/5 | 0.9300 | 0.9450 | 0.9717 | **0 / 42** |
| `burst: 4` (current) | single-rate 15/15/5 | 0.8883 | 0.9450 | 1.0000 | **0 / 42** |
| `burst: 3` (this patch) | multi-rate 60/15/5 | 0.8750 | 0.8883 | 0.9300 | 0 / 42 |
| `burst: 3` (this patch) | single-rate 15/15/5 | **0.8333** | 0.8883 | 0.9450 | **3 / 42** |

`burst: 2` was measured too and is indistinguishable from `burst: 4`
(single-rate min 0.8883, median 0.9450).

**Why `burst` cannot buy margin here.** The measured distance is quantised in
whole simulation ticks, and what sets it is not the banking cap but **where the
client's last burst lands**. `MeasureAsync` waits *after* sending packet `p` when
`p % burst == 0`, so the final arrival is at
`floor((packets - 1) / burst) * burst * interval`:

- `burst: 4` -> `floor(17/4) * 4 * 66 ms` = **1056 ms**
- `burst: 2` -> `floor(17/2) * 2 * 66 ms` = **1056 ms**
- `burst: 3` -> `floor(17/3) * 3 * 66 ms` = **990 ms**

`expected` is `speed * RunSeconds` = a full 1200 ms regardless. So the bursty
cases start from a ceiling of `1056 / 1200` = 0.88 before any jitter at all, and
`burst: 3` moves that ceiling down one 66.7 ms tick to `990 / 1200` = 0.825 —
below the threshold on its own. The whole observed distribution shifts down by
exactly one tick, which is what the table shows.

The 2.7 ms cap margin is real, but it is not what the 42 runs were limited by:
the single-rate minimum of 0.8883 is exactly 16 ticks, i.e. the full un-capped
send window, meaning the cap did not bite once in 42 loaded runs.

**Why the threshold was not lowered instead.** With banking removed
(`MaxBankedMovementMs` forced to 1, 8 runs) the same cases measure **0.278**
single-rate and **0.388** multi-rate — the #100 defect, and the same 28%/46%
figures the test's own doc comment quotes. The 0.85 threshold sits 3.1x and 2.2x
above the defect signal; it is the only thing separating the banked model from
the unbanked one and it is nowhere near tight.

**What landed instead.** Nothing in the assertions. The measurement above, and a
comment on the two cases recording that `burst: 4` is load-bearing and why
lowering it is a regression — so the next reader does not re-derive the same
plausible-looking fix from the same correct arithmetic. F1 was reproduced
**0 times in 42 loaded runs** here, on top of 0/32 in the original audit.
