using DarkHaven.ContentDb;
using Xunit;

namespace DarkHaven.Launcher.Tests;

public class ContentDatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dh-content-{Guid.NewGuid():N}.db");

    [Fact]
    public void CheckIntegrity_is_null_for_a_missing_or_fresh_db()
    {
        var db = new ContentDatabase(_path);
        Assert.Null(db.CheckIntegrity());          // file doesn't exist yet

        db.Initialize();
        Assert.Null(db.CheckIntegrity());          // freshly created + migrated
    }

    [Fact]
    public void CheckIntegrity_reports_a_corrupt_file()
    {
        var db = new ContentDatabase(_path);
        db.Initialize();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // scribble over the header so SQLite no longer recognises it
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Write))
            fs.Write(new byte[512]);

        Assert.NotNull(db.CheckIntegrity());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var s in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + s); } catch { /* best effort */ }
    }
}
