MusicFinder {VERSION} - self-contained, no install needed (Windows 10/11 x64)

1. Unzip this folder anywhere you can write to (not "Program Files"), then run MusicFinder.exe.
   If the window looks blank or broken in a VM or Remote Desktop, run:  MusicFinder.exe --software
2. In Settings, add the folders that contain your music. Indexing runs in the background.
3. Everything MusicFinder stores (library index, settings, logs, backups) is in the Data folder next to the exe.
   Delete the folder to remove all traces. Delete portable.flag to keep that data in %LOCALAPPDATA%\MusicFinder instead.
4. Your music files are changed only when you press Write in the tag editor, or confirm a rename/move after its preview.
   Renaming or moving files breaks cue points and playlists in Traktor and VirtualDJ, which track files by path.
5. Settings -> Backup and restore saves your index and history (not your music). Make one before upgrading.

Third-party licences: THIRD-PARTY-NOTICES.md
