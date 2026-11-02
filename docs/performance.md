# Search performance

Measured with the opt-in benchmark `tests/MusicFinder.IntegrationTests/SearchBenchmarkTests.cs`:

```
MF_BENCH=1 dotnet test tests/MusicFinder.IntegrationTests --filter "FullyQualifiedName~SearchBenchmarkTests"
```

`MF_BENCH_TRACKS` changes the size (default 100,000); `MF_BENCH_KEEP=1` keeps the generated database as `%TEMP%\mf-bench.db` for profiling by hand. Results are written to
`%TEMP%\mf-bench-results.txt`.

## What it measures

A synthetic library of 100,000 tracks, generated with a fixed seed: 40% English artists and titles, 35% Greek, 15% Greeklish, 10% mixed, in 400 folders (every fifth called "Λαϊκά N"),
BPM on 80% of them. The generator reuses about 20 Greek artist names, so a Greek or Greeklish query typically matches **thousands** of tracks: a harsher test than a real library, where most
names are distinct. Each query: 20 warm-up runs, then 50 timed runs of a page of 200 (`SearchAsync`) and of the count (`CountAsync`) on a warm database, one connection per call as in the app.

## Results (100,000 tracks, one run on the development PC)

| What | Value |
|---|---|
| Building the smart-search index | 100,000 tracks in about 27 s (3,700 tracks/s), in the background |
| Word list for typo correction | about 102,000 words, read in 170 ms |
| Database file | 135 MB (about 40 MB of it the new index) |
| Memory (whole test process) | about 100 MB |

Page of 200, p95:

| Query | p95 (ms) |
|---|---|
| empty search, sorted by title | 2 |
| `"midnight groove"` (phrase) / `the` / `le fre` | 3 / 2 / 5 |
| `papzoglou` (typo, corrected) | 16 |
| `papaz` (Greeklish prefix) | 28 |
| `mazwnakhs` | 29 |
| `artist:xaris` | 29 |
| `xaris alex` | 35 |
| `giorgos` (exists in both scripts) | 37 |
| `alej;ioy` (wrong keyboard layout) | 45 |
| `παπ` (Greek prefix, matches about a third of the library) | 48 |
| `xa` (2 letters) | 48 |
| `pa` (2 letters) | 72 |
| `λαικα` (the folder name of 20% of all tracks) | 69 |
| `bpm:120-128 disco` (a word on 25% of tracks plus a filter) | 83 |
| page 2 by keyset cursor | about 33 (p50 26–33) |

Counts (the "N results" figure, computed separately and not blocking the page): 1–13 ms, 54 ms for the broad `disco` + BPM query.

**Verdict.** The target "common queries p95 < 50 ms on 100k tracks" is met for typical Greek, Greeklish, wrong-layout, scoped and typo queries and for browsing. It is **not** met for very broad
queries on this synthetic data: two-letter prefixes and words that match a quarter of the library (about 70–85 ms). They are still well under the 100 ms at which typing feels delayed.
On the real library of 3,713 tracks the page queries measured 0.3–5 ms (median of the id step; the rows of the page are read afterwards): `love` 1.2, `radiohead` 0.6, `vradines` 0.8, `the` 4.9, `a` 5.1, empty 0.3.

## What mattered

Found by profiling (`MF_BENCH_KEEP=1`, then timing the parts of one query):

- **`IN (a UNION b)`, not `IN a OR IN b`.** With an OR the planner cannot start from the matching ids and scans every track (the `bpm:` query went from 181 to 97 ms with this alone).
- **Late materialization.** The page query returns only `(Id, sort key)`; genres, tags, covers and the rest are read for the 200 rows of the page, not for every match.
- **Order by the alias**, so the relevance expression is evaluated once per row, not twice.
- **Skip the original index once the new one is complete** (and the second "primary" set): two fewer full sets per query. Re-checked every two seconds.
- **An expression index on the title sort key** (migration 0006) plus `+t.Missing = 0` (so the planner does not pick the nearly useless `Missing` index) and a row-value keyset comparison:
  browsing and the empty search went from 60 ms to 2 ms and stay constant for any page depth in ascending order.

What still costs: ranking needs a set of matching ids for each tier (as large as the match set itself), so a query that matches 25,000 tracks pays for about five such sets. An idea not done:
fetch tier by tier and stop as soon as a page is full.

## Find Similar

Candidates are limited to tempo-near, related-genre and shared-tag tracks (cap 20,000) and scored in memory. Not part of the benchmark yet, so no timing is claimed; on the real library the list appears with no visible delay.
