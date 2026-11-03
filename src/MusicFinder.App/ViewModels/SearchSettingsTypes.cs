using MusicFinder.App.Localization;
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
    private static string[] TierNames =>
        [Loc.T("exactly as typed"), Loc.T("normalized (case, accents)"), Loc.T("other keyboard layout"), Loc.T("phonetic key (Greek/Greeklish)"), Loc.T("typo correction")];

    public static string Format(SearchExplanation x)
    {
        var sb = new System.Text.StringBuilder();
        if (x.Terms.Count == 0)
        {
            sb.AppendLine(Loc.T("Nothing searchable in this text."));
            foreach (var w in x.Warnings) sb.AppendLine(Loc.T("Warning: {0}", w));
            return sb.ToString().TrimEnd();
        }

        foreach (var t in x.Terms)
        {
            sb.AppendLine((t.Field is null ? "" : t.Field + ": ") + (t.IsPhrase ? Loc.T("phrase") : Loc.T("word")));
            foreach (var f in t.Forms) sb.AppendLine($"    {Loc.T(f.Kind),-34} {f.Value}");
        }
        foreach (var w in x.Warnings) sb.AppendLine(Loc.T("Warning: {0}", w));

        sb.AppendLine();
        if (x.Results.Count == 0)
        {
            sb.Append(Loc.T("No results."));
        }
        else
        {
            sb.AppendLine(x.Results.Count == x.ResultLimit ? Loc.T("First {0} results, best first:", x.ResultLimit) : Loc.T("{0} results, best first:", x.Results.Count));
            foreach (var r in x.Results)
            {
                var tier = TierNames[Math.Clamp(r.Tier, 0, TierNames.Length - 1)];
                var artist = r.Artist is { Length: > 0 } a ? " — " + a : "";
                var where = r.InTitleArtistAlbum ? "" : "   " + Loc.T("(path or genre only)");
                sb.AppendLine($"    {tier,-34} {r.Title}{artist}{where}");
            }
        }
        return sb.ToString().TrimEnd();
    }
}
