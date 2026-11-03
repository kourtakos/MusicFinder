using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.UnitTests;

public class AcoustIdKeyCheckerTests
{
    private sealed class FakeFingerprints : IFingerprintService
    {
        public FingerprinterInfo? Found { get; set; } = new(@"C:\tools\fpcalc.exe", "test");
        public Exception? Throws { get; set; }
        public int Computed;
        public FingerprinterInfo? Locate() => Found;

        public Task<FingerprintResult> ComputeAsync(string path, CancellationToken ct)
        {
            Computed++;
            if (Throws is not null) throw Throws;
            return Task.FromResult(new FingerprintResult("AQAAfingerprint", 215, 2, 120));
        }
    }

    private sealed class FakeAcoustId : IAcoustIdClient
    {
        public Exception? Throws { get; set; }
        public int Matches { get; set; } = 1;
        public List<(string Key, string Fingerprint, int Duration)> Calls { get; } = [];

        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(string apiKey, string fingerprint, int durationSeconds, CancellationToken ct)
        {
            Calls.Add((apiKey, fingerprint, durationSeconds));
            if (Throws is not null) throw Throws;
            return Task.FromResult<IReadOnlyList<AcoustIdMatch>>(Enumerable.Range(0, Matches).Select(i => new AcoustIdMatch("id" + i, 0.9, ["rec" + i])).ToList());
        }
    }

    private readonly FakeFingerprints _fp = new();
    private readonly FakeAcoustId _api = new();

    private Task<KeyCheckResult> CheckAsync(string key = "abcd1234") => new AcoustIdKeyChecker(_fp, _api).CheckAsync(key, @"C:\Music\a.mp3", default);

    [Fact]
    public async Task AKeyThatAcoustIdAnswersTo_IsAccepted_AndOnlyTheFingerprintLeaves()
    {
        var result = await CheckAsync("  abcd1234 ");

        Assert.Equal(KeyCheckOutcome.Accepted, result.Outcome);
        var call = Assert.Single(_api.Calls);
        Assert.Equal(("abcd1234", "AQAAfingerprint", 215), call);        // the cleaned key, the fingerprint and the length: no path, no audio
    }

    [Fact]
    public async Task NoMatches_IsStillAcceptance_BecauseTheKeyWasNotRefused()
    {
        _api.Matches = 0;

        var result = await CheckAsync();

        Assert.Equal(KeyCheckOutcome.Accepted, result.Outcome);
        Assert.Contains("did not recognise this particular track", result.Message);
    }

    [Fact]
    public async Task ABadlyShapedKey_IsRejectedWithoutAnyWork()
    {
        var result = await CheckAsync("https://acoustid.org/new-application");

        Assert.Equal(KeyCheckOutcome.Rejected, result.Outcome);
        Assert.Contains("web address", result.Message);
        Assert.Equal(0, _fp.Computed);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task WithoutFpcalc_NothingIsSent_AndTheUserIsToldWhy()
    {
        _fp.Found = null;

        var result = await CheckAsync();

        Assert.Equal(KeyCheckOutcome.NoFingerprinter, result.Outcome);
        Assert.Empty(_api.Calls);
    }

    [Fact]
    public async Task AFileThatCannotBeFingerprinted_IsAFailure_NotAVerdictOnTheKey()
    {
        _fp.Throws = new FingerprintException("fpcalc could not decode this file.");

        var result = await CheckAsync();

        Assert.Equal(KeyCheckOutcome.Failed, result.Outcome);
        Assert.Contains("could not decode", result.Message);
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData("AcoustID: invalid API key")]
    [InlineData("AcoustID: invalid client")]
    [InlineData("AcoustID: Invalid key")]
    public async Task AnAnswerThatBlamesTheKey_IsARejection(string message)
    {
        _api.Throws = new IdentificationHttpException(IdentificationFailure.ServerError, 400, message);

        var result = await CheckAsync();

        Assert.Equal(KeyCheckOutcome.Rejected, result.Outcome);
        Assert.Contains(message, result.Message);
    }

    [Fact]
    public async Task AnErrorThatIsNotAboutTheKey_DoesNotBlameTheKey()
    {
        _api.Throws = new IdentificationHttpException(IdentificationFailure.ServerError, 400, "AcoustID: invalid fingerprint");

        var result = await CheckAsync();

        Assert.Equal(KeyCheckOutcome.Failed, result.Outcome);
        Assert.Contains("not about the key", result.Message);
    }

    [Fact]
    public async Task Offline_IsReportedAsUnreachable()
    {
        _api.Throws = new IdentificationHttpException(IdentificationFailure.Offline, null, "offline");

        Assert.Equal(KeyCheckOutcome.Unreachable, (await CheckAsync()).Outcome);
    }

    [Fact]
    public async Task RateLimited_AsksToWait_AndDoesNotRejectTheKey()
    {
        _api.Throws = new IdentificationHttpException(IdentificationFailure.RateLimited, 429, "slow down");

        var result = await CheckAsync();

        Assert.Equal(KeyCheckOutcome.Failed, result.Outcome);
        Assert.Contains("slow down", result.Message);
    }

    [Fact]
    public async Task ACancelledTest_Propagates()
    {
        using var cts = new CancellationTokenSource();
        _api.Throws = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => new AcoustIdKeyChecker(_fp, _api).CheckAsync("abcd1234", @"C:\a.mp3", cts.Token));
    }
}
