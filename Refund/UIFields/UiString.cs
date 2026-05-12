namespace Refund.UIFields;

/// <summary>
/// Field attribute for single-line string values. Renders as a text input field in the UI.
/// Used for short text entries like names, identifiers, or simple text parameters.
/// For longer multi-line text, use UiText instead.
/// 
/// Commonly used in cryo-EM processing jobs for:
/// 1. GPU device selection (e.g., "gpu" argument in Class3D to override automatic GPU allocation)
/// 2. Additional command-line arguments that aren't exposed in the UI (typically marked as advanced options)
/// </summary>
public class UiString : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiStringView)
    /// </summary>
    public override Type ViewType => typeof(UiStringView);

    /// <summary>
    /// Creates a new string field with the specified properties
    /// </summary>
    /// <param name="cliName">Command-line argument name (e.g., "gpu" for GPU selection in Class3D)</param>
    /// <param name="label">Display label in the UI (e.g., "Which GPUs to use", "Additional arguments")</param>
    /// <param name="helpText">Optional tooltip text explaining the parameter's purpose and usage</param>
    /// <param name="isAdvanced">Whether this is an advanced option (typically true for GPU selection and additional arguments)</param>
    public UiString(string cliName, string label, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced) { }
}
