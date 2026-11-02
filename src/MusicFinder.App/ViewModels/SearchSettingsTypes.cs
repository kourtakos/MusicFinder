using MusicFinder.Domain.Models;

namespace MusicFinder.App.ViewModels;

/// <summary>One of the user's own search aliases, as a row in Settings.</summary>
public sealed class SearchAliasItem
{
    public long Id { get; }
    public string Source { get; }
    public string Alias { get; }

    public SearchAliasItem(SearchAliasOverride o)
    {
        Id = o.Id;
        Source = o.Source;
        Alias = o.Alias;
    }
}

/// <summary>Plain-text rendering of a <see cref="SearchExplanation"/>.</summary>
public static class SearchExplanationFormatter
{
    private static readonly string[] TierNames =
        ["exactly as typed", "normalized (case, accents)", "other keyboard layout", "phonetic key (Greek/Greeklish)", "typo correction"];

    public static string Format(SearchExplanation x)
    {
        var sb = new System.Text.StringBuilder();
        if (x.Terms.Count == 0)
        {
            sb.AppendLine("Nothing searchable in this text.");
            foreach (var w in x.Warnings) sb.AppendLine("Warning: " + w);
            return sb.ToString().TrimEnd();
        }

        foreach (var t in x.Terms)
        {
            sb.AppendLine((t.Field is null ? "" : t.Field + ": ") + (t.IsPhrase ? "phrase" : "word"));
            foreach (var f in t.Forms) sb.AppendLine($"    {f.Kind,-34} {f.Value}");
        }
        foreach (var w in x.Warnings) sb.AppendLine("Warning: " + w);

        sb.AppendLine();
        if (x.Results.Count == 0)
        {
            sb.Append("No results.");
        }
        else
        {
            sb.AppendLine(x.Results.Count == x.ResultLimit ? $"First {x.ResultLimit} results, best first:" : $"{x.Results.Count} results, best first:");
            foreach (var r in x.Results)
            {
                var tier = TierNames[Math.Clamp(r.Tier, 0, TierNames.Length - 1)];
                var artist = r.Artist is { Length: > 0 } a ? " — " + a : "";
                var where = r.InTitleArtistAlbum ? "" : "   (path or genre only)";
                sb.AppendLine($"    {tier,-34} {r.Title}{artist}{where}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
