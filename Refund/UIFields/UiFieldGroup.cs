namespace Refund.UIFields;

/// <summary>
/// Attribute used to group related UI fields together in the job parameter editor interface.
/// Groups allow logical separation of fields and can be displayed with collapsible sections.
/// </summary>
/// <remarks>
/// In cryo-EM job configuration, this attribute is commonly used to organize parameters into 
/// logical processing stages or functional groups. Common groupings include:
/// 
/// - "Motion Processing" - Parameters related to motion correction settings (e.g., minimum/maximum resolution)
/// - "CTF Processing" - Parameters related to CTF estimation (e.g., patch size, defocus range)
/// - "Output" - Parameters controlling job outputs (e.g., exporting aligned averages)
/// - "Optimization" - Parameters for the optimization process (e.g., iterations, class numbers)
///
/// Groups are typically ordered to follow the logical processing sequence of the job, with input
/// and preprocessing parameters appearing first, followed by core processing parameters, and finally
/// output parameters.
/// </remarks>
public class UiFieldGroup : Attribute
{
    /// <summary>
    /// The display label for the group in the UI
    /// </summary>
    /// <remarks>
    /// Typically follows a pattern of "{Process} Processing" or concise functional descriptions
    /// like "Output" or "Optimization".
    /// </remarks>
    public string Label = "";
    
    /// <summary>
    /// The display order for the group relative to other groups (lower numbers appear first)
    /// </summary>
    /// <remarks>
    /// In practice, jobs typically follow a convention where:
    /// - 0: Input and preprocessing parameters 
    /// - 1: Core processing parameters
    /// - 2: Output and post-processing parameters
    /// </remarks>
    public int Order = 0;

    /// <summary>
    /// Creates a new UI field group with the specified label and display order
    /// </summary>
    /// <param name="label">The human-readable label to display for this group (e.g., "Motion Processing", "CTF Processing", "Output")</param>
    /// <param name="order">The relative ordering of this group compared to others (lower numbers appear first)</param>
    /// <remarks>
    /// In the MotionAndCTF2D job, for example, parameters are organized into three groups:
    /// - "Motion Processing" (order: 0) - Parameters for motion correction
    /// - "CTF Processing" (order: 1) - Parameters for CTF estimation
    /// - "Output" (order: 2) - Export parameters
    /// 
    /// This organization makes the UI more intuitive, grouping related parameters together and
    /// presenting them in a logical sequence that follows the processing workflow.
    /// </remarks>
    public UiFieldGroup(string label, int order)
    {
        Label = label;
        Order = order;
    }
}
