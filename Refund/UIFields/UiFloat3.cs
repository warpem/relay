namespace Refund.UIFields;

/// <summary>
/// Field attribute for a 3D vector of floating-point values. Renders as three coordinated number inputs in the UI.
/// Used for parameters that require three related values, particularly in helical reconstruction and 
/// angular search parameters for cryo-EM processing.
/// </summary>
/// <remarks>
/// The UiFloat3 class expects a comma-separated list of three CLI parameter names and maps them to 
/// a float3 property. When processing CLI arguments, the first, second, and third names receive the 
/// X, Y, and Z components respectively.
/// 
/// In cryo-EM processing, UiFloat3 is primarily used for:
/// 1. Angular search parameters (rot/tilt/psi) - Controls Euler angle search ranges in 3D classification
/// 2. Helical symmetry search parameters - Defines min/max/step values for twist and rise parameters
/// 
/// Example applications in Class3D jobs:
/// - "sigma_rot,sigma_tilt,sigma_psi" - Angular search ranges for rotational alignment
/// - "helical_twist_min,helical_twist_max,helical_twist_inistep" - Twist search parameters for helical structures
/// - "helical_rise_min,helical_rise_max,helical_rise_inistep" - Rise search parameters for helical structures
/// </remarks>
public class UiFloat3 : UiFieldBase
{
    /// <summary>
    /// Minimum allowed value for each component of the vector
    /// </summary>
    public float Min;
    
    /// <summary>
    /// Maximum allowed value for each component of the vector
    /// </summary>
    public float Max;
    
    /// <summary>
    /// Step size for incrementing/decrementing each component value
    /// </summary>
    public float StepSize;
    
    /// <summary>
    /// Unit of measurement to display alongside the vector values (e.g., "°" for angles, "Å" for rise)
    /// </summary>
    public string Unit = "";
    
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiFloat3View)
    /// </summary>
    public override Type ViewType => typeof(UiFloat3View);

    /// <summary>
    /// Creates a new 3D float vector field with the specified constraints
    /// </summary>
    /// <param name="cliName">Comma-separated list of three command-line argument names (e.g., "min,max,step")</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="min">Minimum allowed value for each component</param>
    /// <param name="max">Maximum allowed value for each component</param>
    /// <param name="stepSize">Increment/decrement step size</param>
    /// <param name="helpText">Optional tooltip text</param>
    /// <param name="isAdvanced">Whether this is an advanced option</param>
    public UiFloat3(string cliName, string label, float min = -1e10f, float max = 1e10f, float stepSize = 1.0f, string helpText = "", bool isAdvanced = false)
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
