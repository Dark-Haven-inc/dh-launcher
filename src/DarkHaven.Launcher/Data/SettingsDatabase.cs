using DarkHaven.Launcher.Api;
using Microsoft.Data.Sqlite;

namespace DarkHaven.Launcher.Data;

/// <summary>A stored account (from the <c>Login</c> table).</summary>
public sealed record StoredLogin(Guid UserId, string UserName, string Token, DateTimeOffset Expires)
{
    public AuthToken AsToken() => new(Token, Expires);
}

/// <summary>A server the player has connected to before.</summary>
public sealed record RecentServer(string Address, string Name, DateTimeOffset LastPlayed, bool IsRegion);

/// <summary>A pinned favourite server.</summary>
public sealed record FavoriteServerEntry(string Address, string Name);

/// <summary>Aggregated local playtime for one server / region.</summary>
public sealed record PlaytimeEntry(string Name, string Address, long TotalSeconds, int Sessions, DateTimeOffset LastPlayed, bool IsRegion);

/// <summary>
/// The launcher's small settings/accounts DB. Schema mirrors the reference launcher's
/// <c>settings.db</c> so it feels familiar and could be imported later.
/// </summary>
public sealed class SettingsDatabase(string dbPath)
{
    private const int SchemaVersion = 3;

    private const string CreateV1 = """
        CREATE TABLE Login (
            UserId   TEXT PRIMARY KEY NOT NULL,
            UserName TEXT NOT NULL,
            Token    TEXT NOT NULL,
            Expires  DATETIME NOT NULL
        );
        CREATE TABLE Config (
            Key   TEXT PRIMARY KEY NOT NULL,
            Value
        );
        CREATE TABLE FavoriteServer (
            Address  TEXT PRIMARY KEY NOT NULL,
            Name     TEXT,
            RaiseTime DATETIME
        );
        CREATE TABLE Hub (
            Address  TEXT NOT NULL PRIMARY KEY,
            Priority INTEGER NOT NULL UNIQUE
        );
        CREATE TABLE AcceptedPrivacyPolicy (
            Identifier   TEXT NOT NULL PRIMARY KEY,
            Version      TEXT NOT NULL,
            AcceptedTime DATETIME NOT NULL
        );
        """;

    private const string CreateV2 = """
        CREATE TABLE RecentServer (
            Address    TEXT PRIMARY KEY NOT NULL,
            Name       TEXT NOT NULL,
            LastPlayed DATETIME NOT NULL,
            IsRegion   INTEGER NOT NULL DEFAULT 0
        );
        """;

    private const string CreateV3 = """
        CREATE TABLE PlaySession (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            Address    TEXT NOT NULL,
            Name       TEXT NOT NULL,
            StartedUtc DATETIME NOT NULL,
            Seconds    INTEGER NOT NULL DEFAULT 0,
            IsRegion   INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX IX_PlaySession_Address ON PlaySession(Address);
        """;

    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var con = Connect();

        using var pragma = con.CreateCommand();
        pragma.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt64(pragma.ExecuteScalar());
        if (version >= SchemaVersion)
            return;

