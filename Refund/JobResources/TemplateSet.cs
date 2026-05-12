using Refund.DataModel;

namespace Refund.JobResources
{
    /// <summary>
    /// Represents a set of template references used for template-based procedures like
    /// template matching, template-based particle picking, or multi-reference alignment.
    /// A template set typically consists of reference volumes and associated metadata.
    /// </summary>
    public class TemplateSet : Resource
    {
        /// <summary>
        /// Path to a STAR file containing metadata about the templates, such as
        /// class populations, refinement statistics, or classification parameters.
        /// </summary>
        public readonly string ModelStarPath;
        
        /// <summary>
        /// Path to an MRC file containing the 3D template volumes used for alignment or picking.
        /// This may be a single volume or multiple volumes stacked together.
        /// </summary>
        public readonly string TemplateMrcPath;
        
        /// <summary>
        /// Path to a visualization resource showing class statistics for the templates,
        /// typically in the form of a plot or table.
        /// </summary>
        public readonly string VisClassStats;

        /// <summary>
        /// Creates a new TemplateSet resource with paths to template data and visualizations.
        /// </summary>
        /// <param name="modelStarPath">Path to the template model metadata in STAR format</param>
        /// <param name="templateMrcPath">Path to the template volume(s) in MRC format</param>
        /// <param name="visClassStats">Path to visualization of class statistics for the templates</param>
        public TemplateSet(string modelStarPath, string templateMrcPath, string visClassStats)
        {
            ModelStarPath = modelStarPath;
            TemplateMrcPath = templateMrcPath;
            VisClassStats = visClassStats;
        }
    }
}
