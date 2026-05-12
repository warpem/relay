namespace Refund.UIFields;

/// <summary>
/// Field attribute for enumeration values. Renders as a dropdown selection list in the UI.
/// Used for parameters with a fixed set of predefined options in cryo-EM processing jobs.
/// 
/// Primary applications include:
/// - Algorithm selection in 2D/3D classification (e.g., choosing between VDAM or EM algorithm)
/// - Alignment type selection (e.g., Global vs. Local alignment in 3D classification)
/// - Processing mode selection across various job types
/// 
/// UiEnum is designed to work with standard C# enum types to provide consistent categorized
/// options with appropriate documentation in the UI.
/// </summary>
public class UiEnum : UiFieldBase
{
    /// <summary>
    /// The enum type that defines the available options for this field.
    /// This is used to dynamically populate the dropdown with enum values
    /// and their string representations.
    /// </summary>
    public Type EnumType;
    
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiEnumView)
    /// </summary>
    public override Type ViewType => typeof(UiEnumView);

    /// <summary>
    /// Creates a new enum field with the specified enum type
    /// </summary>
    /// <param name="cliName">Command-line argument name for CLI generation</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="enumType">The enum type that defines the available options (e.g., typeof(Class2DAlgorithm))</param>
    /// <param name="helpText">Optional tooltip text explaining the parameter and its impact</param>
    /// <param name="isAdvanced">Whether this is an advanced option hidden by default</param>
    public UiEnum(string cliName, string label, Type enumType, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced)
    {
        EnumType = enumType;
    }
}
