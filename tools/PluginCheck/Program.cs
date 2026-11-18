using Sleezer.PluginCheck;

// Usage: Sleezer.PluginCheck <plugin.dll> <lidarr-bin-dir>. Lists each type or member the plugin uses from Lidarr's bin dir that the build lacks,
// each host abstract member a plugin type no longer implements, and each override left with no base member (see Checker.SameType).
if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: Sleezer.PluginCheck <plugin.dll> <lidarr-bin-dir>");
    return 2;
}

var checker = new Checker(args[0], args[1]);
var problems = checker.Run();

Console.WriteLine($"checked {checker.TypeRefs} host type refs, {checker.MemberRefs} member refs, {checker.RequiredMembers} required members, {checker.Overrides} overrides against {args[1]}");
foreach (var p in problems)
{
    Console.WriteLine(p);
}

Console.WriteLine(problems.Count == 0 ? "RESULT: compatible" : $"RESULT: {problems.Count} problem(s)");
return problems.Count == 0 ? 0 : 1;
