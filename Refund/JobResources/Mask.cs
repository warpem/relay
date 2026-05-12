using Refund.DataModel;

namespace Refund.JobResources
{
    /// <summary>
    /// Represents a binary or soft-edge 3D mask volume used for focused processing or
    /// post-processing. Masks define regions of interest within a 3D volume that should be
    /// included or excluded during image processing.
    /// </summary>
    public class Mask : Resource
    {
        /// <summary>
        /// Path to the mask volume file, typically in MRC format with values between 0 (excluded) and 1 (included).
        /// </summary>
        public readonly string MaskVolumePath;

        /// <summary>
        /// Creates a new Mask resource with a path to the mask volume file.
        /// </summary>
        /// <param name="maskVolumePath">Path to the mask volume file</param>
        public Mask(string maskVolumePath)
        {
            MaskVolumePath = maskVolumePath;
        }

        /// <summary>
        /// Returns a collection of downloadable resources associated with this mask.
        /// </summary>
        /// <returns>Collection of resources that can be downloaded by the user</returns>
        public override IEnumerable<Downloadable> GetDownloadables()
        {
            List<Downloadable> result = new();
            
            if (!string.IsNullOrWhiteSpace(MaskVolumePath))
                result.Add(new Downloadable("Mask", "", MaskVolumePath));

            return result;
        }
    }
}
