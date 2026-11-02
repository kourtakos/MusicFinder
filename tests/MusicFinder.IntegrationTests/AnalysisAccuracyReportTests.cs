using System.Diagnostics;
using System.Globalization;
using System.Text;
using MusicFinder.Application.Analysis;
using MusicFinder.Application.Similar;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Audio;

namespace MusicFinder.IntegrationTests;

/// <summary>
/// An opt-in report for the person who owns the music: how well do the estimates agree with the BPM and key already tagged in a folder
/// of their own files? It READS files only (tags and audio) and writes its report to the temp folder, never to the music folder.
/// <code>
/// set MF_ANALYSIS_REPORT=1
/// set MF_ANALYSIS_FOLDER=D:\Music\SomeFolder
/// set MF_ANALYSIS_MAX=150          (optional; default 150 files, taken evenly across the folder)
/// set MF_ANALYSIS_PROFILE=kk       (optional; compare with the Krumhansl-Kessler key profiles instead of Temperley)
/// dotnet test tests/MusicFinder.IntegrationTests --filter "FullyQualifiedName~AnalysisAccuracyReportTests"
/// </code>
/// The result is in %TEMP%\mf-analysis-report.txt. Agreement with a tag is only as good as the tag: DJ software and taggers make mistakes too.
/// </summary>
public class AnalysisAccuracyReportTests
{
    private static readonly string[] Extensions = [".mp3", ".flac", ".m4a", ".aac", ".wav", ".aif", ".aiff", ".ogg"];

    [Fact]
    public async Task Report()
    {
        if (Environment.GetEnvironmentVariable("MF_ANALYSIS_REPORT") != "1") return;       // not part of the normal run
        var folder = Environment.GetEnvironmentVariable("MF_ANALYSIS_FOLDER");
        Assert.False(string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder), "Set MF_ANALYSIS_FOLDER to an existing folder of music.");

        var max = int.TryParse(Environment.GetEnvironmentVariable("MF_ANALYSIS_MAX"), out var m) && m > 0 ? m : 150;
        var all = Directory.EnumerateFiles(folder!, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var step = Math.Max(1, all.Count / max);
        var files = all.Where((_, i) => i % step == 0).Take(max).ToList();

        var decoder = new AudioDecoderRouter();
        var rows = new List<Row>();
        foreach (var file in files)
        {
            var row = new Row(Path.GetExtension(file).ToLowerInvariant());
            try
            {
                using (var tag = TagLib.File.Create(file, TagLib.ReadStyle.None))
                {
                    row.TagBpm = tag.Tag.BeatsPerMinute > 0 ? tag.Tag.BeatsPerMinute : null;
                    row.TagKey = CamelotKey.TryParse(tag.Tag.InitialKey, out var k) ? k : null;
                }
                var clock = Stopwatch.StartNew();
                var decoded = await decoder.DecodeAsync(file, new AudioDecodeRequest(), default);
                if (decoded.Audio is null) { row.Failure = decoded.Failure; rows.Add(row); continue; }
                var output = AudioAnalyzer.Analyze(decoded.Audio, Environment.GetEnvironmentVariable("MF_ANALYSIS_PROFILE") switch { "kk" => KeyProfileSet.KrumhanslKessler, "temperley" => KeyProfileSet.Temperley, "combined" => KeyProfileSet.Combined, _ => KeyProfileSet.Combined });
                row.Seconds = clock.Elapsed.TotalSeconds;
                row.Bpm = output.Bpm;
                row.Key = output.Key;
            }
            catch (Exception ex) { row.Failure = ex.GetType().Name + ": " + ex.Message; }
            rows.Add(row);
        }

        var report = Build(files.Count, all.Count, rows);
        var path = Path.Combine(Path.GetTempPath(), "mf-analysis-report.txt");
        await File.WriteAllTextAsync(path, report);
        Assert.True(File.Exists(path));
    }

    private sealed class Row(string extension)
    {
        public string Extension { get; } = extension;
        public uint? TagBpm;
        public CamelotKey? TagKey;
        public BpmEstimate? Bpm;
        public KeyEstimate? Key;
        public double Seconds;
        public string? Failure;
    }

