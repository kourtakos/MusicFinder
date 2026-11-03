using System.ComponentModel;
using System.IO.Compression;
using MusicFinder.Infrastructure.Identification;

namespace MusicFinder.IntegrationTests;

/// <summary>Saving the user's fpcalc into the tools folder. A fake runner stands in for the program, so nothing real is started.</summary>
public sealed class FpcalcInstallerTests : IDisposable
{
    private sealed class FakeRunner : IProcessRunner
    {
        public string Output { get; set; } = "fpcalc version 1.6.2";
        public int Exit { get; set; }
        public bool TimeOut { get; set; }
        public bool CannotStart { get; set; }
        public List<(string File, string Args)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((fileName, string.Join(' ', arguments)));
            if (CannotStart) throw new Win32Exception("not a valid application");
            return Task.FromResult(new ProcessResult(Exit, Output, "", TimeOut));
        }
    }

    private static readonly byte[] Program = [(byte)'M', (byte)'Z', 0x90, 0, 3, 0, 0, 0];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mf-fpinst-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRunner _runner = new();
    private string Tools => Path.Combine(_dir, "data", "tools");

    public FpcalcInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { /* temp folder */ }
    }

    private FpcalcInstaller Installer(long maxBytes = FpcalcInstaller.MaxBytes) => new(Tools, _runner, maxBytes);

    private string WriteFile(string relative, byte[]? bytes = null)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes ?? Program);
        return path;
    }

    private string MakeZip(params (string Name, byte[] Bytes)[] entries)
    {
        var path = Path.Combine(_dir, "download-" + Guid.NewGuid().ToString("N")[..6] + ".zip");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(bytes);
        }
        return path;
    }

    private string[] ToolFiles() => Directory.Exists(Tools) ? Directory.GetFiles(Tools).Select(Path.GetFileName).OrderBy(x => x).ToArray()! : [];

    [Fact]
    public async Task AnExe_IsCopiedIntoTheToolsFolder_AfterItAnswersWithAVersion()
    {
        var source = WriteFile(@"dl\fpcalc.exe");

        var result = await Installer().ImportAsync(source, default);

        Assert.True(result.Ok);
        Assert.Equal(Path.Combine(Tools, "fpcalc.exe"), result.Path);
        Assert.Equal("1.6.2", result.Version);
        Assert.Equal(["fpcalc.exe"], ToolFiles());                      // no trial file left behind
        Assert.Equal(Program, File.ReadAllBytes(result.Path!));
        Assert.True(File.Exists(source));                               // the user's own download is not moved or deleted
        Assert.Equal("-version", Assert.Single(_runner.Calls).Args);
    }

    [Fact]
    public async Task AZipFromTheDownloadPage_GivesOnlyFpcalcExe_FromWhereverItSitsInside()
    {
        var zip = MakeZip(("chromaprint-fpcalc-1.6.2-windows-x86_64/README.txt", [1]), ("chromaprint-fpcalc-1.6.2-windows-x86_64/fpcalc.exe", Program),
            ("chromaprint-fpcalc-1.6.2-windows-x86_64/LICENSE.txt", [2]));

        var result = await Installer().ImportAsync(zip, default);

        Assert.True(result.Ok);
        Assert.Equal(["fpcalc.exe"], ToolFiles());                      // nothing else from the zip is written
    }

    [Fact]
    public async Task APathTrickInsideTheZip_CannotWriteOutsideTheToolsFolder()
    {
        var zip = MakeZip((@"..\..\..\evil\fpcalc.exe", Program));

        var result = await Installer().ImportAsync(zip, default);

        Assert.True(result.Ok);                                         // it is just a file called fpcalc.exe; the name inside the zip is ignored
        Assert.Equal(["fpcalc.exe"], ToolFiles());
        Assert.False(Directory.Exists(Path.Combine(_dir, "evil")));
        Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "evil")));
    }

    [Fact]
    public async Task AZipWithoutFpcalc_IsRefusedWithAHelpfulMessage()
    {
        var zip = MakeZip(("readme.txt", [1]), ("tool.exe", Program));

        var result = await Installer().ImportAsync(zip, default);

        Assert.False(result.Ok);
        Assert.Contains("no fpcalc.exe inside", result.Error);
        Assert.Empty(ToolFiles());
        Assert.Empty(_runner.Calls);                                    // nothing was started
    }

    [Fact]
    public async Task AnExeWithAnotherName_IsRefused_AndNeverStarted()
    {
        var source = WriteFile(@"dl\ffmpeg.exe");

        var result = await Installer().ImportAsync(source, default);

        Assert.False(result.Ok);
        Assert.Contains("named fpcalc.exe", result.Error);
        Assert.Empty(_runner.Calls);
        Assert.Empty(ToolFiles());
    }

    [Fact]
    public async Task AFileThatIsNotAProgram_IsRefused_AndNeverStarted()
    {
        var source = WriteFile(@"dl\fpcalc.exe", [(byte)'h', (byte)'i', (byte)'!']);

        var result = await Installer().ImportAsync(source, default);

        Assert.False(result.Ok);
        Assert.Contains("not a Windows program", result.Error);
        Assert.Empty(_runner.Calls);
        Assert.Empty(ToolFiles());
    }

    [Fact]
    public async Task AProgramThatDoesNotAnswerWithAVersion_IsRefused_AndLeavesNothing()
    {
        var source = WriteFile(@"dl\fpcalc.exe");
        _runner.Output = "usage: something else";

        var result = await Installer().ImportAsync(source, default);

        Assert.False(result.Ok);
        Assert.Contains("did not say it is fpcalc", result.Error);
        Assert.Empty(ToolFiles());
    }

    [Theory]
    [InlineData("cannot")]
    [InlineData("timeout")]
    public async Task AProgramThatCannotStartOrHangs_IsRefused_AndLeavesNothing(string how)
    {
        var source = WriteFile(@"dl\fpcalc.exe");
        _runner.CannotStart = how == "cannot";
        _runner.TimeOut = how == "timeout";

        var result = await Installer().ImportAsync(source, default);

        Assert.False(result.Ok);
        Assert.Empty(ToolFiles());
    }

    [Fact]
    public async Task ABrokenNewFile_NeverReplacesAWorkingOne()
    {
        var good = WriteFile(@"data\tools\fpcalc.exe", [(byte)'M', (byte)'Z', 1, 1, 1]);
        var broken = WriteFile(@"dl\fpcalc.exe");
        _runner.CannotStart = true;

        var result = await Installer().ImportAsync(broken, default);

        Assert.False(result.Ok);
        Assert.Equal([(byte)'M', (byte)'Z', 1, 1, 1], File.ReadAllBytes(good));
        Assert.Equal(["fpcalc.exe"], ToolFiles());
    }

    [Fact]
    public async Task AWorkingNewFile_ReplacesTheOldOne()
    {
        WriteFile(@"data\tools\fpcalc.exe", [(byte)'M', (byte)'Z', 1]);
        var fresh = WriteFile(@"dl\fpcalc.exe", [(byte)'M', (byte)'Z', 9, 9]);

        var result = await Installer().ImportAsync(fresh, default);

        Assert.True(result.Ok);
        Assert.Equal([(byte)'M', (byte)'Z', 9, 9], File.ReadAllBytes(Path.Combine(Tools, "fpcalc.exe")));
    }

    [Fact]
    public async Task AFileFarLargerThanFpcalc_IsNotCopied()
    {
        var source = WriteFile(@"dl\fpcalc.exe", new byte[2000]);
        var zip = MakeZip(("fpcalc.exe", new byte[2000]));

        Assert.Contains("larger", (await Installer(maxBytes: 1000).ImportAsync(source, default)).Error);
        Assert.Contains("larger", (await Installer(maxBytes: 1000).ImportAsync(zip, default)).Error);
        Assert.Empty(ToolFiles());
    }

    [Fact]
    public async Task AMissingOrDamagedSource_IsReported_NotThrown()
    {
        var damaged = Path.Combine(_dir, "bad.zip");
        File.WriteAllBytes(damaged, [1, 2, 3, 4, 5]);

        Assert.Contains("does not exist", (await Installer().ImportAsync(Path.Combine(_dir, "nope.exe"), default)).Error);
        Assert.Contains("does not exist", (await Installer().ImportAsync("", default)).Error);
        Assert.False((await Installer().ImportAsync(damaged, default)).Ok);
        Assert.Empty(ToolFiles());
    }
}
