namespace Refund.UIFields;

/// <summary>
/// Field attribute for molecular symmetry selection. Renders as a specialized symmetry selection control.
/// Used specifically for cryo-EM processing where molecular symmetry is a critical parameter,
/// such as in 3D classification or refinement jobs. Supports point group symmetries (C1, C2, D7, etc.)
/// and other symmetry types relevant to structural biology.
/// </summary>
/// <remarks>
/// Key usage patterns:
/// 1. Primary usage in 3D reconstruction jobs (Class3D, Refine3D) to specify the symmetry of reference maps
/// 2. Used for both standard symmetry specification ("sym" parameter) and relaxed symmetry options ("relax_sym")
/// 3. Commonly appears in the "Reference" field group with order 0 (high priority)
/// 4. Default symmetry is typically C1 (no symmetry) when unsure about the specimen
/// 
/// The symmetry parameter is critical for cryo-EM 3D reconstructions as it:
/// - Speeds up calculations by leveraging known structural redundancy
/// - Improves signal-to-noise ratio when the specimen truly has the specified symmetry
/// - Can introduce artifacts if incorrectly specified
/// </remarks>
public class UiSymmetry : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiSymmetryView)
    /// </summary>
    public override Type ViewType => typeof(UiSymmetryView);

    /// <summary>
    /// Creates a new symmetry selection field with the specified properties
    /// </summary>
    /// <param name="cliName">Command-line argument name (typically "sym" for main symmetry or "relax_sym" for relaxed symmetry)</param>
    /// <param name="label">Display label in the UI (typically "Symmetry" or "Relax symmetry")</param>
    /// <param name="helpText">Optional tooltip text explaining symmetry implications</param>
    /// <param name="isAdvanced">Whether this is an advanced option (relaxed symmetry is often an advanced option)</param>
    public UiSymmetry(string cliName, string label, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced)
    {
    }
}