# Measuring things on this project

Every expensive defect this project has shipped had the same shape: **it produced a
plausible number instead of an error.** Not a crash, not a red test — a believable result
about the wrong thing, or about nothing at all.

This document is the distilled list. It is not general engineering advice; every entry
below is something that actually happened here, with the cost attached, and most of them
happened *more than once*.

---

## 1. An empty result reads as good news

The single most expensive pattern on this project. A query that matches nothing, a counter
that was never incremented, a check that never ran — all of them look exactly like success.

Incidents:

- **`gh pr checks` on a CONFLICTING pull request prints nothing.** GitHub runs no checks on
  an unmergeable PR. A poll shaped `pending == 0 && failures == 0` therefore exits
  immediately and reports green. A broken build was declared ready to merge on that reading.
  **Poll on the PASS count**, and require it to be non-zero.
- **`snapshot_entities_gathered`, `snapshot_max_gather` and `snapshot_anchor_missing` read
  `0` for two days** while the server sent 15MB of snapshots. Their per-tick reset sat
  between the gather that wrote them and the call that recorded them. The irony is the
  lesson: `snapshot_anchor_missing` exists *specifically* to expose a healthy-looking zero,
  and it had that failure itself.
- **A required CI check that never runs never passes.** Six checks were excluded from
  docs-only PRs by `paths-ignore` while being required by branch protection. Those PRs
  showed a **fully green check list** and could not be merged, and had been going in by
  administrator override.
- **`dotnet test` exits 0 when it matched no tests.** The headless CI step reads the `.trx`
  counters instead, and fails on `total == 0`.

**The rule:** before trusting an instrument, get a **non-empty** result out of it. A gate
that matches nothing must fail, not pass.

---

## 2. A test only ever seen passing is indistinguishable from one that cannot fail

Mutation-test anything that matters: revert the logic, watch the specific test go red,
restore, re-verify. Report the table.

Incidents:

- A reflection guard asserting a type's constructor shape queried only **public**
  constructors, while the bug was a **private** one — VContainer reflects with
  `BindingFlags.NonPublic` included. The guard passed while the build was broken. Worse: the
  mutation chosen to validate the guard was drawn from the *same wrong assumption*, so it
  agreed. **Two checks, one blind spot, zero signal.**
- A test asserting "enemy population reached zero" was trivially true for the 22 ticks before
  the first wave spawned, and **passed with the feature deliberately removed**.
- Prefer asserting a **change against a control arm** over asserting a state. Where a test
  says "X was capped", add a lower-bound test too: a build running at half rate satisfies
  every upper bound.

**MSBuild's timestamp granularity is one second.** Mutations applied back to back inside the
same second are silently tested against the *previous* binary. A mutation pass that reported
three survivors was entirely false for this reason. Force a rebuild between mutations.

---

## 3. Measure the object you are talking about

Five wrong-object failures happened in one session. All produced internally consistent
numbers about the wrong thing, and no exit code caught any of them.

- `[DOTSNet/health]` reported `lastCorrection`, `snaps` and `reconciles` — **all three from
  the local player's predictor** — and they were being read as evidence about *enemy*
  smoothness. Different object, perfect numbers.
- Every client counter measured **frames**, so a snapshot carrying one entity and one
  carrying eight were indistinguishable. A client rendering 1 of 8 entities produced a
  flawless health line. "The wire is delivering nine" was an **inference**, never a
  measurement — and five instrumented builds were spent before the cause turned out to be
  positional and upstream.
- A render probe reported 47–50% "frozen frames" for enemies. The enemies were **standing
  still by design**, having closed to contact range. Corroborated by the server's own delta
  encoder reporting 31–40% of entities unchanged per snapshot. The instrument was right; the
  interpretation was the wrong object.

---

## 4. Controls, and what disqualifies one

A comparison needs both arms to differ in **exactly one** thing.

- A `-73%` improvement for field-level delta turned out to be a **cross-build artefact**. The
  controlled figure was **32.2%**.
- Two benchmark projects sharing one directory shared `obj/` and `bin/`, so the baseline run
  served the *modified* build's cached binary and printed two identical columns.
