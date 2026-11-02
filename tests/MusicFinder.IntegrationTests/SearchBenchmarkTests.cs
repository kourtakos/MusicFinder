using System.Diagnostics;
using System.Text;
using MusicFinder.Domain.Enums;
using MusicFinder.Domain.Models;
using MusicFinder.Infrastructure.Search;
using Xunit.Abstractions;

namespace MusicFinder.IntegrationTests;

/// <summary>
/// Opt-in performance check of smart Greek search on a synthetic library. Does nothing unless the environment variable MF_BENCH=1 is set:
/// <c>MF_BENCH=1 dotnet test tests/MusicFinder.IntegrationTests --filter "FullyQualifiedName~SearchBenchmarkTests"</c>.
/// MF_BENCH_TRACKS changes the size (default 100000). Results are printed and written to the temp folder (mf-bench-results.txt).
/// </summary>
public class SearchBenchmarkTests(ITestOutputHelper output)
{
    private static readonly string[] EnglishWords =
        ["love", "night", "dance", "fire", "heart", "dream", "city", "summer", "river", "gold", "star", "moon", "rain", "road", "wild", "blue",
         "home", "light", "shadow", "angel", "freak", "groove", "funky", "disco", "soul", "midnight", "paradise", "electric", "thunder", "velvet",
         "crystal", "ocean", "mountain", "desert", "silver", "broken", "golden", "lonely", "burning", "falling", "rising", "running", "dancing",
         "echo", "empire", "garden", "harbor", "island", "jungle", "kingdom", "legend", "memory", "nation", "orbit", "phoenix", "queen", "rhythm",
         "street", "tiger", "universe", "voyage", "whisper", "yellow", "zodiac", "baby", "girl", "boy", "world", "time", "forever", "tonight"];

    private static readonly string[] GreekNames =
        ["Χάρις Αλεξίου", "Γιώργος Μαζωνάκης", "Νίκος Παπάζογλου", "Δημήτρης Μητροπάνος", "Γλυκερία", "Αντύπας", "Πάνος Κιάμος", "Ελευθερία Αρβανιτάκη",
         "Μαρινέλλα", "Στέλιος Καζαντζίδης", "Βασίλης Παπακωνσταντίνου", "Θάνος Πετρέλης", "Άλκηστις Πρωτοψάλτη", "Σωκράτης Μάλαμας", "Παντελής Παντελίδης",
         "Ζωή Παπαδοπούλου", "Μιχάλης Χατζηγιάννης", "Ψαραντώνης", "Λαυρέντης Μαχαιρίτσας", "Νότης Σφακιανάκης"];

    private static readonly string[] GreekWords =
        ["Καλημέρα", "Αγάπη", "Θάλασσα", "Καρδιά", "Νύχτα", "Όνειρο", "Φεγγάρι", "Ουρανός", "Δρόμος", "Χαμόγελο", "Βραδινές", "Περιπλανήσεις", "Σ' αγαπώ"];

    private static readonly string[] GreeklishNames =
        ["Xaris Alexiou", "Giorgos Mazonakis", "Nikos Papazoglou", "Dimitris Mitropanos", "Glykeria", "Marinella", "Stelios Kazantzidis", "Pantelis Pantelidis"];

    private static readonly (string Query, string Note)[] Queries =
    [
        ("pa", "2-letter prefix"), ("παπ", "Greek prefix"), ("papaz", "Greeklish prefix"), ("xaris alex", "two Greeklish prefixes"),
        ("alej;ioy", "wrong keyboard layout"), ("giorgos", "Greeklish, exists in both scripts"), ("mazwnakhs", "Greeklish with eta"),
        ("le fre", "English prefixes"), ("\"midnight groove\"", "phrase"), ("the", "very common word"), ("artist:xaris", "scoped"),
        ("bpm:120-128 disco", "broad: 25% of tracks + filter"), ("λαικα", "broad: folder of 20% of tracks"), ("xa", "2-letter prefix"), ("papzoglou", "typo (fuzzy)"),
        ("", "empty, title order")
    ];

    [Fact]
    public async Task Benchmark()
    {
        if (Environment.GetEnvironmentVariable("MF_BENCH") != "1") return; // opt-in

        var tracks = int.TryParse(Environment.GetEnvironmentVariable("MF_BENCH_TRACKS"), out var n) ? n : 100_000;
        var report = new StringBuilder();
        void Say(string line) { report.AppendLine(line); output.WriteLine(line); }

        await using var db = new TestDb();
        var root = await db.Roots.AddAsync(@"C:\Bench");
        var folders = new List<long>();
        var folderPaths = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            var path = $@"C:\Bench\{(i % 5 == 0 ? "Λαϊκά " : "Pop ")}{i}";
            folderPaths.Add(path);
            folders.Add(await db.Folders.UpsertAsync(root.Id, null, path, $"F{i}"));
        }

