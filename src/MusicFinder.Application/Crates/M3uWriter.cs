using System.Text;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Crates;

/// <summary>Writes a crate as an extended M3U8 playlist (UTF-8, absolute paths). It only lists files; it never touches them.</summary>
public static class M3uWriter
{
    /// <summary>The playlist text and how many tracks were left out because their file is marked missing.</summary>
    public static (string Text, int Skipped) Build(IReadOnlyList<SearchResultRow> rows)
    {
        var sb = new StringBuilder("#EXTM3U\r\n");
        var skipped = 0;
        foreach (var r in rows)
        {
            if (r.Missing) { skipped++; continue; }
            var seconds = r.DurationMs is > 0 ? (long)Math.Round(r.DurationMs.Value / 1000.0) : -1;
            var name = string.IsNullOrWhiteSpace(r.Artist) ? r.Title : r.Artist + " - " + r.Title;
            sb.Append("#EXTINF:").Append(seconds).Append(',').Append(OneLine(name)).Append("\r\n");
            sb.Append(r.FullPath).Append("\r\n");
        }
        return (sb.ToString(), skipped);
    }

    public static void Write(string path, IReadOnlyList<SearchResultRow> rows, out int skipped)
    {
        var (text, skip) = Build(rows);
        skipped = skip;
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string OneLine(string s) => s.Replace("\r", " ").Replace("\n", " ").Trim();
}
