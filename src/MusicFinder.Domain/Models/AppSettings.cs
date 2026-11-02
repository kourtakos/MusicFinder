namespace MusicFinder.Domain.Models;

public sealed class AppSettings
{
    public double WindowWidth { get; set; } = 1440;
    public double WindowHeight { get; set; } = 860;
    public bool WindowMaximized { get; set; }
    public double LeftPanelWidth { get; set; } = 240;
    public double RightPanelWidth { get; set; } = 320;
    public bool IncludeSubfolders { get; set; } = true;
    public bool GlobalSearch { get; set; }
    public int ReaderConcurrency { get; set; } = 3;
    public string Language { get; set; } = "en";
    /// <summary>
    /// Seconds of quiet after a drag before it counts as a possible play. Weak evidence only; clamped to 15 - 20.
    /// A change applies to the next drag, not to windows already open.
    /// </summary>
    public int SessionHeuristicSeconds { get; set; } = 20;
    /// <summary>Start a session automatically on the first completed drag when none is active (a banner says so).</summary>
    public bool AutoStartSessionOnDrag { get; set; } = true;
    public bool HideConfirmedInSession { get; set; }
    public bool HidePossibleInSession { get; set; }
    /// <summary>The tag editor's "keep a backup copy of each file" box. Off by default; backups live in the data folder.</summary>
    public bool KeepTagBackups { get; set; }
    /// <summary>Use cover.jpg / folder.jpg when a file has no embedded cover. Off by default.</summary>
    public bool UseFolderArtwork { get; set; }
    /// <summary>The on-disk thumbnail cache is trimmed (oldest first) when it grows past this.</summary>
    public int ArtworkCacheLimitMb { get; set; } = 200;

    /// <summary>Open the quick-search window from anywhere with <see cref="QuickSearchHotkey"/>. Works only while MusicFinder is running.</summary>
    public bool QuickSearchEnabled { get; set; } = true;
    /// <summary>Canonical shortcut text, for example "Ctrl+Alt+Space".</summary>
    public string QuickSearchHotkey { get; set; } = "Ctrl+Alt+Space";
    /// <summary>Where the user last left the quick-search window (null until it was moved).</summary>
    public double? QuickSearchLeft { get; set; }
    public double? QuickSearchTop { get; set; }

    /// <summary>Greek/Greeklish/wrong-keyboard-layout matching in every search. On by default; there is no mode to switch while typing.</summary>
    public bool SmartGreekSearch { get; set; } = true;
    /// <summary>Suggest close real words when nothing in the library starts with what was typed (bounded to three candidates).</summary>
    public bool FuzzySearch { get; set; } = true;

    /// <summary>Find Similar: tracks within this many BPM of the selected one count as a tempo match.</summary>
    public double SimilarBpmWindow { get; set; } = 8;
    /// <summary>Find Similar: also compare against double and half the tempo (70 and 140 mix well); such matches are labelled.</summary>
    public bool SimilarHalfDoubleTempo { get; set; } = true;
    /// <summary>Find Similar: leave out tracks played (even only possibly) in the last 24 hours.</summary>
    public bool SimilarHideRecentlyPlayed { get; set; }

    /// <summary>Online identification (MusicBrainz / AcoustID). Off by default and honoured only after <see cref="IdentificationConsentUtc"/> is set.</summary>
    public bool IdentificationEnabled { get; set; }
    /// <summary>When the user last accepted the explanation of exactly what is sent.</summary>
    public DateTime? IdentificationConsentUtc { get; set; }
    /// <summary>The user's AcoustID application key. Stored in settings.json as plain text, never logged and never committed.</summary>
    public string? AcoustIdApiKey { get; set; }
    /// <summary>Path of fpcalc.exe; empty means look beside the program, then on PATH.</summary>
    public string? FpcalcPath { get; set; }
    /// <summary>E-mail or URL MusicBrainz can reach the user at; sent in the User-Agent header as its rules require.</summary>
    public string? MusicBrainzContact { get; set; }
    /// <summary>Seconds of audio fingerprinted (fpcalc -length).</summary>
    public int FingerprintLengthSeconds { get; set; } = 120;
    /// <summary>Offer MusicBrainz community genres as an (unticked) suggestion.</summary>
    public bool SuggestGenresFromMusicBrainz { get; set; } = true;
    /// <summary>How MusicBrainz genres meet the genres a file already has (default: added after them, nothing lost).</summary>
    public MusicFinder.Domain.Enums.GenreApplyMode MusicBrainzGenreMode { get; set; } = MusicFinder.Domain.Enums.GenreApplyMode.Add;
    /// <summary>Offer the Cover Art Archive front cover of the chosen release in the identify window. Fetched only on request, embedded only if the user ticks it.</summary>
    public bool UseCoverArtArchive { get; set; } = true;
    /// <summary>Tick "Embed this cover" for the user when the file has no cover yet. It is still written only after the user presses Write.</summary>
    public bool EmbedCoverByDefault { get; set; } = true;

    /// <summary>Seconds of a track the BPM and key analysis listens to (the middle of the track). 30 - 240.</summary>
    public int AnalysisSeconds { get; set; } = 90;
    /// <summary>How an estimated key is written into a tag. Camelot ("8A") is the default; Open Key ("1m") and note names ("Am") are the alternatives.</summary>
    public MusicFinder.Domain.Models.KeyNotation AnalysisKeyNotation { get; set; } = MusicFinder.Domain.Models.KeyNotation.Camelot;
    /// <summary>How many tracks are analysed at once (1 or 2; the work is heavy and runs at low priority).</summary>
    public int AnalysisConcurrency { get; set; } = 1;
    /// <summary>Start the analysis window with "replace the BPM and key already in the file" ticked. Off: existing values are kept unless the user ticks it there.</summary>
    public bool AnalysisAllowOverwriteDefault { get; set; }
}
