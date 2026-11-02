# Online identification

MusicFinder can look a track up on MusicBrainz and propose tags for it. It is **off until you agree**, never needed for search or the library, and it **never writes anything without a preview and your confirmation**.

**What it is not.** It does not give BPM or key (MusicBrainz does not reliably have them, so they are never proposed). It does not download covers. A candidate is a suggestion, not a verdict.

## Turning it on

The first time you use *Identify online* (or switch it on in Settings → Online identification) a window says what is sent and asks for a **contact address**. MusicBrainz's rules require every program to say who is calling;
it is sent in the `User-Agent` header and nowhere else. You may also enter your **AcoustID application key** now or later. Declining changes nothing.

## What is sent

- **AcoustID** (only for audio fingerprints): an acoustic fingerprint of the first 120 seconds (a short code, not audio), the file's length in seconds, and your application key. The request body is gzip-compressed; the key never appears in a URL or a log.
- **MusicBrainz**: MusicBrainz ids already stored in your files, or the title, artist and album text of your tags (the file name when there is no title) for a search, and the `User-Agent` with your contact.
- **Cover Art Archive** (only when you look at a release and covers are allowed in Settings): the MusicBrainz id of that release, to ask for its front cover. The archive hands the picture over from archive.org.
- Nothing else: no file names or folder paths (except a file name used as the search text when a file has no title tag, and only if you ask for a tags search), no play history, no statistics.
- Answers are remembered on your computer (Settings → *Forget remembered answers* clears them), so the same question is not asked twice: found answers for 30 days, "no match" for 7.

## Methods

| Method | Needs | How it works |
|---|---|---|
| Search by tags | contact | Searches MusicBrainz for `recording:"title" AND artist:"artist"` (and the album when there is one). |
| Audio fingerprint | contact, AcoustID key, `fpcalc.exe` | `fpcalc` makes a fingerprint, AcoustID names the MusicBrainz recordings it matches, MusicBrainz supplies their details. |
| The file's own id | contact | Looks up the MusicBrainz recording id already stored in the file. |
| Best available (default) | | The fingerprint when it can run, else the file's id, else a search by tags. |

*Search again* asks the service again and ignores remembered answers.

## Candidates, scores and evidence

Each candidate shows the score **the service reported, by name**: `AcoustID score 0.97` or `MusicBrainz search score 87 of 100` (a candidate found through the file's own id has none). The two scales are different things:
they are never added up, averaged or compared. Next to the score, MusicFinder lists facts it worked out itself: `duration matches (3:45)`, `title differs from your tag`, `already tagged with this id`. Different
releases of one recording have different albums, years and track numbers, so **you choose the release**; the proposals change with it.

## What gets proposed, and what is ticked

Title, artist, album, album artist, year, track number, MusicBrainz recording id and (optionally) genres. A field that is **empty in your file starts ticked**; a field that **already holds a different value starts unticked**, so
nothing you wrote is replaced unless you tick it. The recording id starts ticked (it only links the file). MusicBrainz community genres are only ever a suggestion and never start ticked. Then *Preview changes* shows
before and after, and *Write* uses the same safe path as the tag editor: a verified temporary copy replaces the original, a failure leaves the file untouched, the library and search follow, the cover is kept.

## Covers

When you choose a release, MusicFinder asks the **Cover Art Archive** for its front cover and shows a small preview next to the release. It is **only offered**: *Embed this cover* starts unticked, and the cover is written only after you preview and confirm, through the same safe path as the tags (a verified temporary copy replaces the original; a failure leaves the file untouched).

- A file that **already has a cover is left alone**. Tick *Replace the cover the file has* to swap the front cover; your other pictures (back cover, booklet) are kept.
- What arrives is accepted only if its first bytes say JPEG or PNG and it is under 8 MB; the server's word is not trusted. The 500-pixel version is used (the original if that size does not exist).
- The picture is kept in memory while the window is open and is never stored in the database.
- Covers are other people's work: the Cover Art Archive hosts what the community uploaded. Embedding is your decision, for your own library. Turn the feature off in Settings if you prefer.
- A release without a cover says so; MusicFinder never guesses or substitutes another release's cover.

## fpcalc and the AcoustID key

- **fpcalc.exe** (Chromaprint) is **not** included, because the licence of the exact binary has not been verified for redistribution. Download `chromaprint-fpcalc-1.6.2-windows-x86_64.zip` from the Chromaprint releases on GitHub
  and put `fpcalc.exe` next to `MusicFinder.exe`, in a `Tools` folder there, anywhere on `PATH`, or choose it in Settings.
- **AcoustID key**: register your copy at acoustid.org/new-application. The service is **free for non-commercial use only**; commercial use needs a separate agreement. The key is stored in `settings.json` in plain text on your PC and is never logged or committed.
- Attribution shown in Settings: *Metadata from MusicBrainz. Audio identification by the AcoustID web service.*

## Limits and politeness

MusicBrainz allows about one request per second; AcoustID three. MusicFinder keeps well below both, for everything together, and when a service answers "slow down" (HTTP 503/429) it waits (honouring `Retry-After` up to a minute),
retries up to three times, and otherwise tells you. A batch is looked up one track at a time; *Stop* ends it and keeps what was found. Without a network, only this feature is affected.

## Honest status

Everything is tested with fake services and recorded response shapes; the shapes of the MusicBrainz search, recording and release answers were checked against live responses. It has **not** yet been run end to end with a real AcoustID key
and fpcalc (those need your key and binary); see `docs/external-integrations.md` for what is verified and what is still to check.
