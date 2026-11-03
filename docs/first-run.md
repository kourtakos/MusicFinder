# First run: the welcome tour and song-recognition setup

The first time MusicFinder starts, a window opens next to the library (it is not modal, so the library stays usable). It can be opened again from
Settings: **Show the tour** and **Set up song recognition...**. Closing it by any route (Done, Skip, Esc, the window's close button) means it is not
shown again by itself (`AppSettings.FirstRunCompleted`).

## The tour

Six short screens: add a folder, search, listen (double-click preview), find BPM and key, find similar tracks, use it with VirtualDJ/Traktor. Each text
names only things that exist today (a test checks that the tour never mentions playlists or crates). The step list is `FirstRunViewModel.Steps`.

## Song recognition setup

Online song recognition needs two things MusicFinder does not ship:

1. **fpcalc.exe** (Chromaprint). Its licence for redistribution is not verified, so it is not bundled. The setup page opens
   https://acoustid.org/chromaprint, and accepts the downloaded **zip or the exe** by drag-and-drop onto the window, by "Choose a file...", or by one
   click when it finds the download in the user's Downloads folder. Only a file named `fpcalc.exe` is taken (from the zip, whatever its path inside).
   It is copied to `<data folder>\tools\fpcalc.exe` (so it survives updates and works in portable mode) after it has been started once with
   `-version`; a wrong or broken file never replaces a working one. The user's own download is not moved or deleted.
   Where fpcalc is looked for: the path in Settings, then `<data folder>\tools`, then beside the program, `Tools\` there, then PATH.
2. **An AcoustID application key**, free for non-commercial use, from https://acoustid.org/new-application (an account is needed). The setup page
   checks the pasted text (a web address, spaces, or odd characters are explained, not saved) and has **Test the key**: one real lookup, made only
   after the user picked a song and confirmed a dialog that says exactly what is sent (a fingerprint, the length and the key; not the audio, file name
   or folder). A refusal that mentions the key counts as "key rejected"; any other error is reported without blaming the key.
   The wording AcoustID uses for an invalid key was **not seen with a real key**; the key checker treats a message mentioning "api key", "client" or
   "invalid key" as a rejection.

The same page also holds the contact address MusicBrainz requires and the switch that turns online identification on (which opens the existing
consent window the first time). They are the same settings as in Settings > Online identification.

The key and address are left out of backups unless "include secrets" is ticked, so after a restore the setup page may be needed again.
