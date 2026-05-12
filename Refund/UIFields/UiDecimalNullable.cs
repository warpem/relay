namespace Refund.UIFields;

/// <summary>
/// Field attribute for nullable decimal values. Renders as a number input that can be left empty/null.
/// Extended version of UiDecimal that allows for the absence of a value (null).
/// 
/// Used in cryo-EM processing workflows for:
/// 1. Optional pixel size overrides in import jobs (ImportMap)
/// 2. Angular parameters that can be automatically determined, such as tilt axis angles (ImportDataSetTs)
/// 3. Output pixel size specifications that can default to input settings (ExtractParticles2D)
/// 
/// When null, the underlying processing typically uses a system-determined default value or
/// inherits a value from other processing parameters.
/// </summary>
public class UiDecimalNullable : UiDecimal
{
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiDecimalNullableView)
    /// </summary>
    public override Type ViewType => typeof(UiDecimalNullableView);

    /// <summary>
    /// Creates a new nullable decimal field with the specified constraints
    /// </summary>
    /// <param name="cliName">Command-line argument name used for CLI generation when a value is provided</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="min">Minimum allowed value when a value is provided. For physical measurements like pixel sizes, typically >0</param>
    /// <param name="max">Maximum allowed value when a value is provided</param>
    /// <param name="stepSize">Increment/decrement step size. For precision parameters like pixel size, often 0.001</param>
    /// <param name="unit">Unit of measurement (e.g., "Å" for pixel size, "°" for angular measurements)</param>
    /// <param name="helpText">Optional tooltip text that should explain both the parameter and implications of leaving it null</param>
    /// <param name="isAdvanced">Whether this is an advanced option hidden by default in the UI</param>
    public UiDecimalNullable(string cliName,
                             string label,
                             double min = -1e10,
                             double max = 1e10,
                             double stepSize = 1.0,
                             string unit = "",
                             string helpText = "",
                             bool isAdvanced = false)
        : base(cliName, label, min, max, stepSize, unit, helpText, isAdvanced)
    {
        
    }
}
