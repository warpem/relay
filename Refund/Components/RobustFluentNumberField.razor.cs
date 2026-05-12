using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace Refund.Components;

public partial class RobustFluentNumberField<TValue> where TValue : new()
{
    [Parameter] public TValue Value { get; set; }
    [Parameter] public EventCallback<TValue> ValueChanged { get; set; }
    [Parameter] public string? Class { get; set; }
    [Parameter] public string? Style { get; set; }
    [Parameter] public string? Placeholder { get; set; }
    [Parameter] public string? Min { get; set; }
    [Parameter] public string? Max { get; set; }
    [Parameter] public string? Step { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool Immediate { get; set; }
    [Parameter] public int ImmediateDelay { get; set; }
    [Parameter] public string? AutoComplete { get; set; }

    private string ValueAsString => Value switch
    {
        null => "",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Value.ToString() ?? ""
    };
}
