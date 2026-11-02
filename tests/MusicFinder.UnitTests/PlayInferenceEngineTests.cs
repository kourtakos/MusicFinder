using Microsoft.Extensions.Time.Testing;
using MusicFinder.Application.Dj;
using MusicFinder.Domain.Enums;

namespace MusicFinder.UnitTests;

/// <summary>The 20-second heuristic, tested deterministically with a fake clock (no sleeping).</summary>
public class PlayInferenceEngineTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
    private static readonly DateTime T0 = new(2026, 11, 1, 21, 0, 0, DateTimeKind.Utc);

    private static (PlayInferenceEngine Engine, FakeTimeProvider Clock) Create()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(T0));
        return (new PlayInferenceEngine(clock), clock);
    }

    [Fact]
    public void NothingPending_MeansNoDeadline_AndNothingElapses()
    {
        var (engine, clock) = Create();
        Assert.Null(engine.TimeToNextDeadline);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(engine.Elapsed());
    }

    [Fact]
    public void AWindow_DoesNotElapseBeforeItsFullLength_AndDoesAtExactlyThatLength()
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, Window);

        clock.Advance(TimeSpan.FromSeconds(19.9));
        Assert.Empty(engine.Elapsed());
        Assert.Single(engine.Pending);

        clock.Advance(TimeSpan.FromSeconds(0.1));
        var done = Assert.Single(engine.Elapsed());
        Assert.Equal((1L, 100L, T0), (done.AnchorEventId, done.TrackId, done.ExportedUtc)); // stamped with the export time, not the elapse time
        Assert.Empty(engine.Pending);
        Assert.Empty(engine.Elapsed()); // resolved once only
    }

    [Fact]
    public void ActivityBeforeTheDeadline_InterruptsEveryOpenWindow_NeverTurningThemIntoPlays()
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, Window);
        engine.Open(2, 200, T0, Window);
        clock.Advance(TimeSpan.FromSeconds(7));

        Assert.Empty(engine.Elapsed());
        var interrupted = engine.Interrupt(UserActivity.Search);

        Assert.Equal([1L, 2L], interrupted.Select(p => p.AnchorEventId).Order());
        Assert.Empty(engine.Pending);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(engine.Elapsed()); // an interrupted export never becomes Possible later
    }

    [Fact]
    public void AWindowThatAlreadyRanItsFullLength_IsResolvedAsPossible_EvenIfTheTimerHadNotTickedYet()
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, Window);
        clock.Advance(TimeSpan.FromSeconds(25)); // timer late, then the user types

        // The service always asks Elapsed() before Interrupt(); this is why that order matters.
        var possible = engine.Elapsed();
        var interrupted = engine.Interrupt(UserActivity.Search);

        Assert.Single(possible);
        Assert.Empty(interrupted);
    }

    [Fact]
    public void WindowsAreIndependent_ALaterOneIsNotAffectedByAnEarlierOneElapsing()
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, Window);
        clock.Advance(TimeSpan.FromSeconds(10));
        engine.Open(2, 200, T0.AddSeconds(10), Window);

        clock.Advance(TimeSpan.FromSeconds(10)); // first: 20 s, second: 10 s
        Assert.Equal([1L], engine.Elapsed().Select(p => p.AnchorEventId));
        Assert.Equal([2L], engine.Pending.Select(p => p.AnchorEventId));

        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal([2L], engine.Elapsed().Select(p => p.AnchorEventId));
    }

    [Fact]
    public void TheSameTrackExportedTwice_GetsTwoWindows_AndTwoPlays()
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, Window);
        engine.Open(2, 100, T0, Window); // same track, second drag
        clock.Advance(Window);

        var done = engine.Elapsed();
        Assert.Equal(2, done.Count);
        Assert.All(done, p => Assert.Equal(100, p.TrackId));
        Assert.NotEqual(done[0].AnchorEventId, done[1].AnchorEventId);
    }

    [Fact]
    public void ADragOfSeveralTracks_OpensOneWindowPerTrack()
    {
        var (engine, clock) = Create();
        for (var i = 0; i < 3; i++) engine.Open(i + 1, 100 + i, T0, Window);
        clock.Advance(Window);
        Assert.Equal([100L, 101L, 102L], engine.Elapsed().Select(p => p.TrackId).Order());
    }

    [Fact]
    public void Cancel_RemovesOnlyThatWindow()
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, Window);
        engine.Open(2, 200, T0, Window);

        Assert.True(engine.Cancel(1));
        Assert.False(engine.Cancel(1));
        Assert.False(engine.Cancel(999));
        clock.Advance(Window);

        Assert.Equal([2L], engine.Elapsed().Select(p => p.AnchorEventId));
    }

    [Fact]
    public void TimeToNextDeadline_IsTheEarliestRemainingWindow_AndNeverNegative()
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, TimeSpan.FromSeconds(20));
        clock.Advance(TimeSpan.FromSeconds(8));
        engine.Open(2, 200, T0, TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(12), engine.TimeToNextDeadline); // first has 12 s left, second 15 s

        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.Zero, engine.TimeToNextDeadline);            // overdue: fire now, do not go negative
    }

    [Theory]
    [InlineData(15)]
    [InlineData(18)]
    [InlineData(20)]
    public void TheConfiguredWindowIsHonoured(int seconds)
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, TimeSpan.FromSeconds(seconds));

        clock.Advance(TimeSpan.FromSeconds(seconds - 1));
        Assert.Empty(engine.Elapsed());
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(engine.Elapsed());
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(5, 15)]
    [InlineData(14, 15)]
    [InlineData(15, 15)]
    [InlineData(17, 17)]
    [InlineData(20, 20)]
    [InlineData(21, 20)]
    [InlineData(600, 20)]
    [InlineData(-3, 15)]
    public void TheWindowIsClampedToTheAllowedRange(int configured, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PlayInferenceEngine.ClampWindow(configured));

        var (engine, clock) = Create();
        engine.Open(1, 100, T0, TimeSpan.FromSeconds(configured));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), engine.Pending[0].Window);
    }

    [Fact]
    public void TheWindowIsCapturedWhenItOpens_SoLaterSettingChangesOnlyAffectLaterDrags()
    {
        var (engine, clock) = Create();
        engine.Open(1, 100, T0, TimeSpan.FromSeconds(20));
        engine.Open(2, 200, T0, TimeSpan.FromSeconds(15)); // the user lowered the setting between drags

        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal([2L], engine.Elapsed().Select(p => p.AnchorEventId));
        Assert.Equal([1L], engine.Pending.Select(p => p.AnchorEventId));
    }

    /// <summary>A clock whose wall time can jump independently of monotonic time (FakeTimeProvider moves both together).</summary>
    private sealed class JumpyClock : TimeProvider
    {
        private long _ticks;
        private DateTimeOffset _utc = new(T0);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => _utc;
        public void Advance(TimeSpan by) { _ticks += by.Ticks; _utc += by; }
        public void JumpWallClock(TimeSpan by) => _utc += by; // system time changed; real time did not pass
    }

    [Fact]
    public void ChangingTheWallClock_DoesNotShortenOrSkipAWindow()
    {
        var clock = new JumpyClock();
        var engine = new PlayInferenceEngine(clock);
        engine.Open(1, 100, T0, Window);

        clock.JumpWallClock(TimeSpan.FromHours(5));   // user changes the system time / DST jump
        Assert.Empty(engine.Elapsed());               // no window was shortened by it
        clock.JumpWallClock(TimeSpan.FromHours(-9));  // ...or lengthened by going backwards
        clock.Advance(TimeSpan.FromSeconds(19));
        Assert.Empty(engine.Elapsed());

        clock.Advance(TimeSpan.FromSeconds(1));       // real elapsed time reaches 20 s
        Assert.Single(engine.Elapsed());
    }

    [Fact]
    public void AppClosing_ResolvesPendingWindowsAsInterrupted()
    {
        var (engine, _) = Create();
        engine.Open(1, 100, T0, Window);
        engine.Open(2, 200, T0, Window);

        var interrupted = engine.Interrupt(UserActivity.AppClosed);

        Assert.Equal(2, interrupted.Count);
        Assert.Empty(engine.Pending);
    }

    [Fact]
    public async Task ConcurrentUse_NeverLosesOrDuplicatesAWindow()
    {
        var (engine, clock) = Create();
        var resolved = new System.Collections.Concurrent.ConcurrentBag<long>();

        var openers = Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++) engine.Open(t * 1000 + i, t, T0, Window);
        })).ToArray();
        await Task.WhenAll(openers);

        clock.Advance(Window);
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            foreach (var p in engine.Elapsed()) resolved.Add(p.AnchorEventId);
        })).ToArray();
        await Task.WhenAll(readers);

        Assert.Equal(1600, resolved.Count);
        Assert.Equal(1600, resolved.Distinct().Count()); // each window resolved exactly once
        Assert.Empty(engine.Pending);
    }
}
