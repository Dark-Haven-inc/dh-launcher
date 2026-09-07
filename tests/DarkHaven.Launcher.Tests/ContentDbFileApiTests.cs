using System.IO.Compression;
using System.Text;
using DarkHaven.ContentDb;
using Microsoft.Data.Sqlite;
using Robust.LoaderApi;
using Xunit;
using ZstdSharp;

namespace DarkHaven.Launcher.Tests;

/// <summary>
/// Exercises the loader's content-DB read path (the highest-risk piece) against a synthetic DB
/// built to the same schema the real launcher writes.
/// </summary>
public class ContentDbFileApiTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"dhtest-{Guid.NewGuid():N}.db");

    private static readonly (string Path, string Body, ContentCompressionScheme Scheme)[] Files =
    [
        ("Assemblies/Content.Client.dll", "plain uncompressed blob body", ContentCompressionScheme.None),
        ("Prototypes/entities.yml", "a longer body that we deflate for the deflate code path .......", ContentCompressionScheme.Deflate),
        ("Textures/a.png", "zstd compressed blob body — with unicode éè", ContentCompressionScheme.ZStd),
    ];

    public ContentDbFileApiTests() => BuildFixtureDb();

    [Fact]
    public void Serves_every_compression_scheme_and_reports_all_files()
    {
        using var api = new ContentDbFileApi(_dbPath, version: 1);

        foreach (var (path, body, _) in Files)
        {
            Assert.True(((IFileApi)api).TryOpen(path, out var stream), $"missing {path}");
            using var reader = new StreamReader(stream!, Encoding.UTF8);
            Assert.Equal(body, reader.ReadToEnd());
        }

        Assert.Equal(Files.Select(f => f.Path).ToHashSet(), api.AllFiles.ToHashSet());
        Assert.False(((IFileApi)api).TryOpen("does/not/exist", out _));
    }

    [Fact]
    public void Registers_and_clears_the_running_client_row()
    {
        var pid = Environment.ProcessId;
        using (new ContentDbFileApi(_dbPath, version: 1))
            Assert.Equal(1L, ScalarLong($"SELECT COUNT(*) FROM RunningClient WHERE ProcessId = {pid}"));

        Assert.Equal(0L, ScalarLong($"SELECT COUNT(*) FROM RunningClient WHERE ProcessId = {pid}"));
    }

    [Fact]
    public void Unknown_version_throws()
    {
        Assert.Throws<InvalidOperationException>(() => new ContentDbFileApi(_dbPath, version: 999));
    }

    private long ScalarLong(string sql)
    {
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private void BuildFixtureDb()
    {
        using var con = new SqliteConnection($"Data Source={_dbPath}");
        con.Open();
        using (var schema = con.CreateCommand())
        {
            schema.CommandText = """
                CREATE TABLE Content (Id INTEGER PRIMARY KEY AUTOINCREMENT, Hash BLOB, Size INTEGER, Compression INTEGER, Data BLOB);
                CREATE TABLE ContentVersion (Id INTEGER PRIMARY KEY AUTOINCREMENT, Hash BLOB, ForkId TEXT, ForkVersion TEXT);
                CREATE TABLE ContentManifest (Id INTEGER PRIMARY KEY, VersionId INTEGER, Path TEXT, ContentId INTEGER);
                CREATE TABLE RunningClient (ProcessId INTEGER PRIMARY KEY, MainModule TEXT, UsedVersion INTEGER);
                INSERT INTO ContentVersion (Id, Hash, ForkId, ForkVersion) VALUES (1, x'00', 'test', 'v1');
                """;
            schema.ExecuteNonQuery();
        }

        foreach (var (path, body, scheme) in Files)
        {
            var raw = Encoding.UTF8.GetBytes(body);
            var stored = scheme switch
            {
                ContentCompressionScheme.None => raw,
                ContentCompressionScheme.Deflate => Deflate(raw),
                ContentCompressionScheme.ZStd => new Compressor().Wrap(raw).ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(scheme)),
            };

            using var ins = con.CreateCommand();
            ins.CommandText = """
                INSERT INTO Content (Size, Compression, Data) VALUES ($size, $c, $data);
                INSERT INTO ContentManifest (VersionId, Path, ContentId) VALUES (1, $path, last_insert_rowid());
                """;
            ins.Parameters.AddWithValue("$size", raw.Length);
            ins.Parameters.AddWithValue("$c", (int)scheme);
            ins.Parameters.AddWithValue("$data", stored);
            ins.Parameters.AddWithValue("$path", path);
            ins.ExecuteNonQuery();
        }
    }

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            d.Write(data);
        return ms.ToArray();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }
}
