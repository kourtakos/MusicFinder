# Testing MusicFinder with Traktor (and VirtualDJ) on another PC

Purpose: find out what Traktor really does with a MusicFinder drag. Nothing in the automated tests can prove this. Every step below says what to **observe** (seen on screen)
and what MusicFinder only **infers** (a guess, never proof of playback).

## Setup (5 minutes)

1. Unzip `MusicFinder-win-x64.zip` anywhere writable (Desktop is fine; not `Program Files`). No .NET install is needed.
2. Run `MusicFinder.exe`. The `portable.flag` file makes it keep everything in the `Data` folder beside the exe, so deleting the folder removes every trace.
   If the window renders badly over Remote Desktop or a VM, start it as `MusicFinder.exe --software`.
3. Copy **10-20 test tracks** to a folder on that PC (e.g. `C:\MF-Test`), including: one with a Greek file name, one with a very long path (> 200 characters), one on a network share
   (`\\server\share\...`) if you have one. MusicFinder never changes music files unless you use the tag editor, but use copies anyway.
4. In MusicFinder: add `C:\MF-Test` as a root; wait for "Indexed". Search should find the tracks.
5. Start Traktor with a normal deck layout. Keep both windows visible side by side.

## Checklist (tick and note the result)

| # | Do | Observe (what Traktor did) | Expect in MusicFinder (inferred) |
|---|---|---|---|
| 1 | Session tab → Start session (software: Traktor) | - | Live badge on the Session tab |
| 2 | Drag one track onto **Deck A** | loaded? yes/no, error text | entry "Exported", target shown |
| 3 | Do nothing for 20 s | did it start playing by itself? | entry becomes yellow "Possibly played" |
| 4 | Drag another track to Deck B, then **type in the search box within 10 s** | loaded? | entry stays "Exported" (not "not played") |
| 5 | Select 3 tracks (Ctrl/Shift), drag onto the **Track Collection / playlist** (browser tree) | all 3 arrived? in order? | 3 Exported entries |
| 6 | Same, but drop 3 tracks onto a **deck** | which one loaded? | 3 Exported entries (Traktor may take only one) |
| 7 | Drag the Greek-named file | accepted? name shown correctly? | Exported |
| 8 | Drag the long-path file, and the UNC one | accepted? | Exported |
| 9 | Start a drag, press **Esc** | nothing happens | no entry |
| 10 | Drag the same track twice | loaded twice? | two entries |
| 11 | Right-click a track → Mark played, Mark not played, Undo | - | status and filters change at once |
| 12 | Played filter / Hide confirmed / Hide possible | - | list changes |
| 13 | Close MusicFinder with a pending entry, reopen | - | entry stays "Exported", never "Possibly played" |
| 14 | Compare the dragged file's size/date before and after | unchanged | unchanged |
| 15 | Repeat 2-3 with **VirtualDJ** if installed | as above | as above |

If Traktor refuses a drop, note whether the cursor showed "no entry" and whether dropping on a different area (deck vs. browser vs. playlist) works. That tells us which
drop effect Traktor wants (Copy or Link).

## History files for the importer (M4, later)

Do not guess formats. After one real session in Traktor, **close Traktor** and copy these (read-only, copy, don't move) so the importer can be built from real data:

- Traktor history: `Documents\Native Instruments\Traktor <version>\History\*.nml` (the folder name carries the version) and, if it exists, `...\History\*.m3u`/`.txt` exports
- `Documents\Native Instruments\Traktor <version>\collection.nml` (library; can be large and contains your paths: send only if you are fine with that)
- VirtualDJ: `Documents\VirtualDJ\History\*.m3u` / `*.vdjhistory` or database files you find there

Note the Traktor and VirtualDJ **version numbers** (Help → About) and the Windows build (`winver`).

## Report back (copy this and fill it in)

```
Windows build:
Traktor version:            VirtualDJ version:
Checklist results (number: yes/no + note):
Drop accepted on: deck / browser / playlist  (which did / did not)
Multi-file drop: all / first only / none
Greek name OK:        Long path OK:        UNC OK:
Anything odd (cursor, errors, MusicFinder behaviour):
```

Also attach the newest file from `Data\logs\` (it contains no music files, only paths of the tracks you dragged).
