using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.UIFields;

/// <summary>
/// Base component class for all UI field view components that render job parameters in the UI.
/// Provides common parameters and functionality needed by all field types, enabling consistent
/// editing experience and a clean separation between field data and its visual representation.
/// </summary>
public abstract class UiFieldViewBase : FluentComponentBase
{
    /// <summary>
    /// The field attribute metadata that describes how this field should be displayed
    /// </summary>
    [Parameter]
    public virtual UiFieldBase FieldDescription { get; set; }
    
    /// <summary>
    /// The current value of the field that should be displayed and can be edited
    /// </summary>
    [Parameter]
    public virtual object Value { get; set; }

    /// <summary>
    /// The default value to use when no value is provided or when resetting to defaults
    /// </summary>
    [Parameter]
    public virtual object DefaultValue { get; set; }
    
    /// <summary>
    /// Additional data that can be passed to the field view for rendering or processing
    /// </summary>
    [Parameter]
    public virtual object AdditionalData { get; set; }

    /// <summary>
    /// Event callback that should be triggered when the field value changes, propagating the change back to the parent
    /// </summary>
    [Parameter]
    public virtual EventCallback<object> ValueChanged { get; set; }

    /// <summary>
    /// Error message to display if the current value is invalid
    /// </summary>
    [Parameter]
    public virtual string Error { get; set; }

    /// <summary>
    /// Gets or sets whether this field should be rendered in a disabled/read-only state
    /// </summary>
    /// <remarks>
    /// When true, the field will disable all interactive elements while maintaining
    /// the same visual layout as the editable version.
    /// </remarks>
    [Parameter]
    public virtual bool IsDisabled { get; set; } = false;
}