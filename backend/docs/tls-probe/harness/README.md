# Probe build harness

Scaffolding for driving `../Il2cppTlsProbe.cs` in a real player. Kept separate from the
probe itself: the probe is the artefact under review, this is the thing that drives it,
and entangling the two would let a change to the driver look like a change to the answer.

## Install

| file | goes to |
|---|---|
| `../Il2cppTlsProbe.cs` | `Assets/_TlsProbe/` |
| `TlsProbeQuit.cs` | `Assets/_TlsProbe/` |
| `TlsProbeBuild.cs` | `Assets/_TlsProbe/Editor/` |

The scene is **created programmatically** by `TlsProbeBuild`, not committed — a
hand-written `.unity` is GUIDs and YAML that can drift from the component it is supposed
to instantiate, and the point of this build is that what runs is what was reviewed.

## Run

```bash
# Pick the editor by ProjectSettings/ProjectVersion.txt. Globbing the Hub directory
# picks the oldest install and silently downgrades the asset database.
"/c/Program Files/Unity/Hub/Editor/6000.3.9f1/Editor/Unity.exe" \
  -batchmode -quit -nographics \
  -projectPath 'E:\path\to\UnityProject' \
  -executeMethod TlsProbeBuild.Build \
  -probeStripping Minimal \
  -probeOutput 'E:\path\to\UnityProject\build\tlsprobe' \
  -logFile 'E:\path\to\UnityProject\tlsprobe-build.log'

# then repeat with -probeStripping High

cd build/tlsprobe/Minimal
./TlsProbe.exe -logFile "$PWD/probe.log"   # quits itself after 12s
grep -a 'tls-probe' probe.log
```

## Two things this harness exists to survive

**A build can fail and still leave a plausible .exe.** IL2CPP died at
`fatal error C1085: ... No space left on device` *after* the launcher was written; Unity
reported `result=Failed`, exited **0**, and left a 652 KB `TlsProbe.exe` with no
`GameAssembly.dll`. Running that shell pops a modal `Failed to load il2cpp` and writes a
zero-byte log — from a script, indistinguishable from a player still starting. So
`Build()` asserts the build result **and** `GameAssembly.dll`, and you should read a
terminating line out of the player's own log before believing the run.

**A settings restore that is not flushed never happens.** The build persists
`ProjectSettings.asset` with IL2CPP and the requested stripping level; the restore in the
`finally` is in memory, and `-quit` exits without writing it. One run logged
`restored backend=Mono2x` and still left `Standalone: 1` and
`managedStrippingLevel: {Standalone: 4}` on disk — which the *next* run then read as the
value to preserve, so the leak compounds. `AssetDatabase.SaveAssets()` after the restore
is what makes the log true.

Check with `git status ProjectSettings/ProjectSettings.asset` when the build returns, and
read the **values**, not the fact that the file changed. With the flush in place the file
still differs from `HEAD`, because keys that were absent are now written explicitly:

```diff
   scriptingBackend:
     Android: 1
+    Standalone: 0          # Mono2x — the leak was `1`, IL2CPP
-  managedStrippingLevel: {}
+  managedStrippingLevel:
+    Standalone: 0          # Disabled — the leak was `4`, High
```

That diff is a no-op; `Standalone: 1` / `4` is the real leak. `git checkout` the file
afterwards to keep the worktree clean.

Budget ~25 GB of scratch in `Library/Bee` for a from-scratch IL2CPP build. That directory
is regenerable, so deleting it is the cheapest way to get the space back.
