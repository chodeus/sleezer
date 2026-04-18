using Lidarr.Http.ClientSchema;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;
using System.Text.Json;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public abstract class DynamicStateSettings : IProviderConfig
    {
        [FieldDefinition(0, Label = "States", Type = FieldType.Textbox, Hidden = HiddenType.Hidden)]
        public string StateJson { get; set; } = "{}";

        protected Dictionary<string, bool> GetAllBoolStates() =>
            JsonSerializer.Deserialize<Dictionary<string, bool>>(
                string.IsNullOrEmpty(StateJson) ? "{}" : StateJson) ?? [];

        public bool GetBoolState(string key) =>
            GetAllBoolStates().GetValueOrDefault(key);

        public void SetBoolState(string key, bool value)
        {
            Dictionary<string, bool> states = GetAllBoolStates();
            states[key] = value;
            StateJson = JsonSerializer.Serialize(states);
        }

        public abstract NzbDroneValidationResult Validate();

        public static FieldMapping[] BuildMappings<TSettings>(IEnumerable<DynamicFieldDefinition> fields)
            where TSettings : DynamicStateSettings
        {
            List<FieldMapping> mappings =
            [
                new()
                {
                    Field = new Field
                    {
                        Name   = "stateJson",
                        Label  = "States",
                        Type   = "textbox",
                        Hidden = "hidden",
                        Order  = 0,
                    },
                    PropertyType = typeof(string),
                    GetterFunc   = m => ((TSettings)m).StateJson,
                    SetterFunc   = (m, v) => ((TSettings)m).StateJson = v?.ToString() ?? "{}",
                }
            ];

            int order = 1;
            foreach (DynamicFieldDefinition def in fields)
            {
                string key = def.Key;

                mappings.Add(def.Type == "checkbox"
                    ? new FieldMapping
                    {
                        Field = new Field
                        {
                            Name = key,
                            Label = def.Label,
                            Type = "checkbox",
                            HelpText = def.HelpText,
                            Advanced = def.Advanced,
                            Order = order++,
                        },
                        PropertyType = typeof(bool),
                        GetterFunc = m => ((TSettings)m).GetBoolState(key),
                        SetterFunc = (m, v) => ((TSettings)m).SetBoolState(key, Convert.ToBoolean(v)),
                    }
                    : new FieldMapping
                    {
                        Field = new Field
                        {
                            Name = key,
                            Label = def.Label,
                            Type = def.Type,
                            HelpText = def.HelpText,
                            Advanced = def.Advanced,
                            Order = order++,
                        },
                        PropertyType = typeof(string),
                        GetterFunc = m => ((TSettings)m).GetAllBoolStates().TryGetValue(key, out bool v) ? v : string.Empty,
                        SetterFunc = (m, v) => ((TSettings)m).SetBoolState(key, Convert.ToBoolean(v)),
                    });
            }

            return [.. mappings];
        }
    }

    public record DynamicFieldDefinition(
        string Key,
        string Label,
        string Type = "checkbox",
        string? HelpText = null,
        bool Advanced = false);
}