- A control run for "did my commit break CI" was taken against a workflow run with **one job
  that never invokes Unity**. It was green and meaningless.
- Disabling enemy chase to isolate an AI effect **also collapsed the population** (enemies
  walked to the origin and despawned), so the arms differed in two variables — 17 entities
  against 339. Not a control.

**Where the arm is selected matters.** Prefer one binary with a runtime flag over two
builds: a percentage is only comparable against the same scene, the same spawner and the
same observer position.

---

## 5. Verify remote and live state, not the exit code

Six commands exited 0 having done nothing in a single session.

- `git rev-parse` prints its argument to stdout **and exits 128** when a ref is missing, so
  `git rev-parse foo || echo missing` hands back the literal string as if it were a SHA. Use
  `git cat-file -e` first.
- `gh pr edit --body-file` silently no-ops on these repos (a deprecated `projectCards`
  GraphQL field). It looks successful. Use `gh api -X PATCH` and **read the body back**.
- After a push, `git ls-remote`. After a config change, read the setting back from the API.
  After an env change, read it off `/status` on the running process.
- **A CI re-run replays the same commit.** It cannot prove a fix pushed afterwards, and a
  still-conflicting PR never ran the new head at all.

---

## 6. Configuration that is set but never arrives

Compose's `environment:` block is the whole list. A variable absent from it is **not
forwarded**, however carefully it is set in `.env` or the shell — the process takes its
built-in default and `/status` reports a configuration nobody chose.

This has happened **three times**: `GAMESERVER_IMPORTANCE_W_*` (an entire measurement arm ran
against defaults), the enemy-AI knobs, and the combat knobs one PR after the fix. Defaults
that are sensible make it invisible, because nothing looks broken — the knobs are simply
inert.

`gameserver-dotnet-map02` declares its **own** environment block and inherits nothing, so a
fix covering one service is worse than none: the two then disagree, which is harder to find
than the original gap.

**A comment in the compose file has now failed twice.** The link between "add a
strictly-parsed knob" and "list it in two services" has to be mechanical.

---

## 7. Changing what a number means falsifies documents no test can reach

- The **150 players-per-server ceiling** was a correct measurement, taken before Protobuf and
  id-interning removed 81% of the wire. The context changed and the number stayed, in a
  tier-sizing table, for months.
- ADR-27's decision to ship replication tiering OFF rested on "the measurement says they buy
  nothing" and "field-level delta is unmeasured". **Both became false**; the decision survived
  on new grounds. Superseded reasoning is quoted in place rather than deleted, because a
  silently rewritten reason cannot be checked against what it replaced.
- A frozen-frame improvement attributed to enemies dying rather than accumulating is
  **contingent on kill throughput**. Raise enemy HP or population and it regresses — same
  cause, not a new defect. Recorded as contingent before it could harden into a premise.

When a parameter changes meaning, **grep for the old meaning** and rewrite what you find.

---

## 8. Instruments on this machine that lie

Specific to this development box, and each has cost real time:

| Instrument | Failure |
|---|---|
| `timeout.exe` | returns instantly without a console, so every "wait N seconds" is 0s — the polled thing then looks frozen |
| `powershell.exe Start-Sleep` | works, until WSL interop degrades; bracket waits with `date` and check the elapsed time |
| `tasklist.exe` / `taskkill.exe` | fail silently when WSL→Windows interop is degraded (`UtilAcceptVsock: accept4 failed 110`) |
| WSL `docker` wrapper | fails ~8 in 10 while the daemon is healthy; call `docker.exe` by full path, with **Windows** paths for `-f` and `--project-directory` |
| `tail -f` on `/mnt/e` | DrvFs has no inotify, so a watch never fires and the silence reads as "still running" |
| Unity batch mode exit code | returned 0 with tests failing, and `result=Failed` still leaves a plausible `.exe` on disk |
| A rendered diff | has twice called differing files identical; prove sameness with `cmp`, a hash, or a tree id |

**A running player locks `lib_burst_generated.dll`** and fails the next build with
`result=Failed` — while leaving the previous, working executable in place.

---

## The habit underneath all of it

Ask of every green result: **what would this look like if the thing I am measuring were
broken?** If the answer is "the same", the measurement is not evidence yet.
