using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MusicFinder.Infrastructure.Persistence;

public sealed partial class MigrationRunner
{
    private readonly SqliteConnectionFactory _factory;

    public MigrationRunner(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>Highest embedded migration number; tests use it instead of hard-coding how many scripts exist.</summary>
    public static int LatestVersion { get; } = typeof(MigrationRunner).Assembly.GetManifestResourceNames()
        .Select(n => ScriptName().Match(n))
        .Where(m => m.Success)
        .Max(m => int.Parse(m.Groups[1].Value));

    /// <summary>
    /// Applies pending embedded migrations (NNNN_name.sql). Returns the number applied.
    /// <paramref name="maxVersion"/> stops early; used by upgrade tests that start from an older schema.
    /// </summary>
    public int Migrate(int? maxVersion = null)
    {
        using var conn = _factory.Open();
        Exec(conn, "PRAGMA journal_mode=WAL;");
        EnsureFts5(conn);
        Exec(conn, "CREATE TABLE IF NOT EXISTS SchemaVersions (Version INTEGER PRIMARY KEY, Name TEXT NOT NULL, AppliedUtc TEXT NOT NULL);");

        var applied = new HashSet<int>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT Version FROM SchemaVersions";
            using var r = cmd.ExecuteReader();
            while (r.Read()) applied.Add(r.GetInt32(0));
        }

        var asm = typeof(MigrationRunner).Assembly;
        var scripts = asm.GetManifestResourceNames()
            .Select(n => (Name: n, Match: ScriptName().Match(n)))
            .Where(x => x.Match.Success)
            .Select(x => (Version: int.Parse(x.Match.Groups[1].Value), Title: x.Match.Groups[2].Value, x.Name))
            .OrderBy(x => x.Version)
            .ToList();

        var count = 0;
        foreach (var s in scripts.Where(s => !applied.Contains(s.Version) && (maxVersion is null || s.Version <= maxVersion)))
        {
            using var stream = asm.GetManifestResourceStream(s.Name)!;
            using var reader = new StreamReader(stream);
            var sql = reader.ReadToEnd();
            using var tx = conn.BeginTransaction();
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = sql;
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "INSERT INTO SchemaVersions(Version, Name, AppliedUtc) VALUES ($v, $n, $t)";
                    cmd.Parameters.AddWithValue("$v", s.Version);
                    cmd.Parameters.AddWithValue("$n", s.Title);
                    cmd.Parameters.AddWithValue("$t", DapperConfig.ToDb(DateTime.UtcNow));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
                count++;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
        return count;
    }

    private static void EnsureFts5(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5')";
        if (Convert.ToInt32(cmd.ExecuteScalar()) != 1)
            throw new InvalidOperationException("The bundled SQLite build does not include FTS5, which MusicFinder requires.");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [GeneratedRegex(@"\.Migrations\.(\d{4})_(.+)\.sql$")]
    private static partial Regex ScriptName();
}
