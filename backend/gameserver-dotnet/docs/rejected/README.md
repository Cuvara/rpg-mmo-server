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
