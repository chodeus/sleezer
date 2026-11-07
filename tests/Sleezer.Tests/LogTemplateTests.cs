using System.Text.RegularExpressions;
using Xunit;

namespace Sleezer.Tests;

// NLog quotes a string value itself, so a template's own quotes print it twice ('"Barrut"').
public partial class LogTemplateTests
{
    [Fact]
    public void No_log_template_quotes_a_value_itself()
    {
        string src = Path.Combine(RepoRoot(), "src");
        List<string> offenders = [];
        foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            offenders.AddRange(QuotedValues(text).Select(index => $"{Path.GetRelativePath(src, file)}:{LineOf(text, index)}"));
        }

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("logger.Info(\"Got '{Name}'\", name);", true)]
    [InlineData("_logger.Warn(ex, \"Got \\\"{Name}\\\" back\", name);", true)]
    [InlineData("logger.Debug(\"{Username}'s files\", user);", true)]
    [InlineData("logger.Info(\"Got {Name}\", name);", false)]
    [InlineData("logger.Info($\"Got '{name}'\");", false)]
    [InlineData("throw new Exception(\"Got '{Name}'\");", false)]
    public void The_scan_flags_only_a_template_that_quotes_a_value(string code, bool flagged)
    {
        Assert.Equal(flagged, QuotedValues(code).Any());
    }

    private static IEnumerable<int> QuotedValues(string code) =>
        TemplateRegex().Matches(code)
            .Where(call => QuotedValueRegex().IsMatch(call.Groups["template"].Value))
            .Select(call => call.Index);

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Sleezer.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Sleezer.sln not found above " + AppContext.BaseDirectory);
    }

    // The first plain string literal of a logger call, after an optional exception; $"..." renders as written.
    [GeneratedRegex("""\b\w*[Ll]og\w*\s*\.\s*(?:Trace|Debug|Info|Warn|Error|Fatal)\(\s*(?:\w+\s*,\s*)?"(?<template>(?:[^"\\\r\n]|\\.)*)\x22""")]
    private static partial Regex TemplateRegex();

    [GeneratedRegex("""'\{[A-Za-z@][\w.]*(?:[:,][^}]*)?\}'|\\"\{[A-Za-z@][\w.]*(?:[:,][^}]*)?\}\\"|\}'s\b""")]
    private static partial Regex QuotedValueRegex();
}
