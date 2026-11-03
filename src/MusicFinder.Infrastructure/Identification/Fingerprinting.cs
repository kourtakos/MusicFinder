using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MusicFinder.Application.Identification;
using MusicFinder.Domain.Contracts;
using MusicFinder.Domain.Models;

namespace MusicFinder.Infrastructure.Identification;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

/// <summary>Runs a program without a shell. A seam so the fingerprint code can be tested without a real fpcalc.</summary>
public interface IProcessRunner
{
    /// <exception cref="Win32Exception">The program was not found or cannot be started.</exception>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

public sealed class WindowsProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,          // no shell: nothing in a file name can be interpreted as a command
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);   // each argument is quoted by the framework, spaces and Greek letters included

        using var process = new Process { StartInfo = info };
        process.Start();
        // Both pipes are drained while the process runs, otherwise a chatty program can fill a pipe and block forever.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, "", "", TimedOut: true);
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr, TimedOut: false);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* it ended on its own */ }
    }
}

/// <summary>
/// Finds fpcalc.exe: the path in the settings, then the app's own tools folder (where the first-run setup puts it, inside the data folder, so it
/// survives upgrades and works in portable mode), then beside the program, in a Tools folder there, then on PATH.
/// </summary>
public sealed class FpcalcLocator
{
    public const string ToolsFolderName = "tools";

    private readonly ISettingsStore _settings;
    private readonly string _baseDirectory;
    private readonly string? _pathVariable;
    private readonly string? _toolsDirectory;

    public FpcalcLocator(ISettingsStore settings, string? baseDirectory = null, string? pathVariable = null, string? toolsDirectory = null)
    {
        _settings = settings;
        _baseDirectory = baseDirectory ?? AppContext.BaseDirectory;
        _pathVariable = pathVariable ?? Environment.GetEnvironmentVariable("PATH");
        _toolsDirectory = toolsDirectory;
    }

    /// <summary>The folder the setup copies fpcalc.exe into, or null when the locator was made without one.</summary>
    public string? ToolsDirectory => _toolsDirectory;

    public FingerprinterInfo? Locate()
    {
        var configured = _settings.Load().FpcalcPath?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(configured) && File.Exists(configured)) return new FingerprinterInfo(configured, "settings");

        if (!string.IsNullOrEmpty(_toolsDirectory))
        {
            var own = Path.Combine(_toolsDirectory, "fpcalc.exe");
            if (File.Exists(own)) return new FingerprinterInfo(own, "MusicFinder's tools folder");
        }

        foreach (var candidate in new[] { Path.Combine(_baseDirectory, "fpcalc.exe"), Path.Combine(_baseDirectory, "Tools", "fpcalc.exe") })
            if (File.Exists(candidate)) return new FingerprinterInfo(candidate, "beside the program");

        foreach (var dir in (_pathVariable ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), "fpcalc.exe");
                if (File.Exists(candidate)) return new FingerprinterInfo(candidate, "PATH");
            }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        return null;
    }
}

/// <summary>Chromaprint's <c>fpcalc -json -length N file</c>. The audio never leaves the machine: only the fingerprint it prints does.</summary>
public sealed class ChromaprintFingerprintService : IFingerprintService
{
    public const int DefaultAlgorithm = 2;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly FpcalcLocator _locator;
    private readonly IProcessRunner _runner;
    private readonly ISettingsStore _settings;

    public ChromaprintFingerprintService(FpcalcLocator locator, IProcessRunner runner, ISettingsStore settings)
    {
        _locator = locator;
        _runner = runner;
        _settings = settings;
    }

    public FingerprinterInfo? Locate() => _locator.Locate();

    public async Task<FingerprintResult> ComputeAsync(string path, CancellationToken ct)
    {
        var exe = _locator.Locate() ?? throw new FingerprintException("fpcalc.exe was not found.", missing: true);
        var length = Math.Clamp(_settings.Load().FingerprintLengthSeconds, 10, 600);

        ProcessResult result;
        try
        {
            // "--" ends option parsing, so a file name that starts with "-" cannot be taken for an option.
            result = await _runner.RunAsync(exe.Path, ["-json", "-length", length.ToString(CultureInfo.InvariantCulture), "--", path], Timeout, ct);
        }
        catch (Win32Exception ex)
        {
            throw new FingerprintException("fpcalc.exe could not be started: " + ex.Message, missing: true, ex);
        }

        if (result.TimedOut)
            throw new FingerprintException("fpcalc took too long on this file (is it on a slow network drive?).");
        return result.ExitCode switch
        {
            0 => Parse(result.StdOut, length),
            2 => throw new FingerprintException("fpcalc could not decode this file (an unsupported codec, a damaged file, or too short)."),
            3 => throw new FingerprintException("fpcalc stopped while reading this file."),
            _ => throw new FingerprintException("fpcalc failed (exit code " + result.ExitCode + ")" + FirstLine(result.StdErr))
        };
    }

    /// <summary>Reads <c>{"duration": 215.4, "fingerprint": "AQAD..."}</c>.</summary>
    public static FingerprintResult Parse(string json, int lengthSeconds)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fingerprint = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("fingerprint", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
            var duration = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;
            if (string.IsNullOrWhiteSpace(fingerprint) || duration < 1)
                throw new FingerprintException("fpcalc produced no fingerprint for this file.");
            return new FingerprintResult(fingerprint, (int)Math.Round(duration), DefaultAlgorithm, lengthSeconds);
        }
        catch (JsonException ex)
        {
            throw new FingerprintException("fpcalc printed something unexpected.", inner: ex);
        }
    }

    private static string FirstLine(string stderr)
    {
        var line = stderr.Split('\n', 2, StringSplitOptions.TrimEntries)[0];
        return line.Length == 0 ? "." : ": " + (line.Length > 200 ? line[..200] : line);
    }
}
