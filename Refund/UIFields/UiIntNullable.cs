namespace Refund.UIFields;

/// <summary>
/// Field attribute for integer values. Renders as a number input with optional spinner controls.
/// Commonly used for counts, indices, or dimensionless integer parameters like iterations or batch size.
/// </summary>
/// <remarks>
/// This field is frequently used in cryo-EM processing jobs for parameters such as:
/// - Number of classes in 3D classification (e.g., "K" parameter in Class3D)
/// - Iteration counts for optimization procedures (e.g., in refinement and classification jobs)
/// - B-factor values for map sharpening in post-processing (supporting large negative/positive values)
/// - Angular sampling parameters
/// 
/// The field supports conditional display based on other field values through the ConditionalOnField 
/// property inherited from UiFieldBase.
/// </remarks>
public class UiIntNullable : UiFieldBase
{
    /// <summary>
    /// Minimum allowed value for this integer field.
    /// </summary>
    /// <remarks>
    /// In practice, values can range from very negative (e.g., -100000 for B-factors in post-processing)
    /// to standard minimums like 1 for iteration or class counts.
    /// </remarks>
    public int Min;
    
    /// <summary>
    /// Maximum allowed value for this integer field.
    /// </summary>
    /// <remarks>
    /// Typical maximum values include 10000 for iteration counts and class numbers,
    /// and 100000 for parameters like B-factors in post-processing.
    /// </remarks>
    public int Max;
    
    /// <summary>
    /// Step size for incrementing/decrementing the value (used by spinners/sliders).
    /// </summary>
    /// <remarks>
    /// Commonly set to 1 for iteration counts and class numbers, but can be larger
    /// (e.g., 10) for parameters with wider ranges like B-factors.
    /// </remarks>
    public int StepSize;
    
    /// <summary>
    /// Optional unit of measurement to display alongside the value (e.g., "px", "frames")
    /// </summary>
    public string Unit = "";
    
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiIntView)
    /// </summary>
    public override Type ViewType => typeof(UiIntNullableView);

    /// <summary>
    /// Creates a new integer field with the specified constraints
    /// </summary>
    /// <param name="cliName">Command-line argument name (e.g., "K", "iter", "adhoc_bfac")</param>
    /// <param name="label">Display label in the UI (e.g., "Number of classes", "Number of iterations")</param>
    /// <param name="min">Minimum allowed value</param>
    /// <param name="max">Maximum allowed value</param>
    /// <param name="stepSize">Increment/decrement step size</param>
    /// <param name="unit">Unit of measurement (e.g., "Å", "Å²")</param>
    /// <param name="helpText">Optional tooltip text explaining parameter purpose and effects</param>
    /// <param name="isAdvanced">Whether this is an advanced option</param>
    /// <remarks>
    /// Common patterns seen in usage:
    /// - Class number parameters typically use min:1, max:10000, stepSize:1
    /// - Iteration counts typically use min:1, max:10000, stepSize:1
    /// - B-factors use wider ranges (min:-100000, max:100000) with larger step sizes (10)
    /// 
    /// When used with ConditionalOnField, this allows creating dependent parameter relationships,
    /// such as showing a manual B-factor field only when automatic estimation is disabled.
    /// </remarks>
    public UiIntNullable(string cliName, string label, int min = int.MinValue, int max = int.MaxValue, int stepSize = 1, string unit = "", string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced)
    {
        Min = min;
        Max = max;
        StepSize = stepSize;
        Unit = unit;
    }

    /// <summary>
    /// Gets the full label including the unit of measurement if specified
    /// </summary>
    public override string FullLabel => Label + (!string.IsNullOrWhiteSpace(Unit) ? $" ({Unit})" : "");
}
