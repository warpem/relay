using System.Text.Json.Nodes;
using Serilog;

namespace Refund.Utils;

/// <summary>
/// Provides utility methods for working with JSON data, particularly for comparing JSON structures.
/// </summary>
public static class JsonUtils
{
    /// <summary>
    /// Compares two JSON nodes and reports differences between them.
    /// </summary>
    /// <param name="node1">The first JSON node to compare.</param>
    /// <param name="node2">The second JSON node to compare.</param>
    /// <returns>
    /// Returns true if the nodes are different, false if they are identical in structure and values.
    /// Differences are logged at Debug level.
    /// </returns>
    /// <remarks>
    /// This method performs a deep comparison of JSON structures, reporting any differences
    /// in properties, values, or structure. The comparison is not type-sensitive and converts
    /// values to strings before comparison.
    /// </remarks>
    public static bool CompareNodes(JsonNode node1, JsonNode node2) => CompareNodes(node1, node2, "");
    
    /// <summary>
    /// Internal recursive implementation of node comparison with path tracking.
    /// </summary>
    /// <param name="node1">The first JSON node to compare.</param>
    /// <param name="node2">The second JSON node to compare.</param>
    /// <param name="path">The current path in the JSON tree, used for reporting differences.</param>
    /// <returns>Returns true if the nodes are different, false if they are identical.</returns>
    /// <remarks>
    /// This method builds a dot-notation path (e.g., "user.address.street") to identify 
    /// where differences occur in the JSON structure. The differences are logged at Debug level
    /// with the path and the values that differ.
    /// 
    /// There are three types of differences reported:
    /// 1. Property exists in first object but not in second (marked as "removed")
    /// 2. Property exists in second object but not in first (marked as "added")
    /// 3. Property exists in both but values differ (shows both values)
    /// </remarks>
    static bool CompareNodes(JsonNode node1, JsonNode node2, string path)
    {
        bool IsDifferent = false;
        
        if (node1 is JsonObject obj1 && node2 is JsonObject obj2)
        {
            foreach (var property in obj1)
            {
                string CurrentPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";

                if (obj2.ContainsKey(property.Key))
                    IsDifferent |= CompareNodes(property.Value, obj2[property.Key], CurrentPath);
                else
                {
                    Log.ForContext("SourceContext", "Refund.Utils.JsonUtils").Debug("JSON property removed: {Path} value was {Value}", CurrentPath, property.Value);
                    IsDifferent = true;
                }
            }

            foreach (var property in obj2)
            {
                if (!obj1.ContainsKey(property.Key))
                {
                    string CurrentPath = string.IsNullOrEmpty(path) ? property.Key : $"{path}.{property.Key}";
                    Log.ForContext("SourceContext", "Refund.Utils.JsonUtils").Debug("JSON property added: {Path} new value {Value}", CurrentPath, property.Value);
                    IsDifferent = true;
                }
            }
        }
        else if (node1 is JsonValue value1 && node2 is JsonValue value2)
        {
            if (!value1.ToString().Equals(value2.ToString()))
            {
                Log.ForContext("SourceContext", "Refund.Utils.JsonUtils").Debug("JSON value changed: {Path} from {OldValue} to {NewValue}", path, value1, value2);
                IsDifferent = true;
            }
        }

        return IsDifferent;
    }
}