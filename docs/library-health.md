# Library Health

The **Health** tab shows what is missing or suspect in the library. The numbers come from what MusicFinder already knows about each file (its tags and properties as they were read when the file was indexed). Opening the tab reads **no music file**,
and nothing on this tab changes a file.

## Using it

- Each card is one finding with its count. **Show in Library** lists exactly those tracks across the whole library (it is the same list as typing the search shown below into the Library box).
- From that list the usual actions apply to a selection: edit tags (F2), identify online (Ctrl+I), analyze BPM and key (Ctrl+B), open in Explorer (Ctrl+O). Counts are recounted when you return to the Health tab, so they follow tag edits with no rescan.
- Selecting a track shows an **Audio** line (codec, sample rate, channels, bit depth) and, when something looks off, a **Check** line in the details panel.

## The findings

| Card | Library search | Meaning |
|---|---|---|
| Files not found | (none) | Known to the library but not found at the last scan. A disconnected drive is not a deletion; nothing is removed. |
| Not checked yet | (none) | Indexed by an older version; the next scan reads the file again and fills in the newer facts. |
| No title / artist / album / genre / year | `missing:title` … | The tag is empty (blank or spaces only counts as empty). |
| No BPM / No key | `missing:bpm`, `missing:key` | A BPM of 0 or a blank key counts as missing. BPM and key are never looked up online. |
| No embedded cover | `missing:art` (or `missing:cover`) | Only covers embedded in the file are counted. |
| Garbled text | `issue:mojibake` | See below. |
| Tags unreadable | `issue:error` | TagLib could not read the file. It is still indexed by name; the reason is in the details panel. |
| Low bitrate | `issue:lowbitrate` | A lossy file under 128 kbps. |
| Unusual sample rate | `issue:samplerate` | Not one of 8000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 88200, 96000, 176400, 192000 Hz. |
| Codec differs from extension | `issue:mismatch` | For example FLAC audio inside a `.mp3`. An unknown codec is never a mismatch. |
| Size does not fit the bitrate | `issue:size` | A lossy file under 85 % or over 160 % of what its bitrate and length imply; smaller may mean cut short. Lossless files are exempt. |
| No duration | `issue:duration` | No length could be read. |

`issue:any` lists every track with any of the quality or tag-text findings. More filters: `bitrate:320`, `bitrate:0-127`, `codec:flac` (matches part of the codec name).

Quality findings are **suspicions to look at, never proof**. A low bitrate says nothing about where a recording came from, and MusicFinder never calls a file "fake 320" from its bitrate. The rules live in `QualityRules`
(`src/MusicFinder.Application/Health`); when a rule changes its version is bumped and `Track.CurrentMetaVersion` makes the next scan recompute the hints.

## Garbled text (mojibake)

Text that was UTF-8 but got read as a single-byte code page turns Greek into strings of accented Latin letters. MusicFinder flags a tag as garbled only if a **round trip proves it**: the text is turned back into bytes with Latin-1,
Windows-1252 or Windows-1253, and those bytes must be valid UTF-8 that is shorter than the original. Real Greek ("Χάρις Αλεξίου") never passes, and neither do genuine accented words (Über, Café, Łódź, İstanbul), because their
bytes are not valid UTF-8. A tag containing U+FFFD (the replacement character) is flagged too. Known gap: a run of `?` is **not** flagged, since real titles contain question marks. Nothing is repaired automatically.

## Not supported

Audio-like files the scanner does not index (`.wma`, `.ape`, `.opus`, `.mp4`, `.m4p`, `.aif`, `.wv`, `.mpc`, `.dsf`, `.ac3`, `.mid`) are counted per extension at each scan and listed at the top of the tab.

## What is not here yet

Duplicate candidates, and the log of file operations, arrive with the next M6 steps. Whether a file is "missing", "not scanned", "unreadable" or "not supported" are separate cards on purpose, never one empty number.
