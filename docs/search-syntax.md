# Search syntax (M1)

Search runs while you type (120 ms debounce, latest query wins). Free text combines with the selected folder and the
Genre / BPM / Decade filters; filters never exclude one another.

## Free text

- Words are combined with AND and match as **prefixes** across file name, title, artist, album artist, album, genre and path.
  `chic fre` finds "Chic – Le Freak".
- `"quoted phrase"` matches the words together, in order.
- Matching is case-insensitive and ignores accents, including Greek tonos and final sigma: `αλεξιου` finds `Αλεξίου`.
- **Smart Greek search** is automatic: `xaris alexiou`, `haris alexiou` and `x;aris` (Greek typed with the US layout) find Χάρις Αλεξίου, and Greek finds Greeklish-tagged tracks. Field filters (`artist:xaris`) use the same matching. Exact matches are listed first. See `docs/greek-search.md`.
- Punctuation and FTS operators (`AND`, `OR`, `NEAR`, `*`, `-`, `:` …) are treated as plain text; they cannot break the query.
- An empty query lists the tracks of the current scope.

## Field filters

| Filter | Meaning |
|---|---|
| `artist:madonna` `title:frozen` `album:…` `genre:disco` | Match inside one field (prefix match). Use quotes for several words: `title:"like a prayer"`. |
| `ext:flac` | File extension. |
| `key:8A` | Musical key (case-insensitive, exact). |
| `bpm:120` / `bpm:115-125` | Exact value or inclusive range. A reversed range is swapped. |
| `year:1985` / `year:1980-1989` | Year or range. |
| `decade:80s` / `decade:1980s` | Decade shorthand for a ten-year `year` range (`10s` = 2010–2019, `00s` = 2000–2009). |
| `missing:title` | Tracks with no value for `title`, `artist`, `album`, `genre`, `year`, `bpm` or `key` (a BPM of 0 or a blank key counts as missing). |
| `missing:art` | Tracks with no embedded cover (also `missing:cover`). |
| `issue:any` | Tracks flagged by Library Health: `issue:mojibake`, `error`, `lowbitrate`, `samplerate`, `mismatch`, `size`, `duration`, or `any` (see library-health.md). |
| `bitrate:320` / `bitrate:0-127` | Bitrate in kbps, exact or range. |
| `dup:exact` | Files that have a byte-identical twin found by a duplicate scan; `dup:` and the first letters of a content hash lists one group. |
| `codec:flac` | Part of the codec name the file reports. |
| `played:never` | Never played (no possible or confirmed play). |
| `played:possible` / `played:confirmed` | At least one possible / confirmed play. |
| `played:session` | Played (possibly or confirmed) in the live DJ session; matches nothing when no session is running. |

Unknown prefixes such as `foo:bar` are searched as literal text. Invalid values (`bpm:abc`, `missing:banana`, `artist:`)
show a non-blocking warning under the search box and are ignored.

## Scope and facets

- Select a folder to limit results; **Include subfolders** makes it recursive. **Global search** ignores the folder.
- The Played dropdown filters by play status and can hide tracks already played in the live session (see `docs/dj-session.md`).
- Genre, Tags and Decade dropdowns are multi-select: OR inside a facet, AND across facets and with the text query.
  BPM presets are single-select ranges.
- The Genre dropdown uses the genre taxonomy: choosing a parent (e.g. Disco) also matches its sub-genres (Nu Disco, Italo Disco, …).
  `Unknown genre` finds tracks with no genre text; `Uncategorized` finds tracks whose genre text matches nothing in the taxonomy.
  Genres you assigned yourself count too. The typed `genre:` filter still searches the raw genre text in the file.
- Tags filters by your DJ tags (Warm Up, Wedding, …).

## Sorting and paging

The default order is **Relevance** (best match first, then by title; with an empty search box, by title). Click a column header (Title, Artist, BPM, Key, Time, Last Played, Plays) to sort by it, again for descending, a third time to return to Relevance. Sorting happens in SQLite; missing values sort last in both directions;
ties are broken by track id so paging is stable. Results load 200 at a time as you scroll.

## Known limits

- Greek ↔ Greeklish matching is a best guess (no exact spelling rules exist); names it cannot connect, like `Daft Punk` and `νταφτ πανκ`, need an alias in Settings → Search. Only the standard Windows Greek layout is understood for wrong-layout input.
