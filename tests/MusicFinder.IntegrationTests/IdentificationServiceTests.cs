using System.Net;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.IntegrationTests;

public class IdentificationServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string RecA = "5f2c9a0e-7b1d-4c3a-9e84-0d6b2a1f7c33";
    private const string RecB = "11111111-1111-1111-1111-111111111111";

    // ===== fakes: no network, no fpcalc =====

    private sealed class FakeFingerprints : IFingerprintService
    {
        public bool Present { get; set; } = true;
        public int Computed { get; private set; }
        public Exception? Fails { get; set; }
        public FingerprinterInfo? Locate() => Present ? new FingerprinterInfo(@"C:\Tools\fpcalc.exe", "settings") : null;
        public Task<FingerprintResult> ComputeAsync(string path, CancellationToken ct)
        {
            Computed++;
            if (Fails is not null) throw Fails;
            return Task.FromResult(new FingerprintResult("FP-" + Path.GetFileName(path), 215, 2, 120));
        }
    }

    private sealed class FakeAcoustId : IAcoustIdClient
    {
        public List<(string Key, string Fingerprint, int Duration)> Calls { get; } = [];
        public List<AcoustIdMatch> Matches { get; set; } = [new("aid-1", 0.97, [RecA]), new("aid-2", 0.55, [RecB])];
        public Exception? Fails { get; set; }
        public Task<IReadOnlyList<AcoustIdMatch>> LookupAsync(string apiKey, string fingerprint, int durationSeconds, CancellationToken ct)
        {
            Calls.Add((apiKey, fingerprint, durationSeconds));
            return Fails is not null ? throw Fails : Task.FromResult<IReadOnlyList<AcoustIdMatch>>(Matches);
        }
    }

    private sealed class FakeMusicBrainz : IMusicBrainzClient
    {
        public Dictionary<string, MbRecording> Recordings { get; } = new()
        {
            [RecA] = new(RecA, "Ξανά στη ζωή μου", "Χάρις Αλεξίου", 225_000,
                [new ReleaseSummary("rel-1", "Το Τραγούδι", "1985", "GR", "Official", "rg-1", "Album", [])], ["laiko"], []),
            [RecB] = new(RecB, "Other Song", "Someone", 180_000, [], [], [])
        };
        public List<string> RecordingCalls { get; } = [];
        public List<string> Searches { get; } = [];
        public List<MbSearchHit> SearchHits { get; set; } = [];
        public Exception? Fails { get; set; }

        public Task<MbRecording?> GetRecordingAsync(string recordingId, CancellationToken ct)
        {
            RecordingCalls.Add(recordingId);
            if (Fails is not null) throw Fails;
            return Task.FromResult(Recordings.GetValueOrDefault(recordingId));
        }

        public Task<MbRelease?> GetReleaseAsync(string releaseId, CancellationToken ct) =>
            Task.FromResult<MbRelease?>(new MbRelease(releaseId, "Το Τραγούδι", "1985-03-02", "GR", "Χάρις Αλεξίου", ["Minos"],
                [new ReleaseTrack(RecA, 1, 5, 9, "Ξανά στη ζωή μου", "Χάρις Αλεξίου")]));

        public Task<IReadOnlyList<MbSearchHit>> SearchRecordingsAsync(string luceneQuery, int limit, CancellationToken ct)
        {
            Searches.Add(luceneQuery);
            return Fails is not null ? throw Fails : Task.FromResult<IReadOnlyList<MbSearchHit>>(SearchHits);
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public FakeTimeProvider Time { get; } = new(Start);
        public FakeFingerprints Fingerprints { get; } = new();
        public FakeAcoustId AcoustId { get; } = new();
        public FakeMusicBrainz Mb { get; } = new();
        public MusicBrainzClientTests.FakeSettings Settings { get; } = new();
        public IdentificationService Service { get; }

        public Rig(Action<AppSettings>? configure = null)
        {
            Settings.Current = new AppSettings
            {
                IdentificationEnabled = true, IdentificationConsentUtc = Start.UtcDateTime, MusicBrainzContact = "dj@example.com",
                AcoustIdApiKey = "KEY123", FingerprintLengthSeconds = 120
            };
            configure?.Invoke(Settings.Current);
            Mb.SearchHits = [new MbSearchHit(RecA, 94, Mb.Recordings[RecA]), new MbSearchHit(RecB, 70, Mb.Recordings[RecB])];
            Service = new IdentificationService(Db.Tracks, Db.Identification, Fingerprints, AcoustId, Mb, Settings, Time);
        }

        public async Task<long> AddTrackAsync(string name = "Xanà.mp3", string? title = "Ξανά στη ζωή μου", string? artist = "Χάρις Αλεξίου",
            string? mbid = null, long? durationMs = 224_000)
        {
            var root = await Db.Roots.AddAsync(@"C:\Music\" + Guid.NewGuid().ToString("N")[..6]);
            var folder = await Db.Folders.UpsertAsync(root.Id, null, root.Path, "Music");
            var track = TestDb.MakeTrack(folder, Path.Combine(root.Path, name), title, artist);
            track.DurationMs = durationMs;
            track.MusicBrainzRecordingId = mbid;
            await Db.Tracks.UpsertBatchAsync([track]);
            using var conn = Db.Factory.Open();
            return conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FullPath = @p", new { p = track.FullPath });
        }

        public int Calls => AcoustId.Calls.Count + Mb.RecordingCalls.Count + Mb.Searches.Count + Fingerprints.Computed;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    // ===== switched off / not ready =====

    [Theory]
    [InlineData(false, true)]    // off
    [InlineData(true, false)]    // on, but never saw what is sent
    public async Task WithoutConsent_NothingHappens_AndNothingIsAsked(bool enabled, bool consented)
    {
        await using var rig = new Rig(s => { s.IdentificationEnabled = enabled; s.IdentificationConsentUtc = consented ? Start.UtcDateTime : null; });
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(IdentificationOutcome.Disabled, result.Outcome);
        Assert.Contains("Settings", result.Message);
        Assert.Equal(0, rig.Calls);
        Assert.Empty(await rig.Service.IdentifyAsync(id, default));
        Assert.Null(await rig.Service.GetReleaseDetailsAsync("rel-1", default));
    }

    [Fact]
    public async Task WithoutAContact_MusicBrainzIsNeverCalled()
    {
        await using var rig = new Rig(s => s.MusicBrainzContact = " ");
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(IdentificationOutcome.NoContact, result.Outcome);
        Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task Readiness_SaysWhatIsMissing()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);
        rig.Fingerprints.Present = false;

        var r = rig.Service.GetReadiness();

        Assert.True(r.CanTagSearch);
        Assert.False(r.CanFingerprint);
        Assert.False(r.HasApiKey);
        Assert.Null(r.Fingerprinter);
    }

    [Fact]
    public async Task AnUnknownTrack_IsAnError_NotACrash()
    {
        await using var rig = new Rig();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(999), default);

        Assert.Equal(IdentificationOutcome.Error, result.Outcome);
        Assert.Equal(0, rig.Calls);
    }

    // ===== search by tags =====

    [Fact]
    public async Task TagSearch_ReturnsScoredCandidates_WithEvidence_BestFirst()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);   // no key: Auto falls to the tag search
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal((IdentificationOutcome.Found, IdentificationMethod.MusicBrainzTagSearch, false), (result.Outcome, result.MethodUsed, result.FromCache));
        Assert.Equal([RecA, RecB], result.Candidates.Select(c => c.RecordingId));
        var best = result.Candidates[0];
        Assert.Equal((94, (double?)null), (best.MusicBrainzSearchScore, best.AcoustIdScore));
        Assert.Equal("MusicBrainz search score 94 of 100", best.ScoreText);
        Assert.Contains("duration matches (3:44)", best.Evidence);
        Assert.Contains("title matches your tag", best.Evidence);
        Assert.Contains("artist matches your tag", best.Evidence);
        Assert.Contains("title differs from your tag", result.Candidates[1].Evidence);
        Assert.Equal("recording:\"Ξανά στη ζωή μου\" AND artist:\"Χάρις Αλεξίου\"", Assert.Single(rig.Mb.Searches));
    }

    [Fact]
    public async Task ARepeatedSearch_IsAnsweredFromTheCache_UntilForced_OrExpired()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);
        var id = await rig.AddTrackAsync();
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        var again = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);
        Assert.True(again.FromCache);
        Assert.Equal(2, again.Candidates.Count);
        Assert.Equal("Ξανά στη ζωή μου", again.Candidates[0].Title);                    // survived the round trip through JSON
        Assert.Equal("Το Τραγούδι", again.Candidates[0].Releases[0].Title);
        Assert.Single(rig.Mb.Searches);

        await rig.Service.IdentifyAsync(new IdentificationRequest(id, ForceRefresh: true), default);
        Assert.Equal(2, rig.Mb.Searches.Count);

        rig.Time.Advance(IdentificationService.FoundLifetime + TimeSpan.FromDays(1));
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);
        Assert.Equal(3, rig.Mb.Searches.Count);
    }

    [Fact]
    public async Task NoMatch_IsRemembered_ButOnlyForAWeek()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);
        rig.Mb.SearchHits = [];
        var id = await rig.AddTrackAsync();

        var first = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);
        Assert.Equal(IdentificationOutcome.NoMatch, first.Outcome);
        Assert.Contains("edit the tags by hand", first.Message);

        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);
        Assert.Single(rig.Mb.Searches);

        rig.Time.Advance(IdentificationService.NoMatchLifetime + TimeSpan.FromHours(1));
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);
        Assert.Equal(2, rig.Mb.Searches.Count);
    }

    [Fact]
    public async Task ATrackWithNothingToSearchFor_SaysSo()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);
        var id = await rig.AddTrackAsync(name: ".mp3", title: null, artist: null);

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id, IdentificationMethod.MusicBrainzTagSearch), default);

        Assert.Equal(IdentificationOutcome.NotEnoughTags, result.Outcome);
        Assert.Empty(rig.Mb.Searches);
    }

    [Fact]
    public async Task AFileNamedArtistDashTitle_IsSearchedByThatName_AndSaysSo()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);
        var id = await rig.AddTrackAsync(name: "Chic - Le Freak.mp3", title: null, artist: null);
        rig.Mb.Recordings[RecB] = rig.Mb.Recordings[RecB] with { Title = "Le Freak", ArtistCredit = "Chic" };
        rig.Mb.SearchHits = [new MbSearchHit(RecB, 90, rig.Mb.Recordings[RecB])];

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal("recording:\"Le Freak\" AND artist:\"Chic\"", Assert.Single(rig.Mb.Searches));
        Assert.Contains("title matches your file name", result.Candidates[0].Evidence);
    }

    // ===== fingerprint =====

    [Fact]
    public async Task Fingerprint_Auto_UsesAcoustId_ThenMusicBrainzRecordings_WithAcoustIdScores()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal((IdentificationOutcome.Found, IdentificationMethod.AcoustIdFingerprint), (result.Outcome, result.MethodUsed));
        Assert.Equal([RecA, RecB], result.Candidates.Select(c => c.RecordingId));
        Assert.Equal([0.97, 0.55], result.Candidates.Select(c => c.AcoustIdScore!.Value));
        Assert.All(result.Candidates, c => Assert.Null(c.MusicBrainzSearchScore));
        Assert.Equal("AcoustID score 0.97", result.Candidates[0].ScoreText);
        var call = Assert.Single(rig.AcoustId.Calls);
        Assert.Equal(("KEY123", 215), (call.Key, call.Duration));
        Assert.StartsWith("FP-", call.Fingerprint);
        Assert.Empty(rig.Mb.Searches);
    }

    [Fact]
    public async Task TheFingerprintAndTheLookup_AreReused_ByAFileThatHasNotChanged()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        var again = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.True(again.FromCache);
        Assert.Equal(1, rig.Fingerprints.Computed);
        Assert.Single(rig.AcoustId.Calls);
        Assert.Equal(2, again.Candidates.Count);
    }

    [Fact]
    public async Task AChangedFile_IsFingerprintedAgain()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        await rig.Db.Writes.EnqueueAsync((c, t) => c.Execute("UPDATE Tracks SET FileSize = FileSize + 1 WHERE Id = @id", new { id }, t));
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(2, rig.Fingerprints.Computed);
        Assert.Single(rig.AcoustId.Calls);   // same fingerprint text in this fake, so the question itself was already answered
    }

    [Fact]
    public async Task TwoCopiesOfTheSameAudio_ShareOneAcoustIdQuestion()
    {
        await using var rig = new Rig();
        var a = await rig.AddTrackAsync("same.mp3");
        var b = await rig.AddTrackAsync("same.mp3");   // different folder, identical fake fingerprint

        await rig.Service.IdentifyAsync(new IdentificationRequest(a), default);
        var second = await rig.Service.IdentifyAsync(new IdentificationRequest(b), default);

        Assert.Single(rig.AcoustId.Calls);
        Assert.True(second.FromCache);
    }

    [Fact]
    public async Task ChangingTheFingerprintLength_ForcesANewFingerprint()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        rig.Settings.Current.FingerprintLengthSeconds = 60;
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(2, rig.Fingerprints.Computed);
    }

    [Fact]
    public async Task AskingForAFingerprint_WithoutAKey_OrWithoutFpcalc_ExplainsAndSendsNothing()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        var request = new IdentificationRequest(id, IdentificationMethod.AcoustIdFingerprint);

        rig.Settings.Current.AcoustIdApiKey = null;
        var noKey = await rig.Service.IdentifyAsync(request, default);
        rig.Settings.Current.AcoustIdApiKey = "KEY123";
        rig.Fingerprints.Present = false;
        var noTool = await rig.Service.IdentifyAsync(request, default);

        Assert.Equal(IdentificationOutcome.NoApiKey, noKey.Outcome);
        Assert.Contains("search by tags", noKey.Message);
        Assert.Equal(IdentificationOutcome.FingerprinterMissing, noTool.Outcome);
        Assert.Contains("fpcalc.exe", noTool.Message);
        Assert.Equal(0, rig.Calls);
    }

    [Fact]
    public async Task AFileFpcalcCannotRead_IsReported_AndNothingIsAskedOrRemembered()
    {
        await using var rig = new Rig();
        rig.Fingerprints.Fails = new FingerprintException("fpcalc could not decode this file.");
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(IdentificationOutcome.FingerprintFailed, result.Outcome);
        Assert.Equal("fpcalc could not decode this file.", result.Message);
        Assert.Empty(rig.AcoustId.Calls);
        Assert.Equal(0, await rig.Db.Identification.CountAsync(default));
    }

    [Fact]
    public async Task AFingerprintAcoustIdDoesNotKnow_IsNoMatch_AndRemembered()
    {
        await using var rig = new Rig();
        rig.AcoustId.Matches = [];
        var id = await rig.AddTrackAsync();

        var first = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(IdentificationOutcome.NoMatch, first.Outcome);
        Assert.Contains("AcoustID does not know", first.Message);
        Assert.Single(rig.AcoustId.Calls);
    }

    [Fact]
    public async Task AMatchMusicBrainzCannotShow_IsNotACandidate()
    {
        await using var rig = new Rig();
        rig.AcoustId.Matches = [new("aid", 0.9, ["99999999-9999-9999-9999-999999999999"])];
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(IdentificationOutcome.NoMatch, result.Outcome);
        Assert.Contains("no usable record", result.Message);
    }

    [Fact]
    public async Task AtMostFiveCandidates_BestScoresFirst_WithTheBestScorePerRecording()
    {
        await using var rig = new Rig();
        var ids = Enumerable.Range(1, 8).Select(i => $"00000000-0000-0000-0000-00000000000{i}").ToList();
        foreach (var rid in ids) rig.Mb.Recordings[rid] = new MbRecording(rid, "T" + rid[^1], "A", null, [], [], []);
        rig.AcoustId.Matches = ids.Select((rid, i) => new AcoustIdMatch("a" + i, 0.5 + i * 0.05, [rid])).Append(new AcoustIdMatch("dup", 0.1, [ids[7]])).ToList();
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(5, result.Candidates.Count);
        Assert.Equal(ids.AsEnumerable().Reverse().Take(5), result.Candidates.Select(c => c.RecordingId));
        Assert.Equal(0.85, result.Candidates[0].AcoustIdScore!.Value, 3);       // not the duplicate's 0.1
    }

    // ===== the id already in the file =====

    [Fact]
    public async Task AnIdInTheFile_IsLookedUpDirectly_WithoutAScore()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);
        var id = await rig.AddTrackAsync(mbid: RecA);

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal((IdentificationOutcome.Found, IdentificationMethod.ExistingRecordingId), (result.Outcome, result.MethodUsed));
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("From the file's own MusicBrainz id", candidate.ScoreText);
        Assert.Contains("already tagged with this id", candidate.Evidence);
        Assert.Empty(rig.Mb.Searches);
        Assert.Empty(rig.AcoustId.Calls);
    }

    [Fact]
    public async Task AskingForTheFilesIdWhenItHasNone_IsExplained()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id, IdentificationMethod.ExistingRecordingId), default);

        Assert.Equal(IdentificationOutcome.NotEnoughTags, result.Outcome);
        Assert.Contains("no MusicBrainz id", result.Message);
    }

    // ===== failures =====

    [Theory]
    [InlineData(IdentificationFailure.Offline, IdentificationOutcome.Offline, "Could not reach")]
    [InlineData(IdentificationFailure.RateLimited, IdentificationOutcome.RateLimited, "slow down")]
    [InlineData(IdentificationFailure.ServerError, IdentificationOutcome.Error, "AcoustID: invalid API key")]
    public async Task NetworkFailures_BecomeOutcomes_AndAreNeverRemembered(IdentificationFailure failure, IdentificationOutcome outcome, string text)
    {
        await using var rig = new Rig();
        rig.AcoustId.Fails = new IdentificationHttpException(failure, null, "AcoustID: invalid API key");
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);

        Assert.Equal(outcome, result.Outcome);
        Assert.Contains(text, result.Message);
        Assert.Empty(result.Candidates);
        // The fingerprint is worth keeping; the failed question is not.
        using var conn = rig.Db.Factory.Open();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM IdentificationLookups"));
    }

    [Fact]
    public async Task Cancelling_IsReportedAsCancelled_NotAsAnError()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);
        var id = await rig.AddTrackAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await rig.Service.IdentifyAsync(new IdentificationRequest(id), cts.Token);

        Assert.Equal(IdentificationOutcome.Cancelled, result.Outcome);
        Assert.Equal(0, rig.Calls);
    }

    // ===== releases =====

    [Fact]
    public async Task AReleaseChoice_ReturnsItsDetails()
    {
        await using var rig = new Rig();

        var release = await rig.Service.GetReleaseDetailsAsync("rel-1", default);

        Assert.Equal(("Το Τραγούδι", "1985-03-02", "Χάρις Αλεξίου"), (release!.Title, release.Date, release.AlbumArtistCredit));
        Assert.Equal(5, release.Tracks.Single().Position);
    }

    [Fact]
    public async Task ClearingTheCache_ForgetsEverything()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        await rig.Service.IdentifyAsync(new IdentificationRequest(id), default);
        Assert.True(await rig.Db.Identification.CountAsync(default) > 0);

        await rig.Service.ClearCacheAsync(default);

        Assert.Equal(0, await rig.Db.Identification.CountAsync(default));
    }

    // ===== genres for hits that came from a search =====

    [Fact]
    public async Task ASearchHit_GetsItsGenres_FromTheRecordingsOwnRecord_OnRequest_AndOnlyOnce()
    {
        await using var rig = new Rig(s => s.AcoustIdApiKey = null);
        var id = await rig.AddTrackAsync();
        rig.Mb.SearchHits = [new MbSearchHit(RecA, 94, rig.Mb.Recordings[RecA] with { Genres = [], GenreLevel = null })];   // as a real search answer: no genres
        var hit = (await rig.Service.IdentifyAsync(new IdentificationRequest(id), default)).Candidates[0];
        Assert.Empty(hit.GenreSuggestions);                                       // a search answer carries none
        rig.Mb.Recordings[RecA] = rig.Mb.Recordings[RecA] with { Genres = ["laiko", "pop"], GenreLevel = "artist", Isrcs = ["GRA018500123"] };
        var before = rig.Mb.RecordingCalls.Count;

        var enriched = await rig.Service.EnrichAsync(hit, default);

        Assert.Equal(["laiko", "pop"], enriched.GenreSuggestions);
        Assert.Equal("artist", enriched.GenreLevel);
        Assert.Equal(["GRA018500123"], enriched.Isrcs);
        Assert.Equal(hit.Releases, enriched.Releases);                            // what the hit already had is kept
        Assert.Equal(before + 1, rig.Mb.RecordingCalls.Count);
    }

    [Fact]
    public async Task ACandidateThatAlreadyHasGenres_IsNotLookedUpAgain()
    {
        await using var rig = new Rig();
        var candidate = new IdentificationCandidate(RecA, "T", "A", null, IdentificationMethod.AcoustIdFingerprint, 0.9, null, [], [], ["disco"], []);

        var result = await rig.Service.EnrichAsync(candidate, default);

        Assert.Same(candidate, result);
        Assert.Empty(rig.Mb.RecordingCalls);
    }

    [Fact]
    public async Task WithoutAgreement_NothingIsLookedUpForGenres()
    {
        await using var rig = new Rig(s => s.IdentificationEnabled = false);
        var candidate = new IdentificationCandidate(RecA, "T", "A", null, IdentificationMethod.MusicBrainzTagSearch, null, 90, [], [], [], []);

        var result = await rig.Service.EnrichAsync(candidate, default);

        Assert.Same(candidate, result);
        Assert.Empty(rig.Mb.RecordingCalls);
    }

    [Fact]
    public async Task WhenGenresCannotBeFetched_TheCandidateIsReturnedAsItWas()
    {
        await using var rig = new Rig();
        rig.Mb.Fails = new IdentificationHttpException(IdentificationFailure.Offline, null, "offline");
        var candidate = new IdentificationCandidate(RecB, "T", "A", null, IdentificationMethod.MusicBrainzTagSearch, null, 90, [], [], [], []);

        var result = await rig.Service.EnrichAsync(candidate, default);

        Assert.Same(candidate, result);
    }

    [Fact]
    public async Task ARecordingWithNoGenresAtAll_StaysWithout()
    {
        await using var rig = new Rig();
        var candidate = new IdentificationCandidate(RecB, "Other Song", "Someone", null, IdentificationMethod.MusicBrainzTagSearch, null, 90, [], [], [], []);

        var result = await rig.Service.EnrichAsync(candidate, default);

        Assert.Empty(result.GenreSuggestions);
    }
}
