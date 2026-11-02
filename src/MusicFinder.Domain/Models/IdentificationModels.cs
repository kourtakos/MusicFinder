namespace MusicFinder.Domain.Models;

/// <summary>How a track is looked up online. <see cref="Auto"/> picks the best one that is ready.</summary>
public enum IdentificationMethod { Auto, AcoustIdFingerprint, MusicBrainzTagSearch, ExistingRecordingId }

public enum IdentificationOutcome
{
    Found, NoMatch,
    /// <summary>Online identification is off, or the user has not yet seen what is sent.</summary>
    Disabled,
    NoApiKey, FingerprinterMissing, NoContact, NotEnoughTags,
    FingerprintFailed, Offline, RateLimited, Cancelled, Error
}

/// <summary>What a stored lookup said. Errors are never stored.</summary>
public enum LookupOutcome { Found, NoMatch }

public sealed record IdentificationRequest(long TrackId, IdentificationMethod Method = IdentificationMethod.Auto, bool ForceRefresh = false);

/// <summary>
/// One possible recording for a track. Exactly one score is set, and it is named by where it came from; the two scales
/// are different things and are never combined or compared with each other. A candidate found through the file's own
/// MusicBrainz id has no score.
/// </summary>
/// <param name="AcoustIdScore">0..1, from AcoustID's audio fingerprint match.</param>
/// <param name="MusicBrainzSearchScore">0..100, from MusicBrainz's text search.</param>
/// <param name="Evidence">Facts computed locally ("duration matches", "title matches your tag").</param>
public sealed record IdentificationCandidate(
    string RecordingId, string Title, string ArtistCredit, long? DurationMs,
    IdentificationMethod Method,
    double? AcoustIdScore, int? MusicBrainzSearchScore,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<ReleaseSummary> Releases,
    IReadOnlyList<string> GenreSuggestions,
    IReadOnlyList<string> Isrcs)
{
    /// <summary>"AcoustID score 0.97", "MusicBrainz search score 87 of 100", or the id-only note.</summary>
    public string ScoreText =>
        AcoustIdScore is { } a ? "AcoustID score " + a.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
        : MusicBrainzSearchScore is { } m ? $"MusicBrainz search score {m} of 100"
        : "From the file's own MusicBrainz id";
}

public sealed record ReleaseSummary(
    string ReleaseId, string Title, string? Date, string? Country, string? Status,
    string? ReleaseGroupId, string? PrimaryType, IReadOnlyList<string> SecondaryTypes);

public sealed record ReleaseTrack(string RecordingId, int DiscNumber, int Position, int TrackCount, string Title, string ArtistCredit);

/// <summary>One release with the tracks of each disc, used to fill album, album artist, year and track number.</summary>
public sealed record ReleaseDetails(
    string ReleaseId, string Title, string? Date, string? Country, string AlbumArtistCredit,
    IReadOnlyList<string> Labels, IReadOnlyList<ReleaseTrack> Tracks);

public sealed record IdentificationResult(
    long TrackId, IdentificationMethod MethodUsed, IdentificationOutcome Outcome,
    IReadOnlyList<IdentificationCandidate> Candidates, string? Message, bool FromCache, DateTime? CheckedUtc)
{
    public static IdentificationResult Failed(long trackId, IdentificationMethod method, IdentificationOutcome outcome, string message) =>
        new(trackId, method, outcome, [], message, false, null);
}

public sealed record FingerprinterInfo(string Path, string Source);

public sealed record FingerprintResult(string Fingerprint, int DurationSeconds, int Algorithm, int LengthSeconds);

/// <summary>What can run right now, and why not. The UI shows this instead of failing at the first click.</summary>
public sealed record IdentificationReadiness(bool Enabled, bool Consented, bool HasApiKey, bool HasContact, FingerprinterInfo? Fingerprinter)
{
    public bool CanTagSearch => Enabled && Consented && HasContact;
    public bool CanFingerprint => CanTagSearch && HasApiKey && Fingerprinter is not null;
}

/// <summary>One proposed change, shown next to the current value. The user ticks the ones to apply.</summary>
public sealed record FieldProposal(TagField Field, string? Current, string? Proposed, bool Selected, string Source);

/// <summary>A fingerprint stored for one file; valid only while the file's size and modified time are unchanged.</summary>
public sealed record CachedFingerprint(string Fingerprint, int DurationSeconds, int Algorithm, int LengthSeconds, DateTime ComputedUtc);

public sealed record CachedLookup(string RequestKey, IdentificationMethod Method, LookupOutcome Outcome, string ResultJson, DateTime CheckedUtc);

/// <summary>
/// A front cover ready to embed: the image bytes exactly as received, its type and (when it could be read) size in pixels.
/// <see cref="Hash"/> is the same SHA-1 the library uses as an artwork key, so a written cover can be recognized on read-back.
/// </summary>
public sealed record CoverImage(byte[] Data, string MimeType, int? Width, int? Height)
{
    private string? _hash;
    public string Hash => _hash ??= Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Data)).ToLowerInvariant();

    /// <summary>"500×500 JPEG, 84 KB".</summary>
    public string Description
    {
        get
        {
            var kind = MimeType.EndsWith("png", StringComparison.OrdinalIgnoreCase) ? "PNG" : "JPEG";
            var size = Data.Length < 1024 * 1024 ? $"{Math.Max(1, Data.Length / 1024)} KB" : (Data.Length / 1024.0 / 1024.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
            return Width is { } w && Height is { } h ? $"{w}×{h} {kind}, {size}" : $"{kind}, {size}";
        }
    }
}
