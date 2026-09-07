using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Robust.LoaderApi;
using ZstdSharp;

namespace DarkHaven.ContentDb;

/// <summary>
/// <see cref="IFileApi"/> that serves one <c>ContentVersion</c>'s files out of the launcher's SQLite
/// content DB. Managed port of the reference launcher's <c>ContentDbFileApi</c>: a small pool of
/// read-only connections, each holding an open read transaction so the launcher can't delete blobs
/// from under a running client. Also writes a <c>RunningClient</c> row for its lifetime.
/// </summary>
public sealed class ContentDbFileApi : IFileApi, IDisposable
{
    private readonly record struct FileEntry(long RowId, int Length, ContentCompressionScheme Compression);

    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.Ordinal);
    private readonly string _dbPath;
    private readonly SemaphoreSlim _poolSemaphore;
    private readonly ConcurrentBag<SqliteConnection> _pool = new();
    private readonly SqliteConnection _master;
    private readonly int _poolSize;

    public ContentDbFileApi(string contentDbPath, long version)
    {
        _dbPath = contentDbPath;

        _master = Open(readOnly: false);
        AddRunningClient(_master, version);
        BeginRead(_master);

        LoadManifest(_master, version);

        _poolSize = ConnectionPoolSize();
        _poolSemaphore = new SemaphoreSlim(_poolSize, _poolSize);
        _pool.Add(_master);
        for (var i = 1; i < _poolSize; i++)
        {
            var con = Open(readOnly: true);
            BeginRead(con);
            _pool.Add(con);
        }
    }

    public IEnumerable<string> AllFiles => _files.Keys;

    public bool TryOpen(string path, [NotNullWhen(true)] out Stream? stream)
    {
        if (!_files.TryGetValue(path, out var entry))
        {
            stream = null;
            return false;
        }

        _poolSemaphore.Wait();
        SqliteConnection? con = null;
        try
        {
            if (!_pool.TryTake(out con))
                throw new InvalidOperationException("Entered pool semaphore but no connection available");

            using var blob = new SqliteBlob(con, "Content", "Data", entry.RowId, readOnly: true);

            var buffer = GC.AllocateUninitializedArray<byte>(entry.Length);
            var target = new MemoryStream(buffer, writable: false);

            switch (entry.Compression)
            {
                case ContentCompressionScheme.None:
                    FillExact(blob, buffer);
                    break;
                case ContentCompressionScheme.Deflate:
                    using (var d = new DeflateStream(blob, CompressionMode.Decompress, leaveOpen: true))
                        FillExact(d, buffer);
                    break;
                case ContentCompressionScheme.ZStd:
                    using (var d = new DecompressionStream(blob, leaveOpen: true))
                        FillExact(d, buffer);
                    break;
                default:
                    throw new NotSupportedException($"Unknown compression scheme {entry.Compression} for {path}");
            }

            stream = target;
            return true;
        }
        finally
        {
            if (con != null)
                _pool.Add(con);
            _poolSemaphore.Release();
        }
    }

    private SqliteConnection Open(bool readOnly)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();

        var con = new SqliteConnection(cs);
        con.Open();
        return con;
    }

    private static void BeginRead(SqliteConnection con)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "BEGIN";
        cmd.ExecuteNonQuery();
    }

    private static void AddRunningClient(SqliteConnection con, long version)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO RunningClient (ProcessId, MainModule, UsedVersion)
            VALUES ($pid, $module, $version)
            """;
        cmd.Parameters.AddWithValue("$pid", Environment.ProcessId);
        cmd.Parameters.AddWithValue("$module", Process.GetCurrentProcess().MainModule?.FileName ?? "");
        cmd.Parameters.AddWithValue("$version", version);
        cmd.ExecuteNonQuery();
    }

    private void LoadManifest(SqliteConnection con, long version)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT c.Id, c.Size, c.Compression, cm.Path
            FROM Content c
            JOIN ContentManifest cm ON cm.ContentId = c.Id
            WHERE cm.VersionId = $version AND cm.Path NOT LIKE '%/'
            """;
        cmd.Parameters.AddWithValue("$version", version);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rowId = reader.GetInt64(0);
            var size = reader.GetInt32(1);
            var compression = (ContentCompressionScheme)reader.GetInt32(2);
            var path = reader.GetString(3);
            _files[path] = new FileEntry(rowId, size, compression);
        }

        if (_files.Count == 0)
            throw new InvalidOperationException($"Content version {version} has no files (unknown version?)");
    }

    private static int ConnectionPoolSize()
    {
        var env = Environment.GetEnvironmentVariable("SS14_LOADER_CONTENT_POOL_SIZE");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var n) && n > 0)
            return n;
        return Math.Max(2, Environment.ProcessorCount);
    }

    private static void FillExact(Stream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = stream.Read(buffer, read, buffer.Length - read);
            if (n == 0)
                throw new EndOfStreamException("Blob shorter than its declared size");
            read += n;
        }
    }

    public void Dispose()
    {
        for (var i = 0; i < _poolSize; i++)
        {
            _poolSemaphore.Wait();
            if (!_pool.TryTake(out var con))
            {
                Console.Error.WriteLine("loader: failed to reclaim a content DB connection on shutdown");
                continue;
            }

            if (!ReferenceEquals(con, _master))
                con.Dispose();
        }

        try
        {
            using (var rollback = _master.CreateCommand())
            {
                rollback.CommandText = "ROLLBACK";
                rollback.ExecuteNonQuery();
            }

            using var cmd = _master.CreateCommand();
            cmd.CommandText = "DELETE FROM RunningClient WHERE ProcessId = $pid";
            cmd.Parameters.AddWithValue("$pid", Environment.ProcessId);
            cmd.ExecuteNonQuery();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"loader: error clearing RunningClient: {e.Message}");
        }

        _master.Dispose();
    }
}
