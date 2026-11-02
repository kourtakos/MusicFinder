# Probable duplicates and other versions (M6, slice 5)

Health tab, **Find probable duplicates and other versions…**. The review reads library data only (artist, title, duration, bitrate, plays): no file is opened, and there is no delete, move, rename or merge command. Groups are suspicions to review.

## What a song is
A title is split into the song and its version: `Frozen (Radio Edit)`, `Frozen [Radio Edit]` and `Frozen - Radio Edit` are the song `Frozen` in the version `radio edit`. Recognized versions: radio edit, extended, club, instrumental, acoustic, a cappella, live, demo, dub, remaster, clean/explicit, `<name> remix/rework/bootleg/vip`, `<name> edit/mix/version`. "Original mix", "album version" and "single version" are the plain song. A bracket that is not a version (`(Part 2)`, `(1)`) stays in the title, so different songs are not merged. `feat./ft./featuring` in the artist or the title is parsed out; the first credited artist is the artist of the song. Case, accents and punctuation do not matter, and Greek and Greeklish spellings of the same words share a phonetic key (heuristic, see `docs/search-syntax.md`).

## Two lists
- **Probably the same recording:** same song, same version, lengths within 3 seconds (tracks with no stored length are never put here). Suggested file to review: lossless over lossy, then higher bitrate, then more plays, with the reason. It is a hint from tags and file facts, not a check of the audio.
- **Songs in more than one version:** the same song as a remix, edit, live version, remaster or with a clearly different length. Listed for information; not duplicates.

Exact byte-identical copies (the exact duplicate scan) also appear in the first list when their tags match.

## Actions
Open in Explorer, List in Library (searches `artist:"…" title:"…"`), and **Not a duplicate**, which hides the group for good (table `ProbableDismissals`, migration 0012). Groups themselves are never stored.

## Tests
Unit: song identity (version words, brackets, feat., Greek/Greeklish) and grouping (tolerance, remix apart, unknown durations, dismissals, stable order). Integration: a real scan of generated files, nothing changed, dismissals persist. UI: review window, list in Library, dismiss, no delete/move/rename member exists.
