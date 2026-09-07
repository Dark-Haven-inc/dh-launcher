using DarkHaven.Launcher.Api;
using Microsoft.Data.Sqlite;

namespace DarkHaven.Launcher.Data;

/// <summary>A stored account (from the <c>Login</c> table).</summary>
public sealed record StoredLogin(Guid UserId, string UserName, string Token, DateTimeOffset Expires)
{
    public AuthToken AsToken() => new(Token, Expires);
}

/// <summary>
/// The launcher's small settings/accounts DB. Schema mirrors the reference launcher's
/// <c>settings.db</c> so it feels familiar and could be imported later.
/// </summary>
public sealed class SettingsDatabase(string dbPath)
{
    private const int SchemaVersion = 1;

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
        if (version == 0)
            Exec(con, CreateV1);
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

    private static void Exec(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
