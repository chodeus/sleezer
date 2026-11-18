using Sleezer.PluginCheck;

// Usage: Sleezer.PluginCheck <plugin.dll> <lidarr-bin-dir>. Lists each Lidarr type or member the plugin uses that the build lacks,
// and each Lidarr interface or abstract member a plugin type no longer implements (Checker.SameType says what a signature match is).
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Sleezer.PluginCheck <plugin.dll> <lidarr-bin-dir>");
    return 2;
}

var checker = new Checker(args[0], args[1]);
var problems = checker.Run();

Console.WriteLine($"checked {checker.TypeRefs} Lidarr type refs, {checker.MemberRefs} member refs, {checker.RequiredMembers} required members against {args[1]}");
foreach (var p in problems)
{
    Console.WriteLine(p);
}

Console.WriteLine(problems.Count == 0 ? "RESULT: compatible" : $"RESULT: {problems.Count} problem(s)");
return problems.Count == 0 ? 0 : 1;
