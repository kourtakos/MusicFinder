using Microsoft.Data.Sqlite;

namespace MusicFinder.Infrastructure.Persistence;

public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public SqliteConnectionFactory(string databasePath)
    {
        DatabasePath = databasePath;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public static void ReleaseAllPools() => SqliteConnection.ClearAllPools();

    /// <summary>Releases only this database's pooled connections (so its file can be deleted) without disturbing other databases in the process.</summary>
    public void ReleasePool()
    {
        using var conn = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(conn);
    }
}
