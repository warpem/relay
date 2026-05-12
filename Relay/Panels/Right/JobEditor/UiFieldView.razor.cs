using System.Reflection;
using Microsoft.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.UIFields;

namespace Relay.Panels.Right.JobEditor;

/// <summary>
/// A component that renders an appropriate form field for a job parameter.
/// </summary>
/// <remarks>
/// This component dynamically renders a form field based on the UI field attribute
/// attached to a job property. It handles binding between the job parameter value
/// and the field widget, and provides validation display.
/// 
/// The component delegates the actual rendering to specialized view components
/// for each field type (boolean, string, integer, enum, etc.).
/// </remarks>
public partial class UiFieldView
{
    /// <summary>
    /// Gets or sets the reflection property info for the job parameter.
    /// </summary>
    /// <remarks>
    /// This identifies which property of the job we're editing.
    /// </remarks>
    [Parameter]
    public required PropertyInfo Property { get; set; }

    /// <summary>
    /// Gets or sets the job being edited.
    /// </summary>
    [Parameter]
    public ReadOnlyJob Job { get; set; }

    /// <summary>
    /// Gets or sets the callback that will be invoked when the parameter value changes.
    /// </summary>
    /// <remarks>
    /// The callback provides both the property being changed and its new value.
    /// </remarks>
    [Parameter]
    public EventCallback<(PropertyInfo prop, object value)> ValueChanged { get; set; }

    /// <summary>
    /// Gets or sets the content for the field label.
    /// </summary>
    /// <remarks>
    /// This allows customizing the label display, including adding tooltips or
    /// other interactive elements.
    /// </remarks>
    [Parameter]
    public RenderFragment Label { get; set; }

    /// <summary>
    /// Gets or sets content to display next to the label, such as a favorite button.
    /// </summary>
    [Parameter]
    public RenderFragment FavoriteFragment { set; get; }

    /// <summary>
    /// Gets or sets extra content rendered inline next to the label text (e.g. exposure toggle).
    /// </summary>
    [Parameter]
    public RenderFragment LabelExtra { get; set; }

    /// <summary>
    /// Optional override for the label text. When set, displayed instead of the UiField label.
    /// </summary>
    [Parameter]
    public string CustomLabel { get; set; }

    /// <summary>
    /// Gets or sets the validation error message to display for this field.
    /// </summary>
    [Parameter]
    public string ErrorMessage { set; get; } = string.Empty;

    /// <summary>
    /// When true, the static label is hidden (e.g. replaced by an editable field in LabelExtra).
    /// </summary>
    [Parameter]
    public bool HideLabel { get; set; }

    /// <summary>
    /// Gets or sets whether this field should be rendered in a disabled/read-only state.
    /// </summary>
    /// <remarks>
    /// When true, the field will disable all interactive elements while maintaining
    /// the same visual layout as the editable version.
    /// </remarks>
    [Parameter]
    public bool IsDisabled { get; set; } = false;

    /// <summary>
    /// A unique identifier for this field instance, used for ARIA labeling.
    /// </summary>
    private Guid UniqueId = Guid.NewGuid();

    /// <summary>
    /// Gets the CSS class for error message display.
    /// </summary>
    /// <remarks>
    /// When there's no error, the message is hidden with d-none.
    /// Otherwise, it uses error styling.
    /// </remarks>
    private string ErrorMessageCssClass => $"{(!string.IsNullOrWhiteSpace(ErrorMessage) ? " color-error-500 font-error" : "d-none")}";

    /// <summary>
    /// The UI field attribute for the job parameter.
    /// </summary>
    private UiFieldBase _uiField;

    /// <summary>
    /// Common CSS classes applied to all input fields.
    /// </summary>
    private const string CssInputClasses = "ui-field";

    /// <summary>
    /// Updates the UI field when parameters change.
    /// </summary>
    protected override void OnParametersSet()
    {
        if(!Equals(_uiField, GetUiField()))
            _uiField = GetUiField();

        base.OnParametersSet();
    }

    /// <summary>
    /// Gets the current value of the job parameter.
    /// </summary>
    private object GetValue => Job.GetParameterValue(Property);

    /// <summary>
    /// Gets the default value for the job parameter, defined in the job type.
    /// </summary>
    private object GetDefaultValue => Refund.DataModel.Job.DefaultValues[Job.GetOriginalType()][Property.Name];

    private object GetAdditionalData => Refund.DataModel.Job.TypeUiFields[Job.GetOriginalType()][Property].DataDelegate?.Invoke(Job);

    /// <summary>
    /// Handles a value change from the field widget and propagates it to the parent component.
    /// </summary>
    /// <param name="value">The new value entered by the user</param>
    /// <remarks>
    /// This is invoked by the specific UI field view component when the user
    /// interacts with the field control.
    /// </remarks>
    private async Task HandleValueChanged(object value)
    {
        await ValueChanged.InvokeAsync((Property, value));
    }

    /// <summary>
    /// Gets the UI field attribute for the current property.
    /// </summary>
    /// <returns>The UI field attribute, or null if not found</returns>
    /// <remarks>
    /// The UI field attribute determines what type of control to render
    /// (text field, dropdown, checkbox, etc.) and its configuration.
    /// </remarks>
    private UiFieldBase GetUiField() => Refund.DataModel.Job.TypeUiFields[Job.GetOriginalType()].ContainsKey(Property) ?
                                        Refund.DataModel.Job.TypeUiFields[Job.GetOriginalType()][Property] :
                                        null;
}