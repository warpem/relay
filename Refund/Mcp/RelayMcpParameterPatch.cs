using System.Reflection;
using System.Text.Json;
using Refund.DataModel;

namespace Refund.Mcp;

/// <summary>
/// Translates a declarative { parameterName: jsonValue } patch into validated property assignments
/// against a concrete Job type. Validation and coercion happen for every entry before any value is
/// returned, so callers can apply the result atomically (no partial mutation on error).
/// </summary>
public static class RelayMcpParameterPatch
{
    public static IReadOnlyList<(PropertyInfo Prop, object? Value)> Resolve(
        Type jobType, IReadOnlyDictionary<string, JsonElement> patch)
    {
        if (!Job.TypeParameters.TryGetValue(jobType, out var props))
            throw new ArgumentException($"Type '{jobType.Name}' has no settable parameters.");

        var byName = props.ToDictionary(p => p.Name, p => p, StringComparer.Ordinal);
        var result = new List<(PropertyInfo, object?)>(patch.Count);

        foreach (var (name, raw) in patch)
        {
            if (!byName.TryGetValue(name, out var prop))
                throw new ArgumentException(
                    $"Unknown parameter '{name}' for {jobType.Name}. Valid parameters: {string.Join(", ", byName.Keys.OrderBy(k => k))}.");

            object? value;
            try { value = CoerceJsonValue(raw, prop.PropertyType); }
            catch (Exception ex)
            {
                throw new ArgumentException(
                    $"Cannot set '{name}' ({prop.PropertyType.Name}): {ex.Message}");
            }
            result.Add((prop, value));
        }

        return result;
    }

    public static object? CoerceJsonValue(JsonElement value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            return CoerceJsonValue(value, underlying);
        }

        if (targetType.IsEnum)
            return Enum.Parse(targetType, value.GetString()
                ?? throw new ArgumentException("expected an enum name string"), ignoreCase: true);

        if (targetType == typeof(string)) return value.GetString();
        if (targetType == typeof(bool)) return value.GetBoolean();
        if (targetType == typeof(int)) return value.GetInt32();
        if (targetType == typeof(long)) return value.GetInt64();
        if (targetType == typeof(float)) return value.GetSingle();
        if (targetType == typeof(double)) return value.GetDouble();
        if (targetType == typeof(decimal)) return value.GetDecimal();

        throw new ArgumentException($"unsupported parameter type {targetType.Name}");
    }
}
