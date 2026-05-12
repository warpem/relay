namespace Refund.UIFields;

/// <summary>
/// Field attribute for floating-point decimal values. Renders as a number input with precision support.
/// Used for scientific parameters that require decimal precision, such as thresholds, scaling factors,
/// or physical measurements like angstroms or electron volts.
/// </summary>
/// <remarks>
/// In cryo-EM processing, this field is commonly used for physical parameters that require
/// decimal precision, particularly:
/// 
/// - Resolution values in Ångstroms (minimum/maximum resolution for processing)
/// - B-factors for high-frequency weighting (in Å²)
/// - Defocus values and ranges
/// - Patch sizes for processing
/// - Pixel sizes and scaling factors
/// 
/// The field supports appropriate units display and is frequently used in motion correction
/// and CTF estimation parameter groups with domain-specific value ranges and step sizes.
/// </remarks>
public class UiDecimal : UiFieldBase
{
    /// <summary>
    /// Minimum allowed value for this decimal field
    /// </summary>
    /// <remarks>
    /// For resolution parameters, typically set to 1 (Ångstrom) as a physical lower bound.
    /// For B-factors and other parameters, can have wide ranges depending on the physical
    /// meaning of the value.
    /// </remarks>
    public decimal Min;
    
    /// <summary>
    /// Maximum allowed value for this decimal field
    /// </summary>
    /// <remarks>
    /// For resolution parameters, can be set quite high (e.g., 99999) to accommodate
    /// low-resolution limits. For pixel-based measurements, typically constrained to
    /// reasonable values based on image dimensions.
    /// </remarks>
    public decimal Max;
    
    /// <summary>
    /// Step size for incrementing/decrementing the value (used by spinners/sliders)
    /// </summary>
    /// <remarks>
    /// Commonly set to 1.0 for resolution values in Ångstroms, but can be larger (e.g., 10)
    /// for B-factors and other parameters with wider ranges. Step size should reflect the
    /// precision needed for the parameter and the typical adjustment increments.
    /// </remarks>
    public decimal StepSize;
    
    /// <summary>
    /// Unit of measurement to display alongside the value (e.g., "Å", "kV", "px")
    /// </summary>
    /// <remarks>
    /// Common units in cryo-EM processing include:
    /// - "Å" (Ångstrom) for resolution values
    /// - "Å²" for B-factors 
    /// - "px" for pixel measurements
    /// - "kV" for voltage parameters
    /// </remarks>
    public string Unit;
    
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiDecimalView)
    /// </summary>
    public override Type ViewType => typeof(UiDecimalView);

    /// <summary>
    /// Creates a new decimal field with the specified constraints and unit
    /// </summary>
    /// <param name="cliName">Command-line argument name (e.g., "m_range_min", "m_bfac")</param>
    /// <param name="label">Display label in the UI (e.g., "Minimum resolution", "B-factor")</param>
    /// <param name="min">Minimum allowed value</param>
    /// <param name="max">Maximum allowed value</param>
    /// <param name="stepSize">Increment/decrement step size</param>
    /// <param name="unit">Unit of measurement (e.g., "Å", "Å²")</param>
    /// <param name="helpText">Optional tooltip text explaining parameter purpose and effects</param>
    /// <param name="isAdvanced">Whether this is an advanced option</param>
    /// <remarks>
    /// Usage patterns from actual code show:
    /// - Resolution parameters use min:1, max:99999, stepSize:1, unit:"Å"
    /// - B-factor parameters often use larger step sizes (10) with unit:"Å²"
    /// - Help text typically explains the parameter's effect on processing
    /// 
    /// These fields are often organized within UiFieldGroups that correspond to
    /// their functional role in the processing pipeline, such as "Motion Processing"
    /// or "CTF Processing".
    /// </remarks>
    public UiDecimal(string cliName, string label, double min = -1e10, double max = 1e10, double stepSize = 1.0, string unit = "", string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced)
    {
        Min = (decimal)min;
        Max = (decimal)max;
        StepSize = (decimal)stepSize;
        Unit = unit;
    }

    /// <summary>
    /// Gets the full label including the unit of measurement if specified
    /// </summary>
    /// <remarks>
    /// This provides automatic unit display in the UI, showing values like "Minimum resolution (Å)"
    /// or "B-factor (Å²)" without requiring the unit to be part of the main label.
    /// </remarks>
    public override string FullLabel => Label + (!string.IsNullOrWhiteSpace(Unit) ? $" ({Unit})" : "");
}
