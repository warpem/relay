using Refund.DataModel.ReadOnly;

namespace Refund.UIFields;

/// <summary>
/// Base attribute class that indicates a property should be displayed as an editable field in the job parameter editor.
/// This provides the foundation for different field types (bool, string, int, etc.) and handles common presentation concerns.
/// Field attributes enable a declarative way to define job parameters with rich UI integration.
/// </summary>
/// <remarks>
/// Key implementation insights:
/// 1. Functions as the foundation for all UI field types in the system
/// 2. Used in the UiFieldView Blazor component to render fields dynamically
/// 3. Commonly extended by specialized field types (UiPath, UiSymmetry, etc.)
/// 4. Provides conditional display logic through the ConditionalOnField and ConditionalOnValue properties
/// 5. Referenced in AttributeUtils for reflection-based operations on job parameters
/// 
/// The core idea is declarative UI generation - attaching metadata to properties that determine how 
/// they should be displayed, validated, and processed in the job parameter editor interface.
/// </remarks>
public abstract class UiFieldBase : Attribute
{
    /// <summary>
    /// The command-line name used when generating command arguments for job execution.
    /// Examples: "iter" for iterations, "sym" for symmetry, "K" for class number.
    /// </summary>
    public string CliName;
    
    /// <summary>
    /// The human-readable label displayed in the UI.
    /// Specialized field types like UiPath may extend this with additional information.
    /// </summary>
    public string Label;
    
    /// <summary>
    /// Tooltip text providing more detailed information about the field.
    /// Critical for communicating parameter implications to users, especially for complex
    /// cryo-EM processing parameters.
    /// </summary>
    public string HelpText;
    
    /// <summary>
    /// Indicates if this field should be shown only in advanced mode (hidden by default).
    /// Used for parameters that most users don't need to modify but experts might want to fine-tune.
    /// </summary>
    public bool IsAdvanced = false;
    
    /// <summary>
    /// Name of another field this field depends on for visibility (conditional display).
    /// For example, "ManualBfactor" might only be visible when "EstimateBfactor" is false.
    /// </summary>
    public string ConditionalOnField = "";
    
    /// <summary>
    /// Value the conditional field must have for this field to be displayed.
    /// Can be boolean, enum value, or other types depending on the field being referenced.
    /// </summary>
    public object ConditionalOnValue = null;
    
    /// <summary>
    /// Default value for this field, used when creating new jobs.
    /// </summary>
    public object DefaultValue = null;
    
    public string DataDelegateName = null;
    
    public Func<ReadOnlyJob, object> DataDelegate = null;

    /// <summary>
    /// Gets the Blazor component type that should be used to render this field.
    /// Each field type must specify its corresponding view component (e.g., UiPathView, UiSymmetryView).
    /// This enables the dynamic rendering system in UiFieldView.razor.
    /// </summary>
    public abstract Type ViewType { get; }

    /// <summary>
    /// Creates a new UI field descriptor with the specified display and command-line properties.
    /// This constructor is called by all derived field types to initialize common properties.
    /// </summary>
    /// <param name="cliName">Command-line argument name for this parameter</param>
    /// <param name="label">Human-readable label shown in the UI</param>
    /// <param name="helpText">Optional tooltip text explaining the parameter</param>
    /// <param name="isAdvanced">Whether this field should be shown only in advanced mode</param>
    public UiFieldBase(string cliName, string label, string helpText = "", bool isAdvanced = false, string dataDelegateName = null)
    {
        CliName = cliName;
        Label = label;
        HelpText = helpText;
        IsAdvanced = isAdvanced;
        DataDelegateName = dataDelegateName;
    }

    /// <summary>
    /// Gets the complete label to display in the UI, including any necessary formatting or additions.
    /// Overridden by specialized field types like UiPath to add additional context (e.g., file extensions).
    /// </summary>
    public virtual string FullLabel => Label;
}