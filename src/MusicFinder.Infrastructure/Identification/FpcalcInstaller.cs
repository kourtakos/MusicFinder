using System.ComponentModel;
using System.IO.Compression;
using MusicFinder.Application.Identification;

namespace MusicFinder.Infrastructure.Identification;

public sealed record FpcalcImportResult(bool Ok, string? Path, string? Version, string? Error)
{
    public static FpcalcImportResult Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// Saves the user's fpcalc.exe into MusicFinder's own tools folder, from the .exe or from the zip Chromaprint's download page gives. It copies one
/// file and nothing else: the name inside the tools folder is fixed, so nothing in a zip can choose where it is written. The copy is started once,
/// with <c>-version</c>, before it replaces anything, so a wrong or broken file never replaces a working one. The audio is not touched.
/// </summary>
public sealed class FpcalcInstaller
{
    public const long MaxBytes = 150L * 1024 * 1024;
    public static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);
    private const string FileName = "fpcalc.exe";
    private const string TrialName = "fpcalc-trial.exe";

    private readonly string _toolsDirectory;
    private readonly IProcessRunner _runner;
    private readonly long _maxBytes;

    public FpcalcInstaller(string toolsDirectory, IProcessRunner runner, long maxBytes = MaxBytes)
    {
        _toolsDirectory = toolsDirectory;
        _runner = runner;
        _maxBytes = maxBytes;
    }

    public string TargetPath => Path.Combine(_toolsDirectory, FileName);

    public async Task<FpcalcImportResult> ImportAsync(string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return FpcalcImportResult.Fail("That file does not exist.");
        var trial = Path.Combine(_toolsDirectory, TrialName);
        try
        {
            Directory.CreateDirectory(_toolsDirectory);
            var copied = source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? CopyFromZip(source, trial) : CopyFromExe(source, trial);
            if (copied is not null) return FpcalcImportResult.Fail(copied);

            if (!LooksLikeAProgram(trial)) return FpcalcImportResult.Fail("That file is not a Windows program, so it cannot be fpcalc.exe.");
            var (ok, version, error) = await TryRunAsync(trial, ct);
            if (!ok) return FpcalcImportResult.Fail(error!);

            File.Move(trial, TargetPath, overwrite: true);
            return new FpcalcImportResult(true, TargetPath, version, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return FpcalcImportResult.Fail("fpcalc could not be saved: " + ex.Message);
        }
        finally
        {
            TryDelete(trial);
        }
    }

    private string? CopyFromExe(string source, string trial)
    {
        if (!string.Equals(Path.GetFileName(source), FileName, StringComparison.OrdinalIgnoreCase))
            return $"Choose the file named {FileName} (or the zip you downloaded). This one is called {Path.GetFileName(source)}.";
        if (new FileInfo(source).Length > _maxBytes) return "That file is much larger than fpcalc.exe, so it was not copied.";
        File.Copy(source, trial, overwrite: true);
        return null;
    }

    private string? CopyFromZip(string source, string trial)
    {
        using var zip = ZipFile.OpenRead(source);
        // Only the entry's own name is looked at; where it sits in the zip, or what path it claims, never reaches the file system.
        var entry = zip.Entries
            .Where(e => string.Equals(Path.GetFileName(e.FullName.Replace('/', '\\')), FileName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault();
        if (entry is null) return $"There is no {FileName} inside that zip. Is it the Windows download from the Chromaprint page?";
        if (entry.Length > _maxBytes) return "The fpcalc.exe in that zip is much larger than expected, so it was not extracted.";
        entry.ExtractToFile(trial, overwrite: true);
        return null;
    }

    private static bool LooksLikeAProgram(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
    }

    private async Task<(bool Ok, string? Version, string? Error)> TryRunAsync(string exe, CancellationToken ct)
    {
        ProcessResult result;
        try { result = await _runner.RunAsync(exe, ["-version"], VersionTimeout, ct); }
        catch (Win32Exception ex) { return (false, null, "That file could not be started: " + ex.Message); }
        if (result.TimedOut) return (false, null, "That file did not answer, so it does not look like fpcalc.");

        var version = FpcalcVersion.Parse(result.StdOut + "\n" + result.StdErr);
        if (version is null)
            return (false, null, "That program ran but did not say it is fpcalc (no version number came back). Check that it is the one from the Chromaprint page.");
        return (true, version, null);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a leftover trial file is overwritten next time */ }
    }
}
