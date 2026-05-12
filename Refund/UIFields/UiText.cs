namespace Refund.UIFields;

/// <summary>
/// Field attribute for multi-line text values. Renders as a larger text area in the UI.
/// 
/// Primarily used for documentation-focused job types where substantial free-form text 
/// input is required, such as the Note job type where researchers can document their 
/// methodology, reasoning, or observations within the workflow.
/// 
/// The UiText component renders as a taller (12-row) text area through FluentTextArea,
/// making it suitable for capturing longer paragraphs of text that preserve formatting.
/// 
/// For single-line text inputs or shorter fields, use UiString instead.
/// </summary>
/// <remarks>
/// In actual usage, UiText is predominantly found in documentation jobs like Notes,
/// where it's used to store substantial explanatory text. The Note job uses UiText
/// with an empty CLI name since the text isn't passed to command-line processing
/// but is stored directly in job state and saved to log files.
/// 
/// Example usage in Note.cs:
/// ```csharp
/// [UiFieldGroup("Parameters", 0)]
/// [UiText("", "Note text")]
/// [RelayProperty]
/// public string ProcessingNote { get; set; } = "";
/// ```
/// </remarks>
public class UiText : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field.
    /// UiTextView renders a FluentTextArea with 12 rows, providing ample space
    /// for multi-paragraph content with immediate value binding.
    /// </summary>
    public override Type ViewType => typeof(UiTextView);

    /// <summary>
    /// Creates a new multi-line text field with the specified properties
    /// </summary>
    /// <param name="cliName">Command-line argument name (often empty for documentation jobs)</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="helpText">Optional tooltip text</param>
    /// <param name="isAdvanced">Whether this is an advanced option</param>
    public UiText(string cliName, string label, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced) { }
}
