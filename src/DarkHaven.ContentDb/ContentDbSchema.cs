namespace DarkHaven.ContentDb;

/// <summary>
/// The content database schema. Structurally identical to the reference launcher's (so the loader's
/// <see cref="ContentDbFileApi"/> queries match), squashed to a single v1 create script keyed off
/// <c>PRAGMA user_version</c>.
/// </summary>
public static class ContentDbSchema
{
    public const int CurrentVersion = 1;

    public const string CreateV1 = """
        CREATE TABLE ContentVersion(
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Hash        BLOB NOT NULL,                 -- BLAKE2b of the full manifest
            ForkId      TEXT NULL,
            ForkVersion TEXT NULL,
            LastUsed    DATE NOT NULL,
            ZipHash     BLOB NULL                      -- SHA256 of a non-delta zip, if that path was used
        );

        CREATE TABLE Content(
            Id          INTEGER PRIMARY KEY AUTOINCREMENT,
            Hash        BLOB NOT NULL UNIQUE,          -- BLAKE2b of the uncompressed blob
            Size        INTEGER NOT NULL,             -- uncompressed size
            Compression INTEGER NOT NULL,             -- ContentCompressionScheme
            Data        BLOB NOT NULL,
            CONSTRAINT UncompressedSameSize CHECK(Compression != 0 OR length(Data) = Size)
        );

        CREATE TABLE ContentManifest(
            Id        INTEGER PRIMARY KEY,
            VersionId INTEGER NOT NULL REFERENCES ContentVersion(Id) ON DELETE CASCADE,
            Path      TEXT NOT NULL,
            ContentId INTEGER NOT NULL REFERENCES Content(Id) ON DELETE RESTRICT,
            CONSTRAINT NotDirectory CHECK (Path NOT LIKE '%/')
        );
        CREATE UNIQUE INDEX ContentManifestUniqueIndex ON ContentManifest(VersionId, Path);
        CREATE INDEX ContentManifest_ContentId ON ContentManifest(ContentId);

        CREATE TABLE ContentEngineDependency(
            Id            INTEGER PRIMARY KEY,
            VersionId     INTEGER NOT NULL REFERENCES ContentVersion(Id) ON DELETE CASCADE,
            ModuleName    TEXT NOT NULL,               -- 'Robust' == the base engine version
            ModuleVersion TEXT NOT NULL
        );
        CREATE UNIQUE INDEX ContentEngineModuleUniqueIndex ON ContentEngineDependency(VersionId, ModuleName);

        CREATE TABLE InterruptedDownload(
            Id    INTEGER PRIMARY KEY AUTOINCREMENT,
            Added DATE NOT NULL
        );
        CREATE TABLE InterruptedDownloadContent(
            Id                   INTEGER PRIMARY KEY AUTOINCREMENT,
            InterruptedDownloadId INTEGER NOT NULL REFERENCES InterruptedDownload(Id) ON DELETE CASCADE,
            ContentId            INTEGER NOT NULL UNIQUE REFERENCES Content(Id) ON DELETE CASCADE
        );

        CREATE TABLE RunningClient(
            ProcessId   INTEGER PRIMARY KEY NOT NULL,
            MainModule  TEXT NOT NULL,
            UsedVersion INTEGER NOT NULL REFERENCES ContentVersion(Id) ON DELETE RESTRICT
        );
        """;
}
