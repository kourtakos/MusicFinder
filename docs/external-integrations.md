# External integrations

What MusicFinder talks to, what was verified against official sources (and when), and what is still to check. Nothing here is assumed from memory.

## MusicBrainz web service (identification)

- Base `https://musicbrainz.org/ws/2/`, `fmt=json`. Rate limit: about 1 request per second per IP on average; above it **all** requests are declined with HTTP 503 until the rate drops. Every request needs a `User-Agent`
  `AppName/version ( contact )`; blank or generic agents are throttled harder. (Verified 2026-11-02 against the rate-limiting page.)
- Calls used: `recording?query=<Lucene>&limit=<n>` (search), `recording/<mbid>?inc=artist-credits+releases+release-groups+isrcs+genres`, `release/<mbid>?inc=artist-credits+recordings+release-groups+labels+media`.
  `inc=artists` is not a valid recording include; artist data comes through `artist-credits`.
- JSON shapes checked against live responses on 2026-11-02: search returns `recordings[]` with `id`, `score` (0-100), `title`, `length`, `artist-credit[]` (`name`, `joinphrase`), `releases[]` (with `release-group`
  `primary-type`/`secondary-types`, `date`, `country`, `status`); a recording lookup has `genres[]` (`name`, `count`) and `isrcs[]`; a release lookup has `media[].tracks[]` (`position`, `title`, `recording.id`), `track-count`, `label-info[].label.name`.
  In search results the track list is `media[].track[]` (singular), in lookups `tracks[]`. The parser tolerates missing or oddly typed fields.
- Search field names `recording:` and `artist:` with quoted phrases were confirmed by a live query; `release:` is used for the album and **not yet confirmed**. The official search documentation page could not be read (browser check); limit semantics for search: the client clamps to 1-25.
- Not verified: whether MusicBrainz recording genres are reliably present (they were empty for the recording sampled; artist genres were not used).

## AcoustID web service (identification)

- `https://api.acoustid.org/v2/lookup`, GET or POST; a gzip-compressed POST (`Content-Encoding: gzip`) is preferred for long fingerprints. Parameters: `client` (application key), `duration` (whole file, seconds), `fingerprint`, `meta` (`recordingids` is used),
  `format=json`. Limit: at most 3 requests per second. **Free for non-commercial use only** (commercial: acoustid.biz). Keys are per application, registered at acoustid.org/new-application. (Verified 2026-11-02.)
- Response: `status`, `results[]` with `id`, `score` (0-1) and, with `meta=recordingids`, `recordings[].id`.
- **Not verified**: the exact JSON of an error answer (the client reads `{"status":"error","error":{"message":...}}` and falls back to a generic message), the maximum request size, and the attribution wording AcoustID requires. To check with a real key.

## Chromaprint fpcalc (identification)

- Windows x86_64 build 1.6.2: `chromaprint-fpcalc-1.6.2-windows-x86_64.zip` from the Chromaprint GitHub releases (listed on acoustid.org/chromaprint, 2026-11-02).
- Command used: `fpcalc -json -length <seconds> -- <file>`. Options read from `fpcalc.cpp`: `-length` default 120 s (0 = unlimited), JSON keys `duration` and `fingerprint`; exit code 0 ok, 2 invalid input / cannot decode / empty fingerprint, 3 read failure after partial output.
- **Licence of the exact binary: not verified** (the README read did not state it; FFmpeg and optionally FFTW are involved). It is therefore **not bundled** and not redistributed by this project; the user supplies it.
- To verify with a real binary: Greek characters and spaces in the path (the code passes it as a single argument without a shell), that `duration` is the whole file when `-length` is smaller, behaviour on network drives.

## Cover Art Archive (covers)

- `https://coverartarchive.org/release/<mbid>/front-500` (also `front-250`, `front-1200`, `front`). A successful request answers **307** with the image on archive.org (the HTTP client follows it); **404** means no front cover was chosen for that release (a thumbnail size can also 404). (Verified against the Cover Art Archive API page, 2026-11-02.)
- The page says there are no rate-limiting rules but lists 503 for users who exceed one, and states no User-Agent rule. MusicFinder sends only its own name and version, and never more than one request at a time to the host.
- The release lookup JSON of MusicBrainz carries `cover-art-archive` (`front`, `back`, `count`), which shows whether a cover exists.
- **Not yet run against the real service**: the redirect to archive.org, and the formats and sizes of real covers. Tested with fake responses only.

## Windows

- Global hotkey (`RegisterHotKey`), OLE drag-and-drop with `FileDrop`, and window-under-cursor detection: see `docs/quick-search.md` and `docs/dj-session.md`.

## VirtualDJ and Traktor (M4, not started)

Formats and locations are not assumed. See `docs/traktor-test.md` for what to collect from a real installation.

## Opt-in live checks (not part of the default test run)

Nothing in the default suite contacts a real service. A live check needs a real AcoustID key, a contact address, `fpcalc.exe` and an audio file of your own; none of these belong in the repository.
