using DarkHaven.ContentDb;
using DarkHaven.Launcher.Engine;
using DarkHaven.Launcher.Models;
using Microsoft.Data.Sqlite;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DarkHaven.Launcher.Content;

/// <summary>What the loader needs to launch a resolved server version.</summary>
public sealed record LaunchManifest(long VersionId, string EngineVersion, IReadOnlyList<(string Name, string Version)> Modules);

/// <summary>
/// Brings the local content store up to date for a server: reuse an existing <c>ContentVersion</c>
/// if we already have this exact manifest, otherwise download it via <see cref="ManifestDownloader"/>,
/// then make sure the engine + modules it needs are on disk.
/// </summary>
public sealed class ContentUpdater(ContentDatabase db, ManifestDownloader downloader, EngineManager engines)
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<LaunchManifest> UpdateAsync(
        ServerBuildInfo build, DownloadProgress? progress = null, CancellationToken cancel = default)
    {
        db.Initialize();

        long versionId;
        await using (var con = db.Connect())
        {
            versionId = FindExistingVersion(con, build)
                        ?? await DownloadNewVersionAsync(con, build, progress, cancel);

            Touch(con, versionId);
        }

        // Engine + modules — done outside the content transaction so an interrupted engine
        // download doesn't roll back a good content version.
        var resolvedEngine = await engines.EnsureEngineAsync(build.EngineVersion, progress, cancel);

        var modules = new List<(string, string)>();
        await using (var con = db.Connect())
        {
            // Persist the resolved engine version (manifest redirects may have changed it).
            SetEngineDependency(con, versionId, "Robust", resolvedEngine);

            foreach (var moduleName in ReadRequiredModules(con, versionId))
            {
                await engines.EnsureModuleAsync(moduleName, resolvedEngine, progress, cancel);
                // The concrete module version is resolved inside EnsureModuleAsync; for the launch
                // manifest we only need the name + engine version pairing the loader expects.
                modules.Add((moduleName, resolvedEngine));
                SetEngineDependency(con, versionId, moduleName, resolvedEngine);
            }
        }

        Log.Information("Content ready: version {VersionId}, engine {Engine}, {ModuleCount} modules",
            versionId, resolvedEngine, modules.Count);
        return new LaunchManifest(versionId, resolvedEngine, modules);
    }

    private static long? FindExistingVersion(SqliteConnection con, ServerBuildInfo build)
    {
        if (string.IsNullOrEmpty(build.ManifestHash))
            return null;

        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT Id FROM ContentVersion
            WHERE Hash = $hash
            ORDER BY (ForkId IS $fork) DESC, LastUsed DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$hash", Convert.FromHexString(build.ManifestHash!));
        cmd.Parameters.AddWithValue("$fork", (object?)build.ForkId ?? DBNull.Value);

        var result = cmd.ExecuteScalar();
        if (result is null)
            return null;

        var id = Convert.ToInt64(result);
        Log.Debug("Reusing existing content version {Id} for manifest {Hash}", id, build.ManifestHash);
        return id;
    }

    private async Task<long> DownloadNewVersionAsync(
        SqliteConnection con, ServerBuildInfo build, DownloadProgress? progress, CancellationToken cancel)
    {
        var insertedContentIds = new List<long>();
        await using var tx = con.BeginTransaction();

        long versionId;
        using (var insert = con.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO ContentVersion (Hash, ForkId, ForkVersion, LastUsed)
                VALUES (zeroblob(32), $fork, $ver, datetime('now'))
                RETURNING Id
                """;
            insert.Parameters.AddWithValue("$fork", (object?)build.ForkId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ver", (object?)build.Version ?? DBNull.Value);
            versionId = Convert.ToInt64(insert.ExecuteScalar());
        }

        try
        {
            var manifestHash = await downloader.DownloadAsync(con, versionId, build, insertedContentIds, progress, cancel);

            using var update = con.CreateCommand();
            update.CommandText = "UPDATE ContentVersion SET Hash = $hash WHERE Id = $id";
            update.Parameters.AddWithValue("$hash", manifestHash);
            update.Parameters.AddWithValue("$id", versionId);
            update.ExecuteNonQuery();

            SetEngineDependency(con, versionId, "Robust", build.EngineVersion);

            tx.Commit();
            Log.Information("Downloaded new content version {Id} ({Fork}/{Ver})", versionId, build.ForkId, build.Version);
            return versionId;
        }
        catch (Exception e) when (e is not SqliteException)
        {
            if (insertedContentIds.Count == 0)
            {
                tx.Rollback();
                throw;
            }

            // Salvage the blobs we did fetch so a retry is faster.
            Log.Warning(e, "Download interrupted after {Count} blobs — saving them", insertedContentIds.Count);
            SaveInterruptedDownload(con, insertedContentIds);
            using (var delVersion = con.CreateCommand())
            {
                delVersion.CommandText = "DELETE FROM ContentVersion WHERE Id = $id";
                delVersion.Parameters.AddWithValue("$id", versionId);
                delVersion.ExecuteNonQuery();
            }
            tx.Commit();
            throw;
        }
    }

    private static void SaveInterruptedDownload(SqliteConnection con, List<long> contentIds)
    {
        long interruptedId;
        using (var ins = con.CreateCommand())
        {
            ins.CommandText = "INSERT INTO InterruptedDownload (Added) VALUES (datetime('now')) RETURNING Id";
            interruptedId = Convert.ToInt64(ins.ExecuteScalar());
        }

        using var link = con.CreateCommand();
        link.CommandText = "INSERT OR IGNORE INTO InterruptedDownloadContent (InterruptedDownloadId, ContentId) VALUES ($d, $c)";
        link.Parameters.AddWithValue("$d", interruptedId);
        var pc = link.Parameters.Add("$c", SqliteType.Integer);
        foreach (var id in contentIds)
        {
            pc.Value = id;
            link.ExecuteNonQuery();
        }
    }

    private static void Touch(SqliteConnection con, long versionId)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE ContentVersion SET LastUsed = datetime('now') WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", versionId);
        cmd.ExecuteNonQuery();
    }

    private static void SetEngineDependency(SqliteConnection con, long versionId, string module, string version)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ContentEngineDependency (VersionId, ModuleName, ModuleVersion)
            VALUES ($v, $m, $ver)
            ON CONFLICT(VersionId, ModuleName) DO UPDATE SET ModuleVersion = excluded.ModuleVersion
            """;
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.Parameters.AddWithValue("$m", module);
        cmd.Parameters.AddWithValue("$ver", version);
        cmd.ExecuteNonQuery();
    }

    private IReadOnlyList<string> ReadRequiredModules(SqliteConnection con, long versionId)
    {
        using var blob = OpenBlob(con, versionId, "manifest.yml");
        if (blob is null)
            return [];

        try
        {
            using var reader = new StreamReader(blob);
            var data = YamlDeserializer.Deserialize<ResourceManifest?>(reader);
            return data?.Modules ?? [];
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to parse manifest.yml for version {Version}", versionId);
            return [];
        }
    }

    /// <summary>Opens a single file blob from a content version for reading (decompressing as needed).</summary>
    internal static Stream? OpenBlob(SqliteConnection con, long versionId, string path)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = """
            SELECT c.Id, c.Compression FROM ContentManifest cm
            JOIN Content c ON c.Id = cm.ContentId
            WHERE cm.VersionId = $v AND cm.Path = $p
            """;
        cmd.Parameters.AddWithValue("$v", versionId);
        cmd.Parameters.AddWithValue("$p", path);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        var rowId = reader.GetInt64(0);
        var compression = (ContentCompressionScheme)reader.GetInt32(1);
        var blob = new SqliteBlob(con, "Content", "Data", rowId, readOnly: true);

        return compression switch
        {
            ContentCompressionScheme.None => blob,
            ContentCompressionScheme.Deflate => new System.IO.Compression.DeflateStream(blob, System.IO.Compression.CompressionMode.Decompress),
            ContentCompressionScheme.ZStd => new ZstdSharp.DecompressionStream(blob),
            _ => throw new InvalidDataException($"Unknown compression {compression}"),
        };
    }

    private sealed class ResourceManifest
    {
        public List<string> Modules { get; set; } = [];
        public bool MultiWindow { get; set; }
    }
}
