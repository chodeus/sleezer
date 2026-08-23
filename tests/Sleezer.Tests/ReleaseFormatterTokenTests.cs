using System.Collections.Generic;
using System.Reflection;
using NzbDrone.Plugin.Sleezer.Core.Utilities;
using Xunit;

namespace Sleezer.Tests;

// A live SubSonic grab landed as "01 -  -  [] [].flac" because the naming format used
// {Track CleanTitle:100} and friends: the :N max-length modifier was part of the lookup
// key, so every such token missed and rendered empty. Unknown tokens are silently blank,
// which is what made it invisible.
public class ReleaseFormatterTokenTests
{
    private static string Replace(string pattern, Dictionary<string, System.Func<string>> handlers)
    {
        MethodInfo m = typeof(ReleaseFormatter).GetMethod("ReplaceTokens",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, [pattern, handlers])!;
    }

    private static Dictionary<string, System.Func<string>> Handlers(params (string key, string value)[] pairs)
    {
        var d = new Dictionary<string, System.Func<string>>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = () => v;
        return d;
    }

    [Fact]
    public void Applies_the_max_length_modifier()
    {
        var h = Handlers(("{Album CleanTitle}", "A Very Long Album Title Indeed"));
        Assert.Equal("A Very", Replace("{Album CleanTitle:6}", h));
    }

    [Fact]
    public void Leaves_a_value_shorter_than_the_limit_alone()
    {
        var h = Handlers(("{Track CleanTitle}", "Lanterns"));
        Assert.Equal("Lanterns", Replace("{Track CleanTitle:100}", h));
    }

    // {track:00} and {medium:00} contain a colon that is part of the token name, so the
    // exact lookup has to win before the :N handling is tried.
    [Theory]
    [InlineData("{track:00}", "01")]
    [InlineData("{medium:00}", "02")]
    public void Does_not_mistake_a_padded_number_token_for_a_length_modifier(string token, string expected)
    {
        var h = Handlers(("{track:00}", "01"), ("{medium:00}", "02"));
        Assert.Equal(expected, Replace(token, h));
    }

    [Fact]
    public void Renders_the_real_world_format_that_produced_the_broken_filename()
    {
        var h = Handlers(
            ("{track:00}", "01"),
            ("{Track ArtistCleanNameThe}", "Birds of Tokyo"),
            ("{Track CleanTitle}", "Lanterns"));

        string actual = Replace("{track:00} - {Track ArtistCleanNameThe:100} - {Track CleanTitle:100}", h);
        Assert.Equal("01 - Birds of Tokyo - Lanterns", actual);
    }

    [Fact]
    public void Still_blanks_a_token_nobody_implements()
    {
        Assert.Equal("[]", Replace("[{Quality Title}]", Handlers()));
    }
}