        using var tx = con.BeginTransaction();
        if (version < 1)
            Exec(con, CreateV1);
        if (version < 2)
            Exec(con, CreateV2);
        if (version < 3)
            Exec(con, CreateV3);
        Exec(con, $"PRAGMA user_version = {SchemaVersion}");
        tx.Commit();
    }

    private SqliteConnection Connect()
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
        var con = new SqliteConnection(cs);
        con.Open();
        return con;
    }

    // --- logins ---

    public IReadOnlyList<StoredLogin> GetLogins()
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT UserId, UserName, Token, Expires FROM Login";
        using var reader = cmd.ExecuteReader();

        var list = new List<StoredLogin>();
        while (reader.Read())
        {
            list.Add(new StoredLogin(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetDateTime(3)));
        }
        return list;
    }

    public void UpsertLogin(StoredLogin login)
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Login (UserId, UserName, Token, Expires)
            VALUES ($id, $name, $token, $expires)
            ON CONFLICT(UserId) DO UPDATE SET
                UserName = excluded.UserName, Token = excluded.Token, Expires = excluded.Expires
            """;
        cmd.Parameters.AddWithValue("$id", login.UserId.ToString());
        cmd.Parameters.AddWithValue("$name", login.UserName);
        cmd.Parameters.AddWithValue("$token", login.Token);
        cmd.Parameters.AddWithValue("$expires", login.Expires.UtcDateTime);
        cmd.ExecuteNonQuery();
    }

    public void DeleteLogin(Guid userId)
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "DELETE FROM Login WHERE UserId = $id";
        cmd.Parameters.AddWithValue("$id", userId.ToString());
        cmd.ExecuteNonQuery();
    }

    // --- config kv ---

    public string? GetConfig(string key)
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Value FROM Config WHERE Key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetConfig(string key, string? value)
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Config (Key, Value) VALUES ($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", (object?)value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // --- recent servers ---

    private const int RecentKeep = 12;

    /// <summary>Records (or bumps) a server the player just connected to; trims to the newest few.</summary>
    public void RecordRecent(string address, string name, bool isRegion)
    {
        using var con = Connect();
        using var tx = con.BeginTransaction();

        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO RecentServer (Address, Name, LastPlayed, IsRegion)
                VALUES ($a, $n, $t, $r)
                ON CONFLICT(Address) DO UPDATE SET Name = excluded.Name, LastPlayed = excluded.LastPlayed, IsRegion = excluded.IsRegion
                """;
            cmd.Parameters.AddWithValue("$a", address);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.UtcDateTime);
            cmd.Parameters.AddWithValue("$r", isRegion ? 1 : 0);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = con.CreateCommand())
        {
            cmd.CommandText = """
                DELETE FROM RecentServer WHERE Address NOT IN (
                    SELECT Address FROM RecentServer ORDER BY LastPlayed DESC LIMIT $keep)
                """;
            cmd.Parameters.AddWithValue("$keep", RecentKeep);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public IReadOnlyList<RecentServer> GetRecent()
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Address, Name, LastPlayed, IsRegion FROM RecentServer ORDER BY LastPlayed DESC";
        using var reader = cmd.ExecuteReader();

        var list = new List<RecentServer>();
        while (reader.Read())
            list.Add(new RecentServer(reader.GetString(0), reader.GetString(1),
                new DateTimeOffset(reader.GetDateTime(2), TimeSpan.Zero), reader.GetInt64(3) != 0));
        return list;
    }

    // --- favourites ---

    public IReadOnlyList<FavoriteServerEntry> GetFavorites()
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT Address, COALESCE(Name, Address) FROM FavoriteServer ORDER BY RaiseTime DESC";
        using var reader = cmd.ExecuteReader();

        var list = new List<FavoriteServerEntry>();
        while (reader.Read())
            list.Add(new FavoriteServerEntry(reader.GetString(0), reader.GetString(1)));
        return list;
    }

    public bool IsFavorite(string address)
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM FavoriteServer WHERE Address = $a";
        cmd.Parameters.AddWithValue("$a", address);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>Adds or removes the favourite; returns the new state (true = now a favourite).</summary>
    public bool ToggleFavorite(string address, string name)
    {
        using var con = Connect();
        if (IsFavoriteOn(con, address))
        {
            using var del = con.CreateCommand();
            del.CommandText = "DELETE FROM FavoriteServer WHERE Address = $a";
            del.Parameters.AddWithValue("$a", address);
            del.ExecuteNonQuery();
            return false;
        }

        using var ins = con.CreateCommand();
        ins.CommandText = "INSERT INTO FavoriteServer (Address, Name, RaiseTime) VALUES ($a, $n, $t)";
        ins.Parameters.AddWithValue("$a", address);
        ins.Parameters.AddWithValue("$n", name);
        ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.UtcDateTime);
        ins.ExecuteNonQuery();
        return true;

        static bool IsFavoriteOn(SqliteConnection c, string addr)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM FavoriteServer WHERE Address = $a";
            cmd.Parameters.AddWithValue("$a", addr);
            return cmd.ExecuteScalar() is not null;
        }
    }

    // --- playtime ---

    /// <summary>Opens a play session and returns its id; call <see cref="EndPlaySession"/> when the client exits.</summary>
    public long StartPlaySession(string address, string name, bool isRegion)
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PlaySession (Address, Name, StartedUtc, Seconds, IsRegion)
            VALUES ($a, $n, $t, 0, $r);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$a", address);
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.UtcDateTime);
        cmd.Parameters.AddWithValue("$r", isRegion ? 1 : 0);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void EndPlaySession(long id, long seconds)
    {
        if (id <= 0 || seconds <= 0)
            return;

        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE PlaySession SET Seconds = $s WHERE Id = $id";
        cmd.Parameters.AddWithValue("$s", seconds);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public long GetTotalPlaytimeSeconds()
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(Seconds), 0) FROM PlaySession";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public DateTimeOffset? GetFirstPlayed()
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT StartedUtc FROM PlaySession ORDER BY StartedUtc ASC LIMIT 1";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? new DateTimeOffset(reader.GetDateTime(0), TimeSpan.Zero) : null;
    }

    public IReadOnlyList<PlaytimeEntry> GetPlaytimeByServer()
    {
        using var con = Connect();
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT Address, MAX(Name), SUM(Seconds), COUNT(*), MAX(StartedUtc), MAX(IsRegion)
            FROM PlaySession
            GROUP BY Address
            ORDER BY SUM(Seconds) DESC
            """;
        using var reader = cmd.ExecuteReader();

        var list = new List<PlaytimeEntry>();
        while (reader.Read())
            list.Add(new PlaytimeEntry(
                reader.GetString(1), reader.GetString(0), reader.GetInt64(2), reader.GetInt32(3),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero), reader.GetInt64(5) != 0));
        return list;
    }

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
