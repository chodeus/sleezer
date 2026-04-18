using NzbDrone.Core.IndexerSearch.Definitions;
using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;

namespace NzbDrone.Plugin.Sleezer.Indexers.Soulseek.Search.Templates;

public static partial class TemplateEngine
{
    private static readonly Regex PlaceholderRegex = CreatePlaceholderRegex();
    private static readonly Regex IndexerRegex = CreateIndexerRegex();
    private static readonly Type SearchCriteriaType = typeof(AlbumSearchCriteria);

    private static readonly ConcurrentDictionary<(Type Type, string Name), PropertyInfo?> PropertyCache = new();
    private static readonly ConcurrentDictionary<(Type Type, string Name), FieldInfo?> FieldCache = new();

    public static IReadOnlyList<string> ParseTemplates(string? templateConfig)
    {
        if (string.IsNullOrWhiteSpace(templateConfig))
            return [];

        return templateConfig
            .Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#') && !l.StartsWith("//"))
            .ToList()
            .AsReadOnly();
    }

    public static IReadOnlyList<string> ValidateTemplates(string? templateConfig)
    {
        List<string> errors = new List<string>();
        IReadOnlyList<string> templates = ParseTemplates(templateConfig);

        for (int i = 0; i < templates.Count; i++)
        {
            MatchCollection matches = PlaceholderRegex.Matches(templates[i]);
            if (matches.Count == 0)
            {
                errors.Add($"Line {i + 1}: No placeholders found (use {{{{Property}}}})");
                continue;
            }

            foreach (Match match in matches)
            {
                string path = match.Groups[1].Value.Trim();
                string? error = ValidatePath(path);
                if (error != null)
                    errors.Add($"Line {i + 1}: '{path}' - {error}");
            }
        }

        return errors.AsReadOnly();
    }

    private static string? ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "empty path";

        Type? currentType = SearchCriteriaType;
        foreach (string segment in path.Split('.').Take(3))
        {
            if (currentType == null)
                return "cannot resolve deeper";

            Match idx = IndexerRegex.Match(segment);
            string propName = idx.Success ? idx.Groups[1].Value : segment;

            PropertyInfo? prop = GetCachedProperty(currentType, propName);
            if (prop == null)
                return $"unknown property '{propName}'";

            currentType = prop.PropertyType;

            // Unwrap Lazy<T>, List<T>, etc.
            if (currentType.IsGenericType)
            {
                Type genDef = currentType.GetGenericTypeDefinition();
                if (genDef == typeof(Lazy<>) || genDef == typeof(List<>) || genDef == typeof(IList<>) || genDef == typeof(IEnumerable<>))
                    currentType = currentType.GetGenericArguments()[0];
            }
        }

        return null;
    }

    public static string? Apply(string template, object? context)
    {
        if (string.IsNullOrWhiteSpace(template) || context == null)
            return null;

        bool hasUnresolved = false;

        string result = PlaceholderRegex.Replace(template, match =>
        {
            object? value = ResolvePath(context, match.Groups[1].Value.Trim());

            if (value == null || (value is string s && string.IsNullOrWhiteSpace(s)))
            {
                hasUnresolved = true;
                return string.Empty;
            }

            return value.ToString() ?? string.Empty;
        });

        if (hasUnresolved)
            return null;

        result = MultiSpaceRegex().Replace(result, " ").Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static object? ResolvePath(object? obj, string path)
    {
        if (obj == null || string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            object? current = obj;
            foreach (string segment in path.Split('.').Take(3))
            {
                if (current == null) break;

                Match idx = IndexerRegex.Match(segment);
                if (idx.Success)
                {
                    current = GetValue(current, idx.Groups[1].Value);
                    current = GetIndexed(current, int.Parse(idx.Groups[2].Value));
                }
                else
                {
                    current = GetValue(current, segment);
                }
            }
            return current;
        }
        catch { return null; }
    }

    private static object? GetValue(object obj, string name)
    {
        Type type = obj.GetType();
        PropertyInfo? prop = GetCachedProperty(type, name);
        object? value = prop?.GetValue(obj) ?? GetCachedField(type, name)?.GetValue(obj);

        // Unwrap Lazy<T>
        if (value?.GetType() is { IsGenericType: true } t && t.GetGenericTypeDefinition() == typeof(Lazy<>))
            value = GetCachedProperty(t, "Value")?.GetValue(value);

        return value;
    }

    private static object? GetIndexed(object? col, int idx) => col switch
    {
        null => null,
        IList list => idx < list.Count ? list[idx] : null,
        IEnumerable e => e.Cast<object>().ElementAtOrDefault(idx),
        _ => null
    };

    private static PropertyInfo? GetCachedProperty(Type type, string name)
        => PropertyCache.GetOrAdd((type, name), key =>
            key.Type.GetProperty(key.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));

    private static FieldInfo? GetCachedField(Type type, string name)
        => FieldCache.GetOrAdd((type, name), key =>
            key.Type.GetField(key.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));

    public static void ClearCaches()
    {
        PropertyCache.Clear();
        FieldCache.Clear();
    }

    [GeneratedRegex(@"\{\{([^}]+)\}\}", RegexOptions.Compiled)]
    private static partial Regex CreatePlaceholderRegex();

    [GeneratedRegex(@"^(\w+)\[(\d+)\]$", RegexOptions.Compiled)]
    private static partial Regex CreateIndexerRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
