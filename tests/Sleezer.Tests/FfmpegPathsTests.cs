using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

public sealed class FfmpegPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ffmpeg-paths-{Guid.NewGuid():N}")).FullName;

    [Fact]
    public void A_path_without_a_quote_is_handed_over_as_it_is()
    {
        string track = Path.Combine(_root, "01 - Heroes.flac");
        File.WriteAllText(track, "audio");

        using (FfmpegInput input = FfmpegPaths.Input(track))
            Assert.Equal(track, input.Path);

        Assert.True(File.Exists(track));
        Assert.Equal(Path.ChangeExtension(track, ".converted.mp3"), FfmpegPaths.TempOutput(track, ".converted.mp3"));
    }

    [Fact]
    public void A_quoted_path_reaches_ffmpeg_through_a_quote_free_alias_that_is_removed_after()
    {
        string track = Path.Combine(_root, "03 - \"Heroes\".flac");
        File.WriteAllText(track, "audio");

        string alias;
        using (FfmpegInput input = FfmpegPaths.Input(track))
        {
            alias = input.Path;
            Assert.DoesNotContain('"', alias);
            Assert.Equal(".flac", Path.GetExtension(alias));
            Assert.Equal("audio", File.ReadAllText(alias));
        }

        Assert.False(File.Exists(alias));
        Assert.Equal("audio", File.ReadAllText(track));
    }

    [Fact]
    public void A_quoted_file_writes_its_temporary_output_beside_it_without_the_quote()
    {
        string track = Path.Combine(_root, "03 - \"Heroes\".flac");

        string output = FfmpegPaths.TempOutput(track, ".converted.mp3");

        Assert.Equal(_root, Path.GetDirectoryName(output));
        Assert.DoesNotContain('"', Path.GetFileName(output));
        Assert.EndsWith(".converted.mp3", output);
    }

    [Fact]
    public void A_relative_quoted_path_links_to_the_file_it_names()
    {
        string track = Path.Combine(_root, "03 - \"Heroes\".flac");
        File.WriteAllText(track, "audio");

        using FfmpegInput input = FfmpegPaths.Input(Path.GetRelativePath(Environment.CurrentDirectory, track));

        Assert.Equal("audio", File.ReadAllText(input.Path));
    }

    [Theory]
    [InlineData("\"Heroes\"/03 - Heroes.flac", true)]
    [InlineData("Heroes/03 - \"Heroes\".flac", false)]
    public void A_folder_holding_a_quote_is_recognised(string relativeTrack, bool holdsQuote)
    {
        Assert.Equal(holdsQuote, FfmpegPaths.FolderHoldsQuote(Path.Combine(_root, relativeTrack)));
    }

    [Fact]
    public void A_quoted_folder_is_refused_before_anything_is_written()
    {
        Assert.Throws<InvalidOperationException>(() => FfmpegPaths.TempOutput(Path.Combine(_root, "\"Heroes\"", "03 - Heroes.flac"), ".converted.mp3"));
    }

    [Fact]
    public void Curly_quotes_need_no_alias()
    {
        string track = Path.Combine(_root, "03 - \u201cHeroes\u201d.flac");
        File.WriteAllText(track, "audio");

        using FfmpegInput input = FfmpegPaths.Input(track);

        Assert.Equal(track, input.Path);
    }

    [Fact]
    public void A_file_in_a_quoted_folder_is_still_read_through_an_alias()
    {
        string track = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "\"Heroes\"")).FullName, "03 - Heroes.flac");
        File.WriteAllText(track, "audio");

        using FfmpegInput input = FfmpegPaths.Input(track);

        Assert.DoesNotContain('"', input.Path);
        Assert.Equal("audio", File.ReadAllText(input.Path));
    }

    [Fact]
    public async Task Readers_of_one_quoted_file_at_once_each_get_and_remove_their_own_alias()
    {
        string track = Path.Combine(_root, "03 - \"Heroes\".flac");
        File.WriteAllText(track, "audio");

        string[] aliases = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            using FfmpegInput input = FfmpegPaths.Input(track);
            Assert.Equal("audio", File.ReadAllText(input.Path));
            return input.Path;
        })));

        Assert.Equal(32, aliases.Distinct().Count());
        Assert.All(aliases, alias => Assert.False(File.Exists(alias)));
        Assert.Equal("audio", File.ReadAllText(track));
    }

    [Fact]
    public void A_quoted_files_leftover_temp_output_is_found_by_the_next_attempt()
    {
        string track = Path.Combine(_root, "03 - \"Heroes\".flac");

        Assert.Equal(FfmpegPaths.TempOutput(track, ".converted.mp3"), FfmpegPaths.TempOutput(track, ".converted.mp3"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
