using System.Text.RegularExpressions;
using Xunit;

namespace Sleezer.Tests;

// Settings help is read in a narrow column under each field: one short sentence, not a manual.
public partial class SettingsHelpTextTests
{
    private const int MaxWords = 25;

    [Fact]
    public void No_settings_help_runs_past_a_short_sentence()
    {
        string src = Path.Combine(RepoRoot(), "src");
        List<string> tooLong = [];
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            tooLong.AddRange(LongHelp(text).Select(help => $"{Path.GetRelativePath(src, file)}: {help}"));
        }

        Assert.True(tooLong.Count == 0, string.Join(Environment.NewLine, tooLong));
    }

    [Theory]
    [InlineData("HelpText = \"{0}\"", MaxWords, false)]
    [InlineData("HelpText = \"{0}\"", MaxWords + 1, true)]
    [InlineData("HelpTextWarning = \"{0}\"", MaxWords + 1, true)]
    [InlineData("public const string StrictMatchingHelp = \"{0}\";", MaxWords + 1, true)]
    [InlineData("Label = \"{0}\"", MaxWords + 1, false)]
    public void The_scan_flags_help_longer_than_the_limit(string shape, int words, bool flagged)
    {
        string code = string.Format(shape, string.Join(' ', Enumerable.Repeat("word", words)));
        Assert.Equal(flagged, LongHelp(code).Any());
    }

    private static IEnumerable<string> LongHelp(string code) =>
        HelpRegex().Matches(code)
            .Select(m => m.Groups["text"].Value)
            .Where(help => help.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxWords);

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Sleezer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Sleezer.sln not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex("""(?:\bHelpText(?:Warning)?\s*=|const\s+string\s+\w*Help\w*\s*=)\s*"(?<text>(?:[^"\\\r\n]|\\.)*)\x22""")]
    private static partial Regex HelpRegex();
}
