#!/usr/bin/env python3
"""Fail the build when a test run selected or executed nothing.

`dotnet test` exits 0 when every selected test passed, when it selected nothing, and
when everything it selected was skipped -- the three readings are identical. This reads
the .trx `Counters` element instead, which can tell them apart.
"""
import glob
import sys
import xml.etree.ElementTree as ET

NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"

files = sorted(glob.glob(sys.argv[1], recursive=True))
if not files:
    print(f"FAIL: no .trx matched {sys.argv[1]} -- the run produced no results file at all.")
    sys.exit(1)

bad = False
for f in files:
    counters = ET.parse(f).getroot().find(f"{NS}ResultSummary/{NS}Counters")
    if counters is None:
        print(f"FAIL: {f} has no Counters element.")
        bad = True
        continue
    total = int(counters.get("total", 0))
    executed = int(counters.get("executed", 0))
    passed = int(counters.get("passed", 0))
    failed = int(counters.get("failed", 0))
    print(f"{f}: total={total} executed={executed} passed={passed} failed={failed}")
    if total == 0:
        print(f"FAIL: {f} selected no tests. An exit code cannot tell that from a pass.")
        bad = True
    elif executed == 0:
        print(f"FAIL: {f} executed nothing -- every selected test was skipped.")
        bad = True

sys.exit(1 if bad else 0)
