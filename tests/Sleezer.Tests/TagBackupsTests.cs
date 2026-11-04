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

    private string[] Names() => [.. Directory.GetFiles(_dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private string[] Backups() => Directory.GetFiles(_dir, "*.sleezer-untagged");

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

    // File one is renamed away, freeing its name; file two is then renamed onto that name with other letter case.
    [Fact]
    public void Restore_keeps_an_original_whose_name_differs_from_a_rename_only_by_case()
    {
        string first = Write("01 - beta.flac", "original 1");
        string second = Write("zz.flac", "original 2");

        using (TagBackups backups = TagBackups.Of([first, second], Log)!)
        {
            string firstRenamed = Path.Combine(_dir, "02 - Alpha.flac");
            string secondRenamed = Path.Combine(_dir, "01 - Beta.flac");
            File.Move(first, firstRenamed);
            File.Move(second, secondRenamed);

            backups.Restore([firstRenamed, secondRenamed]);
        }

        Assert.Equal(["01 - beta.flac", "zz.flac"], Names());
        Assert.Equal("original 1", File.ReadAllText(first));
        Assert.Equal("original 2", File.ReadAllText(second));
    }

    // When a file can't go back, its copy is the only original left.
    [Fact]
    public void A_restore_that_cannot_put_a_file_back_keeps_its_copy()
    {
        string original = Write("01 - Song (feat. Guest).flac", "original");

        using (TagBackups backups = TagBackups.Of([original], Log)!)
        {
            string newName = Path.Combine(_dir, "01 - Song.flac");
            File.Move(original, newName);
            File.WriteAllText(newName, "tagged");
            Directory.CreateDirectory(original);

            Assert.ThrowsAny<IOException>(() => backups.Restore([newName]));
        }

        Assert.Equal("original", File.ReadAllText(Assert.Single(Backups())));
    }

    [Fact]
    public void Discard_removes_the_copies_and_keeps_the_files()
    {
        string file = Write("01 - Song.flac", "tagged");

        using (TagBackups backups = TagBackups.Of([file], Log)!)
        {
            Assert.Single(Backups());
            backups.Discard();
        }

        Assert.Equal(["01 - Song.flac"], Names());
        Assert.Equal("tagged", File.ReadAllText(file));
    }

    [Fact]
    public void Of_keeps_no_copy_when_a_later_copy_fails()
    {
        string first = Write("01 - Song.flac", "one");

        TagBackups? backups = TagBackups.Of([first, Path.Combine(_dir, "02 - Missing.flac")], Log);

        Assert.Null(backups);
        Assert.Equal(["01 - Song.flac"], Names());
    }

    // A name at the filesystem's 255 limit leaves no room for a suffix.
    [Fact]
    public void Of_backs_up_a_file_whose_name_is_at_the_limit()
    {
        string file = Write(new string('a', 250) + ".flac", "one");

        using TagBackups? backups = TagBackups.Of([file], Log);

        Assert.NotNull(backups);
        backups!.Discard();
    }
}
