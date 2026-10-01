using System.IO;
using TestControllerGrpc.Models;
using TestControllerGrpc.Services;

namespace TestControllerGrpc.Tests.Services;

/// <summary>
/// Rolling pre-save backups of WatchList.xml. There is no undo in the editor, so this copy is the
/// only way back from a bad edit - and it must never be able to block the save itself.
/// </summary>
public sealed class WatchListBackupTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "wl-backup-" + Guid.NewGuid().ToString("N"));

    public WatchListBackupTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Path_(string name) => Path.Combine(_dir, name);

    private static WatchListConfig Config(string tag)
    {
        var config = new WatchListConfig();
        config.WatchItems.Add(new WatchItemConfig { Tag = tag, Path = @"C:\drops", Filter = "*.trigger" });
        return config;
    }

    private string[] Backups(string name) =>
        Directory.GetFiles(_dir, name + ".bak-*").OrderBy(f => f).ToArray();

    [Fact]
    public void Save_Should_NotWriteBackup_When_FileDoesNotExistYet()
    {
        var path = Path_("WatchList.xml");

        WatchListXmlParser.Save(Config("first"), path);

        Assert.True(File.Exists(path));
        Assert.Empty(Backups("WatchList.xml"));
    }

    [Fact]
    public void Save_Should_WriteTimestampedBackup_When_FileAlreadyExists()
    {
        var path = Path_("WatchList.xml");
        WatchListXmlParser.Save(Config("original"), path);

        WatchListXmlParser.Save(Config("replacement"), path);

        var backup = Assert.Single(Backups("WatchList.xml"));
        // The backup must hold the PREVIOUS content, not the content just written.
        Assert.Contains("original", File.ReadAllText(backup));
        Assert.Contains("replacement", File.ReadAllText(path));
    }

    [Fact]
    public void Save_Should_KeepOnlyTheNewestTen_When_SavedRepeatedly()
    {
        var path = Path_("WatchList.xml");
        WatchListXmlParser.Save(Config("gen0"), path);

        for (var i = 1; i <= 15; i++)
        {
            // Backups are named to the second, so distinct timestamps need a real gap; the
            // collision suffix covers the rest.
            WatchListXmlParser.Save(Config($"gen{i}"), path);
        }

        Assert.Equal(WatchListXmlParser.BackupsToKeep, Backups("WatchList.xml").Length);
    }

    [Fact]
    public void BackupExisting_Should_KeepNewest_When_PruningOldOnes()
    {
        var path = Path_("WatchList.xml");
        File.WriteAllText(path, "<WatchList />");

        // Stage aged backups directly. BackupExisting prunes as part of the same call, so the
        // fixture has to be stamped BEFORE it runs, not after - otherwise the prune can delete
        // the very file we are about to re-stamp.
        var aged = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var stale = Path_($"WatchList.xml.bak-2026010{i}-000000");
            File.WriteAllText(stale, $"<WatchList Gen=\"{i}\" />");
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-10 + i));
            aged.Add(stale);
        }

        var fresh = WatchListXmlParser.BackupExisting(path, keep: 2);
        Assert.NotNull(fresh);

        var remaining = Backups("WatchList.xml");
        Assert.Equal(2, remaining.Length);
        // Survivors are the newest two by write time: the one just taken, plus the newest stale one.
        Assert.Contains(fresh!, remaining);
        Assert.Contains(aged[^1], remaining);
        // ...and the three oldest are gone.
        Assert.DoesNotContain(aged[0], remaining);
        Assert.DoesNotContain(aged[1], remaining);
        Assert.DoesNotContain(aged[2], remaining);
    }

    [Fact]
    public void BackupExisting_Should_ReturnNull_When_SourceIsMissing()
    {
        Assert.Null(WatchListXmlParser.BackupExisting(Path_("absent.xml")));
    }

    /// <summary>A backup failure must never cost the user their save.</summary>
    [Fact]
    public void Save_Should_StillWrite_When_BackupCannotBeTaken()
    {
        var path = Path_("WatchList.xml");
        WatchListXmlParser.Save(Config("original"), path);

        using (var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // The file is exclusively locked, so File.Copy cannot read it.
            Assert.Null(WatchListXmlParser.BackupExisting(path));
        }

        WatchListXmlParser.Save(Config("replacement"), path);
        Assert.Contains("replacement", File.ReadAllText(path));
    }
}
