using Lidarr.Http.ClientSchema;
using NzbDrone.Core.ThingiProvider;
using System.Reflection;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public static class DynamicSchemaInjector
    {
        private static readonly FieldInfo _mappingsField = typeof(SchemaBuilder).GetField("_mappings", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("SchemaBuilder._mappings field not found. Lidarr internals may have changed.");

        public static void InjectDynamic<TSettings>(IEnumerable<FieldMapping> dynamicMappings, string dynamicPrefix)
            where TSettings : IProviderConfig
        {
            Dictionary<Type, FieldMapping[]> dict = (Dictionary<Type, FieldMapping[]>)_mappingsField.GetValue(null)!;
            lock (dict)
            {
                if (!dict.TryGetValue(typeof(TSettings), out FieldMapping[]? existing))
                {
                    SchemaBuilder.ToSchema(Activator.CreateInstance<TSettings>());
                    dict.TryGetValue(typeof(TSettings), out existing);
                }

                FieldMapping[] staticMappings = existing?
                    .Where(m => !m.Field.Name.StartsWith(dynamicPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToArray() ?? [];

                dict[typeof(TSettings)] = [.. staticMappings, .. dynamicMappings];
            }
        }
    }
}
