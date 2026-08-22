#!/usr/bin/env python3
"""Every Unity-visible file in Shared.GameLogic must have a .meta beside it.

This package is consumed by the Unity client as a UPM git dependency, and Unity
imports it as an *immutable* package. A source file with no committed .meta is not
imported: its types simply do not exist on the client, while every server-side build,
test and CI check stays green because the .NET compiler does not care about .meta files.

That is not hypothetical. Shared.GameLogic 0.2.0 shipped Content/ with no .meta files
and passed all thirteen CI checks; the client then failed to compile with
"the namespace name 'Content' does not exist in the namespace 'Shared.GameLogic'",
and the fix needed a new tag because a published one must not be moved.

Run from the package directory, or with the package directory as argv[1].
"""
import os
import sys

SKIP_DIRS = {"obj", "bin", ".git"}
# Extensions Unity imports. A file Unity ignores does not need a .meta, and adding one
# is noise; the list is deliberately narrow rather than "everything not excluded".
UNITY_VISIBLE = {".cs", ".asmdef", ".json", ".md", ".txt", ".asset", ".uxml", ".uss"}


def main() -> int:
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    missing = []

    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]

        # Folders need one too: without it Unity does not descend, so every file
        # underneath is invisible even when each has its own .meta.
        for d in dirnames:
            full = os.path.join(dirpath, d)
            if not os.path.exists(full + ".meta"):
                missing.append(os.path.relpath(full, root) + "/")

        for f in filenames:
            if f.endswith(".meta"):
                continue
            if os.path.splitext(f)[1].lower() not in UNITY_VISIBLE:
                continue
            full = os.path.join(dirpath, f)
            if not os.path.exists(full + ".meta"):
                missing.append(os.path.relpath(full, root))

    if missing:
        print("::error::Shared.GameLogic has files with no .meta. Unity will not import "
              "them, and the client will fail to compile against types that exist here:")
        for m in sorted(missing):
            print(f"  - {m}")
        print("Generate them by opening the Unity project, or copy the shape of an "
              "existing .meta and give each a fresh 32-hex guid.")
        return 1

    print("every Unity-visible file has a .meta")
    return 0


if __name__ == "__main__":
    sys.exit(main())
