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
    public void Restore_puts_every_original_back()
    {
        string first = Write("01 - Song.flac", "original 1");
        string second = Write("02 - Song.flac", "original 2");

        using (TagBackups backups = TagBackups.Of([first, second], Log)!)
        {
            File.WriteAllText(first, "tagged 1");
            File.WriteAllText(second, "tagged 2");

            backups.Restore();
        }

        Assert.Equal("original 1", File.ReadAllText(first));
        Assert.Equal("original 2", File.ReadAllText(second));
        Assert.Equal(["01 - Song.flac", "02 - Song.flac"], Names());
    }

    // A file that can't go back keeps its copy, the only original left of it, and the rest still go back.
    [Fact]
    public void A_restore_that_cannot_put_a_file_back_keeps_its_copy_and_restores_the_rest()
    {
        string stuck = Write("01 - Song.flac", "original 1");
        string other = Write("02 - Song.flac", "original 2");

        using (TagBackups backups = TagBackups.Of([stuck, other], Log)!)
        {
            File.Delete(stuck);
            Directory.CreateDirectory(stuck);
            File.WriteAllText(other, "tagged 2");

            Assert.Throws<TagRestoreException>(backups.Restore);
        }

        Assert.Equal("original 2", File.ReadAllText(other));
        Assert.Equal("original 1", File.ReadAllText(Assert.Single(Backups())));
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
