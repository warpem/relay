using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Components;

/// <summary>Normalizes empty text before Fluent v5 captures its focused-value snapshot.</summary>
public class RelayTextInput(LibraryConfiguration configuration) : FluentTextInput(configuration)
{
    public override Task SetParametersAsync(ParameterView parameters) =>
        base.SetParametersAsync(TextInputParameters.Normalize(this, parameters));
}

internal static class TextInputParameters
{
    internal static ParameterView Normalize(object target, ParameterView parameters)
    {
        // Fluent uses null to mean "no frozen value yet". A null initial Value can therefore
        // change the rendered value during typing, defeating the focused-input protection.
        if (!parameters.TryGetValue<string>(nameof(RelayTextInput.Value), out var value) || value is not null)
            return parameters;

        // ParameterView.FromDictionary has no way to mark entries as cascading, so rebuilt views
        // would silently lose values such as the EditContext (and InputBase would later throw when
        // it appears). Assign cascading values straight to their properties instead.
        var normalized = new Dictionary<string, object?>();
        foreach (var p in parameters)
        {
            if (!p.Cascading)
                normalized[p.Name] = p.Value;
            else
                FindProperty(target.GetType(), p.Name)?.SetValue(target, p.Value);
        }
        normalized[nameof(RelayTextInput.Value)] = string.Empty;
        return ParameterView.FromDictionary(normalized);
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var property = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null) return property;
        }
        return null;
    }
}
