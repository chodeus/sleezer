using System.Reflection;
using System.Runtime.Loader;

namespace Sleezer.SmokeLoad;

// Usage: SmokeLoad <plugin-dll-path> <lidarr-runtime-dir>
//
//   plugin-dll-path    The packaged Lidarr.Plugin.Sleezer.dll, extracted from
//                      the release zip. This is the file Lidarr installs.
//
//   lidarr-runtime-dir A directory containing the Lidarr-host-provided
//                      assemblies (Lidarr.Common.dll, Lidarr.Core.dll, etc.)
//                      and whatever else Lidarr's net8.0 runtime ships. In
//                      practice we point at ext/Lidarr's build output.
//
// What this simulates:
//   Lidarr installs the plugin zip into /config/plugins/<owner>/<name>/ and
//   loads it into its own process. Its DryIoc container calls
//   Assembly.GetTypes() to discover injectable services (Registrator.
//   RegisterMany in Container.cs:8023). If any type in the plugin references
//   an assembly the host runtime cannot resolve, GetTypes() throws
//   ReflectionTypeLoadException, DryIoc swallows it, and the plugin
//   silently disappears from the UI.
//
// What this does:
//   Loads the plugin into an AssemblyLoadContext whose resolver can ONLY
//   see (a) BCL assemblies that ship with the CI runner's .NET 8 runtime,
//   and (b) assemblies in lidarr-runtime-dir. Any BCL version mismatch
//   (the v1.1.1 incident: plugin wanted System.IO.Pipelines 10, host has 8)
//   fails the load, exit 1, CI red.
//
// Exit codes: 0 = plugin loads cleanly, 1 = load or GetTypes() failed.

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: SmokeLoad <plugin-dll> <lidarr-runtime-dir>");
            return 2;
        }

        string pluginPath = Path.GetFullPath(args[0]);
        string hostDir = Path.GetFullPath(args[1]);

        if (!File.Exists(pluginPath))
        {
            Console.Error.WriteLine($"FAIL: plugin DLL not found: {pluginPath}");
            return 1;
        }
        if (!Directory.Exists(hostDir))
        {
            Console.Error.WriteLine($"FAIL: host runtime dir not found: {hostDir}");
            return 1;
        }

        Console.WriteLine($"plugin: {pluginPath}");
        Console.WriteLine($"host:   {hostDir}");

        AssemblyLoadContext ctx = new("PluginSmoke", isCollectible: false);
        ctx.Resolving += (c, name) =>
        {
            string candidate = Path.Combine(hostDir, name.Name + ".dll");
            if (File.Exists(candidate))
                return c.LoadFromAssemblyPath(candidate);

            // Fall through to default context (CI runner's BCL). Returning
            // null lets .NET keep searching; if nothing resolves, it throws.
            return null;
        };

        Assembly asm;
        try
        {
            asm = ctx.LoadFromAssemblyPath(pluginPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL: LoadFromAssemblyPath threw: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        try
        {
            Type[] types = asm.GetTypes();
            Console.WriteLine($"OK: loaded {types.Length} types from {asm.FullName}");
            return 0;
        }
        catch (ReflectionTypeLoadException ex)
        {
            Console.Error.WriteLine("FAIL: GetTypes() threw ReflectionTypeLoadException");
            Console.Error.WriteLine("Loader exceptions:");
            HashSet<string> seen = [];
            foreach (Exception? le in ex.LoaderExceptions ?? Array.Empty<Exception?>())
            {
                if (le == null)
                    continue;
                string msg = $"  - {le.GetType().Name}: {le.Message}";
                if (seen.Add(msg))
                    Console.Error.WriteLine(msg);
            }
            return 1;
        }
    }
}
