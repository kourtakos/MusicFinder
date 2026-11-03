using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Identification;

public enum KeyCheckOutcome
{
    /// <summary>AcoustID answered the lookup without an error about the key. Zero matches is fine: the test file may simply not be known.</summary>
    Accepted,
    /// <summary>The text is not a key, or AcoustID said the key is not valid.</summary>
    Rejected,
    /// <summary>api.acoustid.org could not be reached.</summary>
    Unreachable,
    /// <summary>fpcalc is needed to make the test lookup and was not found.</summary>
    NoFingerprinter,
    /// <summary>Something else went wrong (the file, the service); the message says what. The key itself is not judged.</summary>
    Failed
}

public sealed record KeyCheckResult(KeyCheckOutcome Outcome, string Message);

/// <summary>
/// "Test the key": one real AcoustID lookup with the key and the fingerprint of one file the user picked. Only the fingerprint, the length and
/// the key are sent, never the audio. The caller must have asked the user first. The judgement rests on what AcoustID answers; how it words an
/// invalid-key answer was not seen with a real key, so a message that mentions the key or the client counts as a rejection and anything else
/// is reported as a failure without blaming the key.
/// </summary>
public sealed class AcoustIdKeyChecker
{
    private readonly IFingerprintService _fingerprints;
    private readonly IAcoustIdClient _acoustId;

    public AcoustIdKeyChecker(IFingerprintService fingerprints, IAcoustIdClient acoustId)
    {
        _fingerprints = fingerprints;
        _acoustId = acoustId;
    }

    public async Task<KeyCheckResult> CheckAsync(string key, string audioPath, CancellationToken ct)
    {
        var format = AcoustIdKeyFormat.Validate(key);
        if (!format.IsValid) return new(KeyCheckOutcome.Rejected, format.Problem!);
        if (_fingerprints.Locate() is null)
            return new(KeyCheckOutcome.NoFingerprinter, "fpcalc.exe is needed for the test, because the test looks up a real track. Add fpcalc first.");

        FingerprintResult fingerprint;
        try { fingerprint = await _fingerprints.ComputeAsync(audioPath, ct); }
        catch (FingerprintException ex) { return new(KeyCheckOutcome.Failed, "The test file could not be fingerprinted: " + ex.Message); }

        try
        {
            var matches = await _acoustId.LookupAsync(format.Key, fingerprint.Fingerprint, fingerprint.DurationSeconds, ct);
            return new(KeyCheckOutcome.Accepted, matches.Count == 0
                ? "AcoustID accepted the key. It did not recognise this particular track, which does not matter for the test."
                : "AcoustID accepted the key and recognised the track.");
        }
        catch (IdentificationHttpException ex) when (ex.Failure == IdentificationFailure.Offline)
        {
            return new(KeyCheckOutcome.Unreachable, "AcoustID could not be reached. Check the internet connection and try again.");
        }
        catch (IdentificationHttpException ex) when (ex.Failure == IdentificationFailure.RateLimited)
        {
            return new(KeyCheckOutcome.Failed, "AcoustID asked to slow down. Wait a few seconds and try again.");
        }
        catch (IdentificationHttpException ex)
        {
            return MentionsTheKey(ex.Message)
                ? new(KeyCheckOutcome.Rejected, "AcoustID did not accept the key: " + ex.Message)
                : new(KeyCheckOutcome.Failed, "AcoustID answered with an error that is not about the key: " + ex.Message);
        }
    }

    private static bool MentionsTheKey(string message) =>
        message.Contains("api key", StringComparison.OrdinalIgnoreCase) || message.Contains("client", StringComparison.OrdinalIgnoreCase)
        || message.Contains("invalid key", StringComparison.OrdinalIgnoreCase);
}
