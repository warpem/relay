namespace Refund.UIFields;

/// <summary>
/// Field attribute for a 3D vector of integer values. Renders as three coordinated integer inputs in the UI.
/// Used for parameters that require three integer dimensions or grid specifications in cryo-EM processing.
/// </summary>
/// <remarks>
/// UiInt3 is primarily used for two key purposes in cryo-EM workflows:
/// 
/// 1. Motion model and CTF model grid specifications in the MotionAndCTF2D job:
///    - Defines the resolution of motion correction models along X, Y, and temporal dimensions
///    - Specifies defocus model grid dimensions for modeling local defocus variations
///    - These are critical for balancing precision of motion/defocus correction against available signal
///    
/// 2. Tomogram dimensions in tilt series import (ImportDataSetTs):
///    - Specifies the X, Y, Z dimensions of tomographic volumes in unbinned pixels
///    - Used to properly calculate memory requirements and initialize reconstruction volumes
///    
/// Unlike UiFloat3, which is often used for search parameters or ranges, UiInt3 is specifically suited
/// for dimensions, grid sizes, and other integer-only 3D values in the processing workflow.
/// </remarks>
public class UiInt4 : UiFieldBase
{
    /// <summary>
    /// Minimum allowed value for each component of the vector
    /// </summary>
    public int Min;
    
    /// <summary>
    /// Maximum allowed value for each component of the vector
    /// </summary>
    public int Max;
    
    /// <summary>
    /// Step size for incrementing/decrementing each component value
    /// </summary>
    public int StepSize;
    
    /// <summary>
    /// Unit of measurement to display alongside the vector values (e.g., "pixels", "unbinned pixels")
    /// </summary>
    public string Unit = "";
    
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiInt3View)
    /// </summary>
    public override Type ViewType => typeof(UiInt4View);

    /// <summary>
    /// Creates a new 3D integer vector field with the specified constraints
    /// </summary>
    /// <param name="cliName">Command-line argument name (Single CLI parameter that accepts a formatted string like "5x5x40")</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="min">Minimum allowed value for each component</param>
    /// <param name="max">Maximum allowed value for each component</param>
    /// <param name="stepSize">Increment/decrement step size</param>
    /// <param name="helpText">Optional tooltip text</param>
    /// <param name="isAdvanced">Whether this is an advanced option</param>
    public UiInt4(string cliName, string label, int min = -(1 << 30), int max = 1 << 30, int stepSize = 1, string helpText = "", bool isAdvanced = false)
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