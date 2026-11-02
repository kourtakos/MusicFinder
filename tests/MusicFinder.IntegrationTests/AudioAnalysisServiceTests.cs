using Dapper;
using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Analysis;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;
using MusicFinder.UnitTests.Analysis;

namespace MusicFinder.IntegrationTests;

public class AudioAnalysisServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A decoder that hands back synthesized music (a 128 BPM beat over chords in A minor) and counts how often it was asked.</summary>
    private sealed class FakeDecoder : IAudioDecoder
    {
        public int Calls { get; private set; }
        public string? FailWith { get; set; }
        public Func<float[]>? Audio { get; set; }
        public List<AudioDecodeRequest> Requests { get; } = [];

        public Task<DecodeResult> DecodeAsync(string path, AudioDecodeRequest request, CancellationToken ct)
        {
            Calls++;
            Requests.Add(request);
            ct.ThrowIfCancellationRequested();
            if (FailWith is not null) return Task.FromResult(DecodeResult.Fail(FailWith));
            var mono = Audio?.Invoke() ?? Music();
            return Task.FromResult(DecodeResult.Ok(new DecodedAudio(mono, 22050, 200, 44100, 2, "fake")));
        }

        public static float[] Music()
        {
            var drums = SignalFixtures.Drums(128, 60);
            var chords = SignalFixtures.Chords(9, true, 60);
            return drums.Zip(chords, (a, b) => a + b).ToArray();
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public TestDb Db { get; } = new();
        public FakeTimeProvider Time { get; } = new(Start);
        public FakeDecoder Decoder { get; } = new();
        public MusicBrainzClientTests.FakeSettings Settings { get; } = new();
        public AudioAnalysisService Service { get; }

        public Rig() => Service = new AudioAnalysisService(Db.Tracks, Db.Analysis, Decoder, Settings, Time);

        public async Task<long> AddTrackAsync(string name = "a.mp3", long? durationMs = 240_000, bool missing = false, double? bpm = null, string? key = null)
        {
            var root = await Db.Roots.AddAsync(@"C:\Music\" + Guid.NewGuid().ToString("N")[..6]);
            var folder = await Db.Folders.UpsertAsync(root.Id, null, root.Path, "Music");
            var track = TestDb.MakeTrack(folder, Path.Combine(root.Path, name), "T", "A");
            track.DurationMs = durationMs;
            track.Missing = missing;
            track.Bpm = bpm;
            track.MusicalKey = key;
            await Db.Tracks.UpsertBatchAsync([track]);
            using var conn = Db.Factory.Open();
            return conn.ExecuteScalar<long>("SELECT Id FROM Tracks WHERE FullPath = @p", new { p = track.FullPath });
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task ATrack_IsAnalysed_WithTheTempoAndKeyOfItsMusic_AndRemembered()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.AnalyzeAsync(id, false, default);

        Assert.Equal(AnalysisOutcome.Analyzed, result.Outcome);
        var a = result.Analysis!;
        Assert.InRange(a.Bpm!.Bpm, 126.5, 129.5);
        Assert.Equal("8A", a.Key!.Camelot);                                   // A minor
        Assert.Equal((AudioAnalyzer.Version, "fake"), (a.AnalyzerVersion, a.Decoder));
        Assert.Equal(Start.UtcDateTime, a.AnalyzedUtc);
        Assert.Equal(1, await rig.Service.CountAsync(default));
        Assert.Equal(a.Bpm.Bpm, (await rig.Service.GetRememberedAsync(id, default))!.Bpm!.Bpm);
    }

    [Fact]
    public async Task AskingAgain_ForAnUnchangedFile_DoesNotDecodeAgain_UnlessForced()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        await rig.Service.AnalyzeAsync(id, false, default);

        var again = await rig.Service.AnalyzeAsync(id, false, default);
        Assert.Equal((AnalysisOutcome.FromCache, 1), (again.Outcome, rig.Decoder.Calls));
        Assert.NotNull(again.Analysis!.Bpm);

        var forced = await rig.Service.AnalyzeAsync(id, true, default);
        Assert.Equal((AnalysisOutcome.Analyzed, 2), (forced.Outcome, rig.Decoder.Calls));
    }

    [Fact]
    public async Task AChangedFile_IsAnalysedAgain()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        await rig.Service.AnalyzeAsync(id, false, default);

        await rig.Db.Writes.EnqueueAsync((c, t) => c.Execute("UPDATE Tracks SET FileSize = FileSize + 1 WHERE Id = @id", new { id }, t));
        var result = await rig.Service.AnalyzeAsync(id, false, default);

        Assert.Equal((AnalysisOutcome.Analyzed, 2), (result.Outcome, rig.Decoder.Calls));
    }

    [Fact]
    public async Task AfterATagWrite_TheAnalysisFollowsTheFile_SoItIsNotRepeated()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        await rig.Service.AnalyzeAsync(id, false, default);
        var track = (await rig.Db.Tracks.GetAsync(id))!;

        // What the tag editor does after writing: new size and time stored on the track, then the analysis follows.
        var newModified = track.ModifiedUtc.AddMinutes(3);
        await rig.Db.Writes.EnqueueAsync((c, t) => c.Execute("UPDATE Tracks SET FileSize = 9999, ModifiedUtc = @m WHERE Id = @id", new { id, m = MusicFinder.Infrastructure.Persistence.DapperConfig.ToDb(newModified) }, t));
        await rig.Service.RebindAfterWriteAsync(id, 9999, newModified, default);

        var result = await rig.Service.AnalyzeAsync(id, false, default);
        Assert.Equal((AnalysisOutcome.FromCache, 1), (result.Outcome, rig.Decoder.Calls));
    }

    [Fact]
    public async Task ASilentTrack_IsStoredAsNoEstimate_WithItsReason_AndNotRepeated()
    {
        await using var rig = new Rig();
        rig.Decoder.Audio = () => SignalFixtures.Silence(60);
        var id = await rig.AddTrackAsync();

        var first = await rig.Service.AnalyzeAsync(id, false, default);
        var second = await rig.Service.AnalyzeAsync(id, false, default);

        Assert.Equal(AnalysisOutcome.Analyzed, first.Outcome);
        Assert.Null(first.Analysis!.Bpm);
        Assert.Null(first.Analysis.Key);
        Assert.Contains("silent", first.Message);
        Assert.Equal(AnalysisOutcome.FromCache, second.Outcome);
        Assert.Equal(1, rig.Decoder.Calls);
    }

    [Fact]
    public async Task AFileThatCannotBeDecoded_IsAMessage_AndNotRemembered()
    {
        await using var rig = new Rig();
        rig.Decoder.FailWith = "Windows could not decode this .flac file.";
        var id = await rig.AddTrackAsync();

        var result = await rig.Service.AnalyzeAsync(id, false, default);

        Assert.Equal(AnalysisOutcome.DecodeFailed, result.Outcome);
        Assert.Equal("Windows could not decode this .flac file.", result.Message);
        Assert.Equal(0, await rig.Service.CountAsync(default));
    }

    [Fact]
    public async Task ATrackKnownToBeShort_IsNotEvenDecoded()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync(durationMs: 6_000);

        var result = await rig.Service.AnalyzeAsync(id, false, default);

        Assert.Equal(AnalysisOutcome.TooShort, result.Outcome);
        Assert.Equal(0, rig.Decoder.Calls);
    }

    [Fact]
    public async Task AMissingTrack_AndAnUnknownId_AreMessages_NeverDecoded()
    {
        await using var rig = new Rig();
        var gone = await rig.AddTrackAsync(missing: true);

        Assert.Equal(AnalysisOutcome.DecodeFailed, (await rig.Service.AnalyzeAsync(gone, false, default)).Outcome);
        Assert.Equal(AnalysisOutcome.NotFound, (await rig.Service.AnalyzeAsync(9999, false, default)).Outcome);
        Assert.Equal(0, rig.Decoder.Calls);
    }

    [Fact]
    public async Task TheListenedSeconds_FollowTheSetting_WithinSaneBounds()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();

        rig.Settings.Current = new AppSettings { AnalysisSeconds = 60 };
        await rig.Service.AnalyzeAsync(id, true, default);
        rig.Settings.Current = new AppSettings { AnalysisSeconds = 5 };
        await rig.Service.AnalyzeAsync(id, true, default);
        rig.Settings.Current = new AppSettings { AnalysisSeconds = 9000 };
        await rig.Service.AnalyzeAsync(id, true, default);

        Assert.Equal([60, 30, 240], rig.Decoder.Requests.Select(r => r.MaxSeconds));
        Assert.All(rig.Decoder.Requests, r => Assert.True(r.CentreWindow));
    }

    [Fact]
    public async Task Cancelling_Throws_AndStoresNothing()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Service.AnalyzeAsync(id, false, cts.Token));
        Assert.Equal(0, await rig.Service.CountAsync(default));
    }

    [Fact]
    public async Task TheTracksToAnalyse_AreThoseWithoutABpmOrAKey()
    {
        await using var rig = new Rig();
        var none = await rig.AddTrackAsync("none.mp3");
        await rig.AddTrackAsync("both.mp3", bpm: 128, key: "8A");
        var onlyBpm = await rig.AddTrackAsync("bpm.mp3", bpm: 120);

        var found = await rig.Service.FindTracksNeedingAnalysisAsync(new AnalysisScope(), default);

        Assert.Equal(new[] { none, onlyBpm }.Order(), found);
    }

    [Fact]
    public async Task Clear_ForgetsEverything()
    {
        await using var rig = new Rig();
        var id = await rig.AddTrackAsync();
        await rig.Service.AnalyzeAsync(id, false, default);

        await rig.Service.ClearAsync(default);

        Assert.Equal(0, await rig.Service.CountAsync(default));
        Assert.Null(await rig.Service.GetRememberedAsync(id, default));
    }
}
