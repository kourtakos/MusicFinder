# BPM and key analysis

MusicFinder can listen to your tracks and **estimate** their tempo and key, entirely on your computer (no network, nothing uploaded). The result is an estimate: shown apart from your tags, with a confidence,
and **written to a file only when you press Write**.

## Using it

- Select tracks and press **Ctrl+B** (or right-click → *Analyze BPM and key…*, or the button in the details panel).
- **Analyze missing BPM and key…** (in the Library box on the left) takes the tracks of the selected folder, or the whole library, that have no BPM or no key.
- The window lists the tracks as they are analysed (in the background, at low priority, one or two at a time; *Stop* ends it). For each track you see what the tags hold now, the estimate with its
  confidence (high, medium, low), and notes ("the next best key is 11B", "half or double this tempo is just as plausible", "the tempo drifts").
- **Tempos** that were estimated with *high* confidence are ticked for you, only where the tag is empty. **Keys are never ticked for you** (see the accuracy section). *Tick all tempos* and *Tick all keys* are
  your decision for the whole list; a checkbox changes one track.
- **½ and ×2** switch a track to half or double the estimated tempo: tempo estimation often lands an octave off (70 or 140 for 140 or 70), and the other octave is always one click away.
- A BPM or key a file **already has is kept**, unless you tick *Replace a BPM or key the file already has* (Settings can start the window with it ticked).
- The line *Will write 12 BPM values and 4 keys to 14 files* is the preview. *Write* uses the same safe path as the tag editor (a verified temporary copy replaces the original; a failure leaves the file
  untouched; an optional backup copy goes to MusicFinder's data folder). Afterwards the library, the grid and Find Similar see the new tags.
- Estimates are remembered while a file is unchanged (writing tags does not count as a change), so listening again is instant. *Analyze again* forgets them for the tracks in the window; Settings has *Forget estimates*.

## What is written

- **BPM**: a whole number (the tag holds integers), so an estimate of 123.5 is written as 124; the window says so.
- **Key**: in the notation you choose in Settings: Camelot (`8A`, the default), Open Key (`1m`) or a note name (`Am`). **Which of these Traktor and VirtualDJ read from a tag has not been verified**: forum reports
  suggest VirtualDJ reads the key tag and that Traktor may prefer Open Key or note names. Try one track in your DJ software before analysing a library, and pick the notation it understands.

## How accurate is it?

Honestly: **tempo is good, key is only fair**. Measured on 150 files taken evenly across the author's own library (mostly MP3 pop and dance; read-only), compared with the BPM and key already in the files' tags
(which other software wrote, and which can be wrong themselves):

| | Files | Agreement with the tag |
|---|---|---|
| Tempo, *high* confidence | 36 | **92 %** within 1 BPM |
| Tempo, *medium* confidence | 25 | 80 % within 1 BPM, 12 % half or double |
| Tempo, *low* confidence | 23 | 70 % within 1 BPM |
| Key, *high* confidence | 21 | **62 %** the same key, 5 % its relative major/minor, 10 % an adjacent key on the wheel, 24 % other |
| Key, *medium* confidence | 17 | 41 % the same key |
| Key, *low* confidence | 12 | 8 % the same key |

So a confident tempo is usually right, a medium one is worth a look, and a key is a suggestion to check: that is why keys are never ticked for you. The confidence labels do carry information (high is better than medium,
medium better than low, for keys and tempos alike). It takes about **1.2 seconds per track** with one worker (decoding included): around 75 minutes for 3,700 tracks, about half that with two.

Known weak spots, shown as notes or as *no estimate* rather than hidden:

- **A tempo that moves** (live recordings, much folk and laïko without a fixed grid): flagged *drifts* and never ticked.
- **Half or double**: the other octave is the commonest tempo mistake. Flagged *octave-ambiguous* when equally plausible.
- **Keys of non-Western music** (modes and dromoi in Greek folk, laïko, rebetiko), music that modulates, and tracks with little harmony: weak or wrong keys, or no estimate.
- **Very short, silent or noisy files**: *no estimate*, with the reason.

You can measure it on your own music: `docs/audio-analysis.md` ends with how to run the opt-in report (it only reads files).

## What it listens to, and what it cannot read

By default the middle 90 seconds of each track (Settings: 30-240), skipping a silent intro and outro; the clip is mixed to mono at 22,050 Hz.
Decoding uses Windows Media Foundation for MP3, AAC/M4A (without DRM) and FLAC (FLAC works on Windows 10 and later; checked on a Windows 11 machine), a built-in reader for WAV and AIFF, and a managed
decoder for Ogg Vorbis. **Not readable**: Ogg Opus, Ogg FLAC, DRM-protected M4A, WMA, damaged files, and any codec missing from your Windows edition (the "N" editions lack the Media Feature Pack). Such a file
shows *Cannot be read* with the reason and the rest go on.

## How it works (short)

Tempo: spectral flux of 40 log-spaced bands gives an onset signal; its autocorrelation, reinforced at two and four beats and weighted by a soft preference for 70-180 BPM, gives the tempo; the beat period is then
refined against the whole clip, and the clip is checked in parts for drift. Key: peaks of the spectrum between 55 and 1,760 Hz are located precisely, the clip's tuning is estimated, and the resulting pitch-class
profile is compared with the Krumhansl-Kessler and Temperley key profiles (the average of both is used: it separated confident from doubtful answers best on real music). The confidence is how far the best answer
stands above the best unrelated one. All of it is plain C# (`src/MusicFinder.Application/Analysis`), tested on synthetic rhythms and chord progressions of known tempo and key, in all 24 keys, out of tune,
with noise, silence and tempo ramps. Bump `AudioAnalyzer.Version` when the maths changes: remembered estimates are then made again.

## Run the accuracy report on your own library

It reads tags and audio from a folder and writes a report to your temp folder; it never writes to the folder.

```
set MF_ANALYSIS_REPORT=1
set MF_ANALYSIS_FOLDER=D:\Music\SomeFolder
set MF_ANALYSIS_MAX=150
dotnet test tests/MusicFinder.IntegrationTests --filter "FullyQualifiedName~AnalysisAccuracyReportTests"
```

The report is `%TEMP%\mf-analysis-report.txt`: decode failures, speed, and the agreement tables above for your files. `MF_ANALYSIS_PROFILE` (`temperley`, `kk`, `combined`) compares key profiles.
