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

**Assert what the failure message SAYS, not just that the test went red.** A diagnostic can
survive a mutation by naming the *wrong* reason, and that is worse than one that fails —
it sends the next reader to look at something that is not there.

- The compose-passthrough gate's parser raises a distinct exception for each of four
  failure modes (service absent, `environment:` block absent, block parsed as empty, list
  form). Its test asserted that the message contained `"environment"`. Deleting the
  *missing-block* throw left that test **green**: the *empty-block* throw caught the same
  input and reported "has an `environment:` block this reader parsed as empty" — for a
  service that had no such block at all. The substring matched; the sentence was false.
  Fixed by asserting the phrase that **distinguishes** each mode. Found only because the
  mutation pass read the message rather than the red/green.

The general form: when a mutation is expected to kill a test and does, check it killed it
*for the reason you intended*. A test that goes red down an unrelated path is a test you do
not have.

---

## 2b. A stopped instrument and a quiet one read the same

"The counter stopped moving" is the shape of a fix working **and** the shape of the process
that writes it having died. Both produce an identical flat line.

This was published as a verification before it was noticed. #401's fix was confirmed by
sampling `snapshot_bytes` and `snapshot_entities_gathered` twice, 120 seconds apart, at
`connections: 0`, and finding them byte-identical — which is exactly what a game server with
a dead tick loop would also have produced. The reading could not distinguish *nothing to
count* from *nothing counting*.

**Assert a liveness signal in the same window as the flat one.** The complete shape is all
three together:

```
current_tick        45968 -> 48671   (+2703 over 45s = 60.1 Hz)   <- alive
snapshot_bytes      33018915 -> 33018915   (0)                    <- quiet
entities_gathered    4582843 ->  4582843   (0)                    <- quiet
connections         0                                             <- and here is why
```

Two related traps in the same family:

- **Flat is not zero.** After the last client leaves, an accumulated total stays large and
  legitimately so; the fix means it stops *growing*. Asserting zero fails on a correct
  server, which is how a correct fix gets reverted.
- **The mirror-image bug looks identical on an idle server.** #401 was fixed by making the
  per-tick resets unconditional. Making the *recording* calls conditional instead would also
  produce a flat counter at rest — while hiding a stopped tick loop under load. Only the
  liveness signal separates them.

The same reasoning applies to any "it stopped" claim: a queue that drains, a log that goes
silent, an error rate that falls to zero. Ask what else produces this exact reading.

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

---

## 9. Adversity that never happened reads exactly like adversity survived

Added when the first tests of the server under a degraded network landed
(`GameServer.Tests/Server/NetworkAdversityTests.cs`). Every trap below cost a run.

**A test that degrades a fake transport measures the fake.** The adversity has to sit
between two real endpoints. `AdversityProxy` is a TCP relay between a real client socket and
the real server listener: both peers are shipping code and the relay only decides *when* a
byte already produced by one is handed to the other.

**Assert that the adversity arrived, before asserting what it cost.** Measured at the client
socket, not asked of the injector — a relay that believes it is blacking out while bytes
flow anyway reports a blackout and changes nothing. A blackout that did not reach the peer
produces a green run of a test that tested nothing, which is §1 wearing a new hat.

**A ratio between two arms survives a defect that shortens both.** Deleting `ApplyHeldMovement`
left the jittered/clean travel ratio at **1.0** while both arms travelled **25%** of what they
were owed — exactly the one-in-four a 15Hz client sees on a 60Hz group. Only the absolute
assertion (`control ≈ speed × seconds`) caught it. Every arm comparison here carries an
absolute bound as well, and the mutation table in the pull request records which of the two
killed which mutation.

**Do not assert a budget the injector's own constant decides.** The first snapshot-cadence
test asserted that p99 inter-arrival stayed inside the client's 150ms interpolation cover,
and failed at 165.3ms on a ±60ms link. At ±60ms one way, arrivals can legitimately be
66.7 + 120 = 187ms apart whatever the server does: the assertion was a test of the jitter
constant. What the server owes is that it **adds no spread of its own** on top of the link's,
plus an absolute bound on the clean arm. The tolerable one-way jitter that follows — about
**±42ms**, from 150ms of cover against a 66.7ms send period — is a property of the link a
deployment may run over, and belongs in a document rather than in an assertion.

**Measure across the adversity, inside one arm, when ambient load moves whole runs.** The
first version of the downstream-stall case compared the stalled run's total distance with a
clean run's; the two were 0.2% apart in isolation and 5.4% apart under the full suite, on the
same binary. The replacement samples the player's position the instant the link goes dark and
again once the buffered burst has drained, so nothing outside the blackout enters the number
and no second run has to be commensurable with the first.

**Persistence hides entity identity.** A player who reconnects after the hold window expired
comes back at the position the store saved, which is where a *held* entity would also have
been. Position therefore cannot distinguish "the entity survived" from "the entity was
rebuilt", and a test asserting the position passes either way. The discriminator is
`EntityCount` polled **across** the gap, not read once at the end.

**Reading a keyframe from an undrained socket returns the front of the backlog.** A client
that sends for 1.5s without reading fills its receive buffer; the first `Full` snapshot
decoded afterwards is seconds old. One run reported a player at 3.333 whom the server had
already walked to 7.5, and the 4.25-unit gap looked exactly like an entity being rebuilt at
the wrong position. Drain concurrently, then resync.

### What a bad network does that loopback still cannot show

- **How much a stall costs depends on how loaded the box is, so it must not be asserted.**
  Idle, a 4s downstream blackout drops **nothing**: every tick gap stays 4, loopback send
  buffers absorb the backlog, and `Connection._sendChannel`'s 64-frame drop-the-oldest path is
  never reached — a mutation shrinking that channel to **4** frames still dropped nothing.
  The same binary under a full-suite run dropped **18 frames and opened a 12-tick gap**, and
  travel came out 5.4% short where an isolated run had it 0.2% short. Both readings are true
  and neither is about the link. This was very nearly written up as "the drop path is
  unreachable at one player" on the strength of the idle runs alone — the loaded run arrived
  afterwards and disproved it. An assertion on frame counts here is an assertion about this
  box's spare capacity; the counts are printed instead, and the class is pinned to a
  `DisableParallelization` collection so the numbers that *are* asserted mean something.
- **Reordering and duplication are not modelled.** TCP and KCP both present a reliable
  ordered stream, so the relay keeps release times non-decreasing. A datagram path would not,
  and nothing here would notice.
- **Loss is not modelled as loss.** On a reliable transport a lost packet becomes delay plus
  head-of-line blocking, which is what these tests inject. The client's tolerance of a
  genuine *gap* in the snapshot sequence is still only covered by unit tests in the netcode
  package.
- **One client, one map, no contention.** Everything here is a single player on an otherwise
  empty server. Adversity interacting with AOI pressure, the importance scheduler or the
  downlink budget is untested.
