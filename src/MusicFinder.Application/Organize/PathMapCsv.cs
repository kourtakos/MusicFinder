using System.Text;
using MusicFinder.Domain.Models;

namespace MusicFinder.Application.Organize;

/// <summary>The old-to-new path list of a batch, for DJ software that keys its library by file path. UTF-8 with a BOM so Excel reads Greek.</summary>
public static class PathMapCsv
{
    public static void Write(string path, IEnumerable<PathMapEntry> entries)
    {
        var sb = new StringBuilder("OldPath,NewPath\r\n");
        foreach (var e in entries) sb.Append(Quote(e.OldPath)).Append(',').Append(Quote(e.NewPath)).Append("\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static string Quote(string v) => "\"" + v.Replace("\"", "\"\"") + "\"";
}
