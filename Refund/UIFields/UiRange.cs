namespace Refund.UIFields;

/// <summary>
/// Field attribute for a paired range of floating-point values. Renders as a dual-value slider or range input in the UI.
/// Used for parameters that represent a min/max pair or interval, such as frequency filters, 
/// resolution ranges, or threshold bands like helical tube diameters.
/// </summary>
/// <remarks>
/// The UiRange class expects a comma-separated pair of CLI parameter names and maps them to a float2 property.
/// When processing CLI arguments, the first name receives the X component and the second receives the Y component.
/// 
/// Commonly used in cryo-EM processing for:
/// - Inner/outer diameter specifications for helical structures
/// - Resolution filter ranges
/// - Minimum/maximum threshold values
/// 
/// Example usage in Class3D and Refine3D jobs for helical reconstruction parameters:
/// [UiRange("helical_inner_diameter,helical_outer_diameter", "Tube diameter")]
/// </remarks>
public class UiRange : UiFieldBase
{
    /// <summary>
    /// Minimum allowed value for both elements of the range
    /// </summary>
    public float Min;
    
    /// <summary>
    /// Maximum allowed value for both elements of the range
    /// </summary>
    public float Max;
    
    /// <summary>
    /// Step size for incrementing/decrementing the range values
    /// </summary>
    public float StepSize;
    
    /// <summary>
    /// Unit of measurement to display alongside the range values (e.g., "Å" for Angstroms)
    /// </summary>
    public string Unit = "";
    
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiRangeView)
    /// </summary>
    public override Type ViewType => typeof(UiRangeView);

    /// <summary>
    /// Creates a new range field with the specified constraints
    /// </summary>
    /// <param name="cliName">Comma-separated pair of command-line argument names (e.g., "min_res,max_res")</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="min">Minimum allowed value for both range elements</param>
    /// <param name="max">Maximum allowed value for both range elements</param>
    /// <param name="stepSize">Increment/decrement step size</param>
    /// <param name="helpText">Optional tooltip text</param>
    /// <param name="isAdvanced">Whether this is an advanced option</param>
    public UiRange(string cliName, string label, float min = -1e10f, float max = 1e10f, float stepSize = 1.0f, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced)
    {
        Min = min;
        Max = max;
        StepSize = stepSize;
    }

    /// <summary>
    /// Gets the full label including the unit of measurement if specified
    /// </summary>
    public override string FullLabel => Label + (!string.IsNullOrWhiteSpace(Unit) ? $" ({Unit})" : "");
}
