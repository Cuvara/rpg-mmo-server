# Encoding sweep — second run (post entity-leak fix)

Re-run of the Part II sweep after the entity-leak fix, with the **baseline image
rebuilt from `7c4108b`** so that fix sits on both sides of the comparison. Kept
separate from `../encoding/` (the first run, against a `f4d5561` baseline) rather
than overwriting it, so both are quotable and the difference between them is
inspectable.

Levels: 50 / 100 / 150 / 200, three arms. The first run also swept 250–400 for
the ceiling search; those files live in `../encoding/` and were **not** re-taken.

Analysis and the conclusions drawn from these files, including a claim withdrawn
on the strength of them: [`backend/docs/BENCHMARK.md`](../../../docs/BENCHMARK.md) §16.

Regenerate the tables with:

```bash
python3 scripts/encoding-report.py results/encoding-rerun
```
