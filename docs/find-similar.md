# Find similar

Select a track and choose **Find similar** (right-click menu or the button in the details panel). The grid then lists tracks that fit well next to it, best first, with a **Match**
score and a **Why** column. **Close** (or typing a search, changing a filter or folder, or clicking a column to sort) goes back to normal results.

**What "similar" means.** Metadata compatibility for mixing: genre, BPM, musical key and your DJ tags. It is **not** acoustic similarity. It says nothing about energy, mood or how
the music sounds, and it never invents a missing BPM or key.

## The score (0–100)

| Signal | Weight | How it is valued |
|---|---|---|
| Genre | 35 | same genre 1.0; parent and child, or two sub-genres of one parent 0.6; only the same family 0.3; otherwise 0 |
| BPM | 30 | 1 at the same tempo, falling in a straight line to 0 at the window's edge (default ±8 BPM) |
| Key | 25 | same key 1.0; relative major/minor (same number, other letter) 0.9; neighbour on the Camelot wheel (±1, 12 wraps to 1, same letter) 0.8; otherwise 0 |
| DJ tags | 10 | shared tags divided by all tags of the two tracks; counted only if the selected track has tags |

A signal counts **only when both tracks have it**, and the weights of the signals that count are re-normalized: `score = 100 × points earned ÷ weight available`. A missing key never
lowers a score. A result is shown only if signals worth at least 55 of the 100 points could be compared; otherwise the score would claim more than the data supports. If the
selected track itself has fewer than two of genre, BPM and key, the list says so ("This track needs at least two of genre, BPM and key… MusicFinder does not guess missing values")
and shows nothing.

Example (seed: Disco, 120 BPM, 8A): a Disco track at 123 BPM in 9A scores 82 (35 + 18.75 + 20 of 90 available); a Nu Disco track at 120 BPM in 8A scores 84 (21 + 30 + 25 of 90).

**Half and double tempo** (on by default): 70 and 140 mix, so the selected track's tempo doubled and halved are compared too. Such a match is labelled `BPM ×2 (+1)` or `BPM ½ (-2)`;
a direct match wins a tie. Turn it off in the banner.

## Why each result is there

The Why column spells the evidence out: `same genre (Disco)`, `related genre (Disco ≈ Nu Disco)`, `BPM +3`, `same BPM`, `BPM ×2 (+0)`, `same key (8A)`, `relative key (8A → 8B)`,
`adjacent key (8A → 9A)`, `shared tag: Warm Up`. `same artist` and `same decade (1970s)` are also shown but are **not scored** (there is no validated weight for them); the same artist is
only used to break ties.

## Keys

Camelot (`8A`, `08a`), Open Key (`1m`, `1d`) and note names (`Am`, `A minor`, `F#m`, `Gbmaj`, `C`) are understood and compared with each other. Anything else is treated as "no key".
Camelot wheel used: minor 1A Ab · 2A Eb · 3A Bb · 4A F · 5A C · 6A G · 7A D · 8A A · 9A E · 10A B · 11A F# · 12A Db; major 1B B · 2B F# · 3B Db · 4B Ab · 5B Eb · 6B Bb · 7B F · 8B C · 9B G · 10B D · 11B A · 12B E.
Only the standard rules above are used; "energy boost" diagonals are a matter of taste and are not included.

## Options

BPM window (3, 5, 8, 10, 12), half/double tempo, and *Hide played in last 24 h* (uses the play history of DJ sessions; possibly-played counts). They are remembered.
The same file is never listed. Missing files are not suggested. At most 100 results are shown.

## Order and stability

Higher score; then more evidence behind it; the same artist; a direct tempo match before half/double; the closer tempo; the title; the track id. The same library and settings always give
the same list.

## Large libraries

Candidates are the tracks within the tempo window (also at half/double), sharing a related genre or a DJ tag, nearest tempo first, capped at 20,000, then scored in memory.
