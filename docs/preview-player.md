# Preview player

A discreet listen-before-you-drag player. **Double-click a track** (in the results grid or in Quick Search) and a slim strip appears at the very top of the window:
Play/Pause, Stop, a digital timer (`01:23 / 03:45`), the cover, the title and the track's details scrolling smoothly (artist, album and year, genre, BPM, key, bitrate and format),
a volume slider, the output it plays on, and a close button. A thin line under the strip shows progress; click it to jump to that point. **Space** (grid focused) pauses or resumes a loaded
preview; it never starts sound by itself. "Play preview" is also in the right-click menu.

## What it is not
- **Not a DJ play.** A preview is not a drag and is never recorded in a session or in play history.
- **Not a file operation.** Files are opened read-only and shared for reading, writing and deleting, so Traktor, VirtualDJ and Explorer are never blocked. MusicFinder's own tag writes
  and renames let go of the file first (the tag writer probes for exclusive access, so a playing file would otherwise make the write fail).
- No auto-advance, no playlist, no capture of audio.

## Which speakers? (Settings, Preview player)
Choose the output the preview uses (for example the headphone card). The default is the Windows default output, and the strip shows it in the warning colour, because during a gig that may be the PA.
If the chosen output is not connected the strip says so and stays silent; it never falls back to the default. Playback is WASAPI **shared mode**, so it does not take the sound card away from Traktor or VirtualDJ.
Volume starts at 50% and is remembered. Outputs are listed only when the Settings page opens or on Refresh, never at startup.

## Formats
MP3, M4A/AAC, FLAC, WMA (Windows Media Foundation; needs the codec in your Windows edition), WAV, AIFF, and Ogg Vorbis (managed decoder). Ogg Opus and Ogg FLAC are not supported. A file that cannot be played shows a short message in the strip (no dialog).

## Design
`IPreviewPlayer` (Domain) with a pure state machine and formatters (`Application/Preview`), `WasapiPreviewPlayer` and `PreviewFileOpener` (Infrastructure), `PreviewViewModel`, `PreviewBar` and `MarqueeText` (App).
The caption scrolls only when it does not fit and only while playing; it pauses under the pointer and is static when Windows animations are off. Closing WASAPI happens outside the player's lock (the output's thread calls back into it).
NAudio 3 marks `WasapiOut` obsolete in favour of `WasapiPlayer`; moving over is a follow-up once that API is verified on a real device.

## Verified and not verified
Tests: the state machine, time and caption text, shared-file opening (rename and write while open), Greek paths, real WASAPI playback at **volume zero** (silent) through play, pause, seek, stop and release, a missing chosen output, the double-click, Space, error and settings behaviour through the real window with a fake player, and the bar's bindings.
Not verified automatically: audible output, codec presence on other Windows editions, switching between real devices while Traktor or VirtualDJ is running, and how smooth the scrolling looks at high DPI.