        // ---- generate ----
        var rng = new Random(1);
        string Pick(string[] a) => a[rng.Next(a.Length)];
        var sw = Stopwatch.StartNew();
        const int Batch = 2000;
        for (var start = 0; start < tracks; start += Batch)
        {
            var batch = new List<MusicFinder.Domain.Entities.Track>(Batch);
            for (var i = start; i < Math.Min(start + Batch, tracks); i++)
            {
                var kind = rng.Next(100);
                string artist, title;
                if (kind < 40) { artist = $"{Pick(EnglishWords)} {Pick(EnglishWords)}".ToUpperInvariant()[0] + $"{Pick(EnglishWords)} {Pick(EnglishWords)}"; title = $"{Pick(EnglishWords)} {Pick(EnglishWords)} {Pick(EnglishWords)}"; }
                else if (kind < 75) { artist = Pick(GreekNames); title = $"{Pick(GreekWords)} {rng.Next(100)}"; }
                else if (kind < 90) { artist = Pick(GreeklishNames); title = $"{Pick(EnglishWords)} {Pick(EnglishWords)}"; }
                else { artist = Pick(GreekNames); title = $"{Pick(EnglishWords)} {Pick(GreekWords)}"; }

                var t = TestDb.MakeTrack(folders[i % folders.Count], $@"{folderPaths[i % folders.Count]}\{artist} - {title} {i}.mp3", title, artist, rng.Next(4) == 0 ? "Disco" : "Pop");
                t.Bpm = rng.Next(5) == 0 ? null : 60 + rng.Next(120);
                batch.Add(t);
            }
            await db.Tracks.UpsertBatchAsync(batch);
        }
        Say($"generated {tracks:N0} tracks in {sw.Elapsed.TotalSeconds:F1} s");

        // ---- index ----
        sw.Restart();
        var indexed = await db.AliasJob.RunAsync();
        var indexSeconds = sw.Elapsed.TotalSeconds;
        Say($"alias index: {indexed:N0} rows in {indexSeconds:F1} s ({indexed / Math.Max(indexSeconds, 0.001):N0} rows/s)");

        var vocabulary = new FtsVocabulary(db.Factory);
        sw.Restart();
        await vocabulary.EnsureLoadedAsync();
        Say($"vocabulary: {vocabulary.WordCount:N0} words in {sw.Elapsed.TotalMilliseconds:F0} ms");

        using (var conn = db.Factory.Open())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        if (Environment.GetEnvironmentVariable("MF_BENCH_KEEP") == "1")
            File.Copy(db.Factory.DatabasePath, Path.Combine(Path.GetTempPath(), "mf-bench.db"), overwrite: true); // for profiling the SQL by hand
        var size = new FileInfo(db.Factory.DatabasePath).Length / 1024.0 / 1024.0;
        Say($"database file: {size:F0} MB");

        var search = new SqliteSearchService(db.Factory, null, vocabulary);

        // ---- search ----
        Say("");
        Say($"{"query",-26}{"note",-34}{"rows",8}  {"page p50",9}{"p95",8}{"max",8}   {"count p95",10}");
        var failures = new List<string>();
        foreach (var (query, note) in Queries)
        {
            var q = new SearchQuery { Text = query, GlobalScope = true, Sort = query.Length == 0 ? SortField.Title : SortField.Relevance, PageSize = 200 };
            for (var i = 0; i < 20; i++) { await search.SearchAsync(q, default); await search.CountAsync(q, default); }   // warm up

            var pages = new List<double>();
            var counts = new List<double>();
            var rows = 0;
            for (var i = 0; i < 50; i++)
            {
                var s = Stopwatch.StartNew();
                var page = await search.SearchAsync(q, default);
                pages.Add(s.Elapsed.TotalMilliseconds);
                rows = page.Items.Count;
                s.Restart();
                await search.CountAsync(q, default);
                counts.Add(s.Elapsed.TotalMilliseconds);
            }
            pages.Sort();
            counts.Sort();
            double P(List<double> l, double p) => l[(int)Math.Min(l.Count - 1, Math.Ceiling(p * l.Count) - 1)];
            Say($"{(query.Length == 0 ? "(empty)" : query),-26}{note,-34}{rows,8}  {P(pages, .5),8:F1} {P(pages, .95),7:F1} {pages[^1],7:F1}   {P(counts, .95),9:F1}");

            if (P(pages, .95) > 50 && !note.Contains("2-letter") && !note.StartsWith("broad")) failures.Add($"{query}: page p95 {P(pages, .95):F1} ms");
        }

        // ---- second page through the keyset cursor ----
        foreach (var query in new[] { "xaris", "love", "giorgos" })
        {
            var first = await search.SearchAsync(new SearchQuery { Text = query, GlobalScope = true, Sort = SortField.Relevance, PageSize = 200 }, default);
            if (first.Next is null) continue;
            var times = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                var s = Stopwatch.StartNew();
                await search.SearchAsync(new SearchQuery { Text = query, GlobalScope = true, Sort = SortField.Relevance, PageSize = 200, After = first.Next }, default);
                times.Add(s.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Say($"page 2 of '{query}': p50 {times[times.Count / 2]:F1} ms, max {times[^1]:F1} ms");
        }

        Say("");
        Say($"working set: {Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024:N0} MB");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "mf-bench-results.txt"), report.ToString(), new UTF8Encoding(true));

        Assert.True(indexSeconds < 60 * Math.Max(1, tracks / 100_000.0), $"indexing took {indexSeconds:F1} s");
        Assert.True(failures.Count == 0, "page p95 above 50 ms: " + string.Join("; ", failures));
    }
}