    private static string Build(int analysed, int total, List<Row> rows)
    {
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine($"MusicFinder BPM/key analysis report: {analysed} of {total} files (analyzer version {AudioAnalyzer.Version})");
        sb.AppendLine($"Could not be decoded: {rows.Count(r => r.Failure is not null)}");
        foreach (var g in rows.Where(r => r.Failure is not null).GroupBy(r => r.Failure!).OrderByDescending(g => g.Count()).Take(5))
            sb.AppendLine($"   {g.Count()}x {g.Key}");
        var ok = rows.Where(r => r.Failure is null).ToList();

        sb.AppendLine().AppendLine("== Speed (decode + analysis) ==");
        var times = ok.Select(r => r.Seconds).OrderBy(x => x).ToList();
        if (times.Count > 0)
            sb.AppendLine($"median {times[times.Count / 2]:0.00} s, 95th percentile {times[(int)(times.Count * 0.95).Clamp(0, times.Count - 1)]:0.00} s, per file; 3,700 files would take about {times[times.Count / 2] * 3700 / 60:0} minutes with one worker");
        foreach (var g in ok.GroupBy(r => r.Extension))
            sb.AppendLine($"   {g.Key}: {g.Count()} files, median {g.Select(r => r.Seconds).OrderBy(x => x).ElementAt(g.Count() / 2):0.00} s");

        sb.AppendLine().AppendLine("== BPM against the tag (files that have a BPM tag) ==");
        var withBpmTag = ok.Where(r => r.TagBpm is not null).ToList();
        sb.AppendLine($"{withBpmTag.Count} files have a BPM tag; {withBpmTag.Count(r => r.Bpm is null)} of them got no estimate");
        foreach (var level in new[] { Confidence.High, Confidence.Medium, Confidence.Low })
        {
            var group = withBpmTag.Where(r => r.Bpm?.Level == level).ToList();
            if (group.Count == 0) continue;
            var same = group.Count(r => Math.Abs(r.Bpm!.Bpm - r.TagBpm!.Value) <= 1.0);
            var octave = group.Count(r => Octave(r.Bpm!.Bpm, r.TagBpm!.Value));
            sb.AppendLine($"   {level,-6} confidence: {group.Count,4} files: within 1 BPM {Pct(same, group.Count, inv)}, half/double {Pct(octave, group.Count, inv)}, other {Pct(group.Count - same - octave, group.Count, inv)}");
        }
        var estimated = withBpmTag.Where(r => r.Bpm is not null).ToList();
        if (estimated.Count > 0)
            sb.AppendLine($"   all estimates: within 1 BPM {Pct(estimated.Count(r => Math.Abs(r.Bpm!.Bpm - r.TagBpm!.Value) <= 1.0), estimated.Count, inv)}");

        sb.AppendLine().AppendLine("== Key against the tag (files that have a key tag) ==");
        var withKeyTag = ok.Where(r => r.TagKey is not null).ToList();
        sb.AppendLine($"{withKeyTag.Count} files have a key tag; {withKeyTag.Count(r => r.Key is null)} of them got no estimate");
        foreach (var level in new[] { Confidence.High, Confidence.Medium, Confidence.Low })
        {
            var group = withKeyTag.Where(r => r.Key?.Level == level).ToList();
            if (group.Count == 0) continue;
            var cats = group.Select(r => Category(r.Key!, r.TagKey!.Value)).ToList();
            sb.AppendLine($"   {level,-6} confidence: {group.Count,4} files: exact {Pct(cats.Count(c => c == "exact"), group.Count, inv)}, relative {Pct(cats.Count(c => c == "relative"), group.Count, inv)}, " +
                          $"neighbour {Pct(cats.Count(c => c == "neighbour"), group.Count, inv)}, other {Pct(cats.Count(c => c == "other"), group.Count, inv)}");
        }

        sb.AppendLine().AppendLine("== What the analysis said overall ==");
        var drifting = ok.Count(r => r.Bpm is not null && r.Bpm.Flags.Contains("unstable"));
        sb.AppendLine($"tempo: high {ok.Count(r => r.Bpm?.Level == Confidence.High)}, medium {ok.Count(r => r.Bpm?.Level == Confidence.Medium)}, low {ok.Count(r => r.Bpm?.Level == Confidence.Low)}, none {ok.Count(r => r.Bpm is null)}, drifting {drifting}");
        sb.AppendLine($"key:   high {ok.Count(r => r.Key?.Level == Confidence.High)}, medium {ok.Count(r => r.Key?.Level == Confidence.Medium)}, low {ok.Count(r => r.Key?.Level == Confidence.Low)}, none {ok.Count(r => r.Key is null)}");
        return sb.ToString();
    }

    private static bool Octave(double estimate, double tag) =>
        Math.Abs(estimate * 2 - tag) <= 2 || Math.Abs(estimate / 2 - tag) <= 1 || Math.Abs(estimate - tag * 2) <= 2 || Math.Abs(estimate - tag / 2) <= 1;

    private static string Category(KeyEstimate estimate, CamelotKey tag)
    {
        if (!CamelotKey.TryParse(estimate.Camelot, out var key)) return "other";
        if (key == tag) return "exact";
        if (key.Number == tag.Number) return "relative";
        var step = ((key.Number - tag.Number) % 12 + 12) % 12;
        return key.IsMinor == tag.IsMinor && (step == 1 || step == 11) ? "neighbour" : "other";
    }

    private static string Pct(int part, int whole, IFormatProvider inv) => whole == 0 ? "-" : ((double)part / whole * 100).ToString("0", inv) + " %";
}

internal static class IntClampExtension
{
    public static int Clamp(this double value, int min, int max) => (int)Math.Clamp(value, min, max);
}
