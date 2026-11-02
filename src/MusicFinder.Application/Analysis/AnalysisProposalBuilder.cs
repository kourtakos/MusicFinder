using System.Globalization;
using MusicFinder.Application.Similar;
using MusicFinder.Application.Tags;
using MusicFinder.Domain.Entities;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Analysis;

/// <summary>Which of the tempos the analysis found is written: its own estimate, or half or double of it (when the user knows better).</summary>
public enum TempoChoice { Estimate, Half, Double }

/// <summary>One proposed tag value from the analysis. <see cref="CanApply"/> is false while a value already in the file would be replaced.</summary>
public sealed record AnalysisFieldProposal(
    TagField Field, string? Current, string Proposed, bool Selected, bool CanApply, Confidence Level, string Source, string? Note);

public sealed record AnalysisProposals(AnalysisFieldProposal? Bpm, AnalysisFieldProposal? Key)
{
    public IEnumerable<AnalysisFieldProposal> All => new[] { Bpm, Key }.OfType<AnalysisFieldProposal>();
}

/// <summary>
/// Turns an estimate into a proposed tag value, with the rules that keep the user in charge: a value already in the file is never replaced
/// unless the user allows it, an estimate that is only a guess is never ticked for them, and the text proposed is exactly what the tag
/// writer will store (whole-number BPM, the key in the chosen notation).
/// </summary>
public static class AnalysisProposalBuilder
{
    public const string KeyCaution = "key estimates are less reliable than tempos: check before writing";

    public static AnalysisProposals Build(Track track, TrackAnalysis analysis, KeyNotation notation, bool allowOverwrite, TempoChoice tempo = TempoChoice.Estimate) =>
        new(BuildBpm(track, analysis.Bpm, allowOverwrite, tempo), BuildKey(track, analysis.Key, notation, allowOverwrite));

    private static AnalysisFieldProposal? BuildBpm(Track track, BpmEstimate? estimate, bool allowOverwrite, TempoChoice tempo)
    {
        if (estimate is null) return null;
        var bpm = estimate.Bpm * tempo switch { TempoChoice.Half => 0.5, TempoChoice.Double => 2.0, _ => 1.0 };
        if (bpm < TagChangeValidator.MinBpm || bpm > TagChangeValidator.MaxBpm) return null;       // not a tempo the tag can hold

        var rounded = (int)Math.Round(bpm, MidpointRounding.AwayFromZero);
        var proposed = rounded.ToString(CultureInfo.InvariantCulture);
        var current = track.Bpm is > 0 ? Math.Round(track.Bpm.Value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) : null;
        if (current == proposed) return null;                                                     // the file already says so

        var notes = new List<string>();
        if (Math.Abs(bpm - rounded) >= 0.05) notes.Add($"{bpm.ToString("0.0", CultureInfo.InvariantCulture)} BPM, written as the whole number {proposed}");
        if (estimate.Flags.Contains("unstable", StringComparison.Ordinal)) notes.Add("the tempo drifts through the track, so this is not reliable");
        if (estimate.Flags.Contains("octave-ambiguous", StringComparison.Ordinal)) notes.Add("half or double this tempo is just as plausible");
        if (tempo != TempoChoice.Estimate) notes.Add($"you chose {(tempo == TempoChoice.Half ? "half" : "double")} the estimate ({estimate.Bpm.ToString("0.0", CultureInfo.InvariantCulture)})");

        // Ticked for the user only when the tempo is a confident one: in a test on a real library, 9 in 10 confident tempos were within a BPM of the
        // tagged value, 8 in 10 at medium confidence. Anything less is offered, not ticked.
        var canApply = current is null || allowOverwrite;
        var selected = canApply && estimate.Level == Confidence.High && estimate.IsReliable;
        return new AnalysisFieldProposal(TagField.Bpm, current, proposed, selected, canApply, estimate.Level,
            "estimated from the audio", notes.Count == 0 ? null : string.Join("; ", notes));
    }

    private static AnalysisFieldProposal? BuildKey(Track track, KeyEstimate? estimate, KeyNotation notation, bool allowOverwrite)
    {
        if (estimate is null || !CamelotKey.TryParse(estimate.Camelot, out var key)) return null;
        var proposed = key.Format(notation);

        var current = string.IsNullOrWhiteSpace(track.MusicalKey) ? null : track.MusicalKey.Trim();
        if (current is not null && CamelotKey.TryParse(current, out var existing) && existing == key) return null;      // the file already says so, in any notation

        var notes = new List<string>();
        if (estimate.Flags.Contains("relative-ambiguous", StringComparison.Ordinal)) notes.Add("the relative major or minor fits almost as well");
        if (estimate.Runner is not null && estimate.Level < Confidence.High) notes.Add($"the next best key is {estimate.Runner}");
        if (Math.Abs(estimate.TuningCents) >= 25) notes.Add($"the recording is {estimate.TuningCents.ToString("+0;-0", CultureInfo.InvariantCulture)} cents from standard pitch");

        notes.Add(KeyCaution);
        // Never ticked for the user. Key estimation is the weakest part of the analysis: in a test on a real library the estimate matched the key
        // already tagged only about 6 times in 10 even at high confidence. A wrong key written into a file misleads harmonic mixing, so the
        // user decides, key by key or all at once.
        var canApply = current is null || allowOverwrite;
        const bool selected = false;
        return new AnalysisFieldProposal(TagField.Key, current, proposed, selected, canApply, estimate.Level,
            "estimated from the audio", notes.Count == 0 ? null : string.Join("; ", notes));
    }

    /// <summary>The tag changes for the ticked proposals only.</summary>
    public static TagChangeSet ToChangeSet(IEnumerable<AnalysisFieldProposal> proposals) =>
        new(proposals.Where(p => p.Selected && p.CanApply).ToDictionary(p => p.Field, p => (string?)p.Proposed));
}
