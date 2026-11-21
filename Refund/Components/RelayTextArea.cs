using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Components;

/// <summary>Applies Relay's empty-text normalization to multiline Fluent inputs.</summary>
public class RelayTextArea(LibraryConfiguration configuration) : FluentTextArea(configuration)
{
    public override Task SetParametersAsync(ParameterView parameters) =>
        base.SetParametersAsync(TextInputParameters.Normalize(this, parameters));
}
