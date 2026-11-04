using NLog;
using NzbDrone.Plugin.Sleezer.Core.PostProcessing;
using Xunit;

namespace Sleezer.Tests;

public class TagBackupsTests : IDisposable
{
    private static readonly Logger Log = LogManager.GetLogger(nameof(TagBackupsTests));
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tagbackups-" + Guid.NewGuid().ToString("N"));

    public TagBackupsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string[] Names() => [.. Directory.GetFileSystemEntries(_dir).Select(Path.GetFileName).Order()!];

    [Fact]
    public void Restore_puts_every_original_back_and_removes_renamed_files()
    {
        string kept = Write("01 - Song.flac", "original 1");
        string renamed = Write("02 - Song (feat. Guest).flac", "original 2");

        using (TagBackups backups = TagBackups.Of([kept, renamed], Log)!)
        {
            File.WriteAllText(kept, "tagged 1");
            string newName = Path.Combine(_dir, "02 - Song.flac");
            File.Move(renamed, newName);
            File.WriteAllText(newName, "tagged 2");

            backups.Restore([newName]);
        }

        Assert.Equal("original 1", File.ReadAllText(kept));
        Assert.Equal("original 2", File.ReadAllText(renamed));
        Assert.Equal(["01 - Song.flac", "02 - Song (feat. Guest).flac"], Names());
    }

    // The renamed file and the copy are the only two versions of that song until the copy is back.
    [Fact]
    public void A_restore_that_cannot_put_a_file_back_keeps_both_versions()
    {
        string original = Write("01 - Song (feat. Guest).flac", "original");
        string newName = Path.Combine(_dir, "01 - Song.flac");

        using (TagBackups backups = TagBackups.Of([original], Log)!)
        {
            File.Move(original, newName);
            File.WriteAllText(newName, "tagged");
            Directory.CreateDirectory(original);

            Assert.ThrowsAny<IOException>(() => backups.Restore([newName]));
        }

        Assert.Equal("tagged", File.ReadAllText(newName));
        Assert.Equal("original", File.ReadAllText(Path.Combine(_dir, ".01 - Song (feat. Guest).flac.sleezer-untagged")));
    }

    [Fact]
    public void Discard_removes_the_copies_and_keeps_the_files()
    {
        string file = Write("01 - Song.flac", "tagged");

        using (TagBackups backups = TagBackups.Of([file], Log)!)
        {
            Assert.Equal(2, Names().Length);
            backups.Discard();
        }

        Assert.Equal(["01 - Song.flac"], Names());
        Assert.Equal("tagged", File.ReadAllText(file));
    }

    // The first copy succeeds and the second can't be written, so the first must not be left behind.
    [Fact]
    public void Of_keeps_no_copy_when_a_later_copy_fails()
    {
        Write("01 - Song.flac", "one");
        string second = Write("02 - Song.flac", "two");
        string blocker = Path.Combine(_dir, ".02 - Song.flac.sleezer-untagged");
        Directory.CreateDirectory(blocker);

        TagBackups? backups = TagBackups.Of([Path.Combine(_dir, "01 - Song.flac"), second], Log);

        Assert.Null(backups);
        Assert.Equal([".02 - Song.flac.sleezer-untagged", "01 - Song.flac", "02 - Song.flac"], Names());
    }
}
