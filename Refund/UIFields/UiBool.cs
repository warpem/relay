namespace Refund.UIFields;

/// <summary>
/// Field attribute for boolean toggle values. Renders as a switch or checkbox in the UI.
/// Commonly used for yes/no options in cryo-EM processing workflows.
/// 
/// Frequently used in jobs for enabling/disabling specific processing steps, such as:
/// - CTF processing options (e.g., "Fit phase shift", "Use movie average")
/// - Output generation options (e.g., "Export averages")
/// - Feature toggles in various processing stages
/// </summary>
public class UiBool : UiFieldBase
{
    /// <summary>
    /// When true, the logical meaning of the UI control is reversed - the toggle being on means the boolean is false.
    /// This is useful for cases where the natural language description is a negative (e.g., "Disable feature").
    /// 
    /// In command-line generation, this property is used to determine whether a flag should be included:
    /// - When Reverse=false and value=false: flag is omitted
    /// - When Reverse=true and value=true: flag is omitted
    /// </summary>
    public bool Reverse { get; set; } = false;
    
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiBoolView)
    /// </summary>
    public override Type ViewType => typeof(UiBoolView);

    /// <summary>
    /// Creates a new boolean field with the specified properties
    /// </summary>
    /// <param name="cliName">Command-line argument name used when generating job execution commands</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="helpText">Optional tooltip text that explains the parameter's purpose and impact</param>
    /// <param name="isAdvanced">Whether this is an advanced option hidden by default in the UI</param>
    /// <param name="reverse">Whether to reverse the logical meaning (true = off, false = on)</param>
    public UiBool(string cliName, string label, string helpText = "", bool isAdvanced = false, bool reverse = false) 
        : base(cliName, label, helpText, isAdvanced)
    {
        Reverse = reverse;
    }
}
