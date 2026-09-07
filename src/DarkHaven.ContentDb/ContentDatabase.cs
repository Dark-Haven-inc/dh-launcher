using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace DarkHaven.ContentDb;

/// <summary>One row of <c>ContentVersion</c>.</summary>
public sealed record ContentVersionRow(
    long Id,
    byte[] Hash,
    string? ForkId,
    string? ForkVersion,
    byte[]? ZipHash);

/// <summary>
/// Opens / migrates / hands out connections to the launcher's content DB and provides the shared
/// housekeeping the launcher and loader both need. Pooling is disabled so the WAL file is truncated
/// promptly (it can grow to 100+ MB during a big download).
/// </summary>
public sealed class ContentDatabase(string dbPath)
{
    public string Path { get; } = dbPath;

    public SqliteConnection Connect(bool foreignKeys = true)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = foreignKeys,
        }.ToString();

        var con = new SqliteConnection(cs);
        con.Open();
        return con;
    }

    /// <summary>Creates the DB if absent and applies migrations. Idempotent.</summary>
    public void Initialize()
    {
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

        using var con = Connect();
        Exec(con, "PRAGMA journal_mode=WAL");

        var userVersion = ScalarLong(con, "PRAGMA user_version");
        if (userVersion >= ContentDbSchema.CurrentVersion)
            return;

        var sw = Stopwatch.StartNew();
        using (var tx = con.BeginTransaction())
        {
            if (userVersion == 0)
                Exec(con, ContentDbSchema.CreateV1);

            Exec(con, $"PRAGMA user_version = {ContentDbSchema.CurrentVersion}");
            tx.Commit();
        }

        Console.Error.WriteLine($"content DB migrated {userVersion} -> {ContentDbSchema.CurrentVersion} in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>Content version ids currently pinned by a live client process (prunes dead rows).</summary>
    public static IReadOnlyList<long> GetRunningClientVersions(SqliteConnection con)
    {
        var running = new List<long>();
        var dead = new List<long>();

        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = "SELECT ProcessId, MainModule, UsedVersion FROM RunningClient";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var pid = reader.GetInt64(0);
                var mainModule = reader.GetString(1);
                var usedVersion = reader.GetInt64(2);
                if (IsProcessAlive((int)pid, mainModule))
                    running.Add(usedVersion);
                else
                    dead.Add(pid);
            }
        }

        foreach (var pid in dead)
        {
            using var del = con.CreateCommand();
            del.CommandText = "DELETE FROM RunningClient WHERE ProcessId = $pid";
            del.Parameters.AddWithValue("$pid", pid);
            del.ExecuteNonQuery();
        }

        return running;
    }

    private static bool IsProcessAlive(int pid, string mainModule)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return proc.MainModule?.FileName == mainModule;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static long ScalarLong(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
