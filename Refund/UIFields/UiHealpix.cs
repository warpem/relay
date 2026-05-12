namespace Refund.UIFields;

/// <summary>
/// Field attribute for HEALPix (Hierarchical Equal Area isoLatitude Pixelization) angular sampling order.
/// Renders as a specialized dropdown control for selecting angular sampling density in spherical distributions.
/// 
/// HEALPix is used in cryo-EM processing for efficient and uniform angular sampling on the sphere, 
/// particularly for defining the angular search space in classification and refinement jobs.
/// The sampling orders translate to specific angular increments:
/// - Order 0: 60° (coarsest)
/// - Order 1: 30°
/// - Order 2: 15°
/// - Order 3: 7.5° (default for initial alignment)
/// - Order 4: 3.7°
/// - Order 5: 1.8° (standard for local searches)
/// - Order 6: 0.9° (finer sampling for high-symmetry structures)
/// - Order 7: 0.5°
/// - Order 8: 0.2°
/// - Order 9: 0.1° (finest)
/// 
/// Higher HEALPix orders provide finer angular sampling but increase computational requirements.
/// 
/// Used in two main contexts:
/// 1. Initial angular sampling ("healpix_order") - Controls the density of initial orientation search
/// 2. Local angular search threshold ("auto_local_healpix_order") - Determines when refinement 
///    transitions from global to local angular searches (typically at order 5/1.8° sampling)
/// </summary>
public class UiHealpix : UiFieldBase
{
    /// <summary>
    /// Gets the Blazor component type used to render this field (UiHealpixView)
    /// </summary>
    public override Type ViewType => typeof(UiHealpixView);

    /// <summary>
    /// Creates a new HEALPix order selection field with the specified properties
    /// </summary>
    /// <param name="cliName">Command-line argument name (e.g., "healpix_order" or "auto_local_healpix_order")</param>
    /// <param name="label">Display label in the UI (e.g., "Angular sampling" or "Local searches from angular sampling")</param>
    /// <param name="helpText">Optional tooltip text explaining HEALPix usage in this context</param>
    /// <param name="isAdvanced">Whether this is an advanced option (typically false as angular sampling is a critical parameter)</param>
    public UiHealpix(string cliName, string label, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced)
    {
    }
}