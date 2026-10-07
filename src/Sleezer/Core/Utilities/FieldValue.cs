using System.Text.Json;

namespace NzbDrone.Plugin.Sleezer.Core.Utilities
{
    public static class FieldValue
    {
        /// <summary>Lidarr hands API field values over as JsonElement, which Convert.ToBoolean rejects.</summary>
        public static bool ToBool(object? value) => value switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.String } e => bool.TryParse(e.GetString(), out bool parsed) && parsed,
            JsonElement => false,
            _ => bool.TryParse(value?.ToString(), out bool parsed) && parsed,
        };
    }
}
