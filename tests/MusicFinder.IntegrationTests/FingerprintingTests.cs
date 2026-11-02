using System.ComponentModel;
using System.Diagnostics;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.IntegrationTests;

public class FingerprintingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-fp-" + Guid.NewGuid().ToString("N"));
    private readonly MusicBrainzClientTests.FakeSettings _settings = new();

    public FingerprintingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private sealed class FakeRunner : IProcessRunner
    {
        public Func<string, IReadOnlyList<string>, ProcessResult> Respond { get; set; } = (_, _) => new ProcessResult(0, "", "", false);
        public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult(Respond(fileName, arguments));
        }
    }

    private string FakeFpcalc()
    {
        var path = Path.Combine(_dir, "fpcalc.exe");
        File.WriteAllText(path, "not really an exe");
        return path;
    }

    private (ChromaprintFingerprintService Service, FakeRunner Runner) Make(string? fpcalcPath = null)
    {
        _settings.Current = new AppSettings { FpcalcPath = fpcalcPath ?? FakeFpcalc(), FingerprintLengthSeconds = 90 };
        var runner = new FakeRunner();
        var locator = new FpcalcLocator(_settings, baseDirectory: Path.Combine(_dir, "nowhere"), pathVariable: "");
        return (new ChromaprintFingerprintService(locator, runner, _settings), runner);
    }

    // ===== locating fpcalc =====

    [Fact]
    public void ThePathInTheSettings_WinsOverEverythingElse()
    {
        var beside = Path.Combine(_dir, "app");
        Directory.CreateDirectory(beside);
        File.WriteAllText(Path.Combine(beside, "fpcalc.exe"), "x");
        var configured = FakeFpcalc();
        _settings.Current = new AppSettings { FpcalcPath = "\"" + configured + "\"" };   // a quoted path pasted from Explorer

        var found = new FpcalcLocator(_settings, beside, "").Locate();

        Assert.Equal((configured, "settings"), (found!.Path, found.Source));
    }

    [Fact]
    public void ItLooksBesideTheProgram_ThenInToolsThenOnPath()
    {
        var app = Path.Combine(_dir, "app");
        var tools = Path.Combine(app, "Tools");
        var onPath = Path.Combine(_dir, "bin");
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(onPath);
        _settings.Current = new AppSettings();

        File.WriteAllText(Path.Combine(onPath, "fpcalc.exe"), "x");
        Assert.Equal("PATH", new FpcalcLocator(_settings, app, "C:\\nope;" + onPath).Locate()!.Source);

        File.WriteAllText(Path.Combine(tools, "fpcalc.exe"), "x");
        Assert.Equal(Path.Combine(tools, "fpcalc.exe"), new FpcalcLocator(_settings, app, onPath).Locate()!.Path);

        File.WriteAllText(Path.Combine(app, "fpcalc.exe"), "x");
        Assert.Equal(Path.Combine(app, "fpcalc.exe"), new FpcalcLocator(_settings, app, onPath).Locate()!.Path);
    }

    [Fact]
    public void WhenItIsNowhere_TheAnswerIsNull_EvenWithAJunkSettingAndPath()
    {
        _settings.Current = new AppSettings { FpcalcPath = Path.Combine(_dir, "gone", "fpcalc.exe") };

        Assert.Null(new FpcalcLocator(_settings, Path.Combine(_dir, "empty"), "<>|;;\"  ;" + _dir).Locate());
    }

    // ===== running it =====

    [Fact]
    public async Task ItAsksForJson_WithTheConfiguredLength_AndPassesAGreekPathAsOneArgument()
    {
        var (service, runner) = Make();
        runner.Respond = (_, _) => new ProcessResult(0, """{"duration": 215.4, "fingerprint": "AQADtEmS"}""", "", false);
        const string path = @"D:\Μουσική\Χάρις Αλεξίου\Ξανά στη ζωή μου & more.mp3";

        var result = await service.ComputeAsync(path, default);

        Assert.Equal(("AQADtEmS", 215, 2, 90), (result.Fingerprint, result.DurationSeconds, result.Algorithm, result.LengthSeconds));
        var call = Assert.Single(runner.Calls);
        Assert.Equal(["-json", "-length", "90", "--", path], call.Args);   // one argument, untouched; "--" protects against names starting with "-"
    }

    [Theory]
    [InlineData(2, "could not decode")]
    [InlineData(3, "stopped while reading")]
    [InlineData(7, "exit code 7")]
    public async Task ExitCodes_BecomeMessagesForTheUser(int code, string expected)
    {
        var (service, runner) = Make();
        runner.Respond = (_, _) => new ProcessResult(code, "", "boom: bad\nsecond line", false);

        var ex = await Assert.ThrowsAsync<FingerprintException>(() => service.ComputeAsync("a.mp3", default));

        Assert.Contains(expected, ex.Message);
        Assert.False(ex.Missing);
    }

    [Fact]
    public async Task ATimeout_IsReportedAsSuch()
    {
        var (service, runner) = Make();
        runner.Respond = (_, _) => new ProcessResult(-1, "", "", TimedOut: true);

        var ex = await Assert.ThrowsAsync<FingerprintException>(() => service.ComputeAsync("a.mp3", default));

        Assert.Contains("too long", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("""{"duration": 0, "fingerprint": "AQAD"}""")]
    [InlineData("""{"duration": 100, "fingerprint": ""}""")]
    [InlineData("""{"duration": "x", "fingerprint": "AQAD"}""")]
    [InlineData("[]")]
    public async Task OutputWithoutAUsableFingerprint_IsRejected(string stdout)
    {
        var (service, runner) = Make();
        runner.Respond = (_, _) => new ProcessResult(0, stdout, "", false);

        await Assert.ThrowsAsync<FingerprintException>(() => service.ComputeAsync("a.mp3", default));
    }

    [Fact]
    public async Task AProgramThatCannotBeStarted_IsReportedAsMissing()
    {
        var (service, runner) = Make();
        runner.Respond = (_, _) => throw new Win32Exception(193, "not a valid Win32 application");

        var ex = await Assert.ThrowsAsync<FingerprintException>(() => service.ComputeAsync("a.mp3", default));

        Assert.True(ex.Missing);
    }

    [Fact]
    public async Task WithoutFpcalc_ComputeSaysSo_AndNeverStartsAnything()
    {
        _settings.Current = new AppSettings();
        var runner = new FakeRunner();
        var service = new ChromaprintFingerprintService(new FpcalcLocator(_settings, Path.Combine(_dir, "x"), ""), runner, _settings);

        var ex = await Assert.ThrowsAsync<FingerprintException>(() => service.ComputeAsync("a.mp3", default));

        Assert.True(ex.Missing);
        Assert.Empty(runner.Calls);
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(9999, 600)]
    public async Task TheLengthSetting_IsKeptWithinSaneBounds(int configured, int used)
    {
        var (service, runner) = Make();
        _settings.Current.FingerprintLengthSeconds = configured;
        runner.Respond = (_, _) => new ProcessResult(0, """{"duration": 100, "fingerprint": "AQAD"}""", "", false);

        var result = await service.ComputeAsync("a.mp3", default);

        Assert.Equal(used, result.LengthSeconds);
    }

    // ===== the real process runner =====

    [Fact]
    public async Task TheRealRunner_CapturesOutputAndTheExitCode()
    {
        var runner = new WindowsProcessRunner();

        var ok = await runner.RunAsync("cmd.exe", ["/c", "echo", "hello"], TimeSpan.FromSeconds(20), default);
        var failed = await runner.RunAsync("cmd.exe", ["/c", "exit", "3"], TimeSpan.FromSeconds(20), default);

        Assert.Equal(0, ok.ExitCode);
        Assert.False(ok.TimedOut);
        Assert.Equal("hello", ok.StdOut.Trim());
        Assert.Equal(3, failed.ExitCode);
    }

    [Fact]
    public async Task TheRealRunner_StopsAProgramThatRunsTooLong()
    {
        var clock = Stopwatch.StartNew();

        var result = await new WindowsProcessRunner().RunAsync("ping.exe", ["-n", "30", "127.0.0.1"], TimeSpan.FromMilliseconds(700), default);

        Assert.True(result.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheRealRunner_StopsWhenCancelled_AndLeavesNoProcessBehind()
    {
        using var cts = new CancellationTokenSource();
        var before = Process.GetProcessesByName("PING").Length;
        var run = new WindowsProcessRunner().RunAsync("ping.exe", ["-n", "30", "127.0.0.1"], TimeSpan.FromSeconds(60), cts.Token);
        await Task.Delay(400);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        await Task.Delay(500);
        Assert.True(Process.GetProcessesByName("PING").Length <= before);
    }

    [Fact]
    public async Task TheRealRunner_ReportsAMissingProgramAsWin32Exception() =>
        await Assert.ThrowsAsync<Win32Exception>(() =>
            new WindowsProcessRunner().RunAsync(Path.Combine(_dir, "no-such-program.exe"), [], TimeSpan.FromSeconds(5), default));
}
