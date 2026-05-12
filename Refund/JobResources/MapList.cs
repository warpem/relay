using System.Collections.ObjectModel;
using Refund.DataModel;

namespace Refund.JobResources
{
    /// <summary>
    /// Represents a collection of 3D volumetric maps, typically resulting from classification or
    /// multi-model refinement procedures. Each map in the collection represents a different
    /// structural class or state.
    /// </summary>
    public class MapList : Resource
    {
        /// <summary>
        /// The collection of Map resources, each representing a different 3D class or state.
        /// </summary>
        public readonly ReadOnlyCollection<Map> Maps;
        
        /// <summary>
        /// Path to a model file (typically in STAR format) containing metadata about the maps,
        /// such as class populations, angular distributions, or refinement parameters.
        /// </summary>
        public readonly string Model;
        
        /// <summary>
        /// Creates a new MapList resource containing multiple 3D maps and optional model metadata.
        /// </summary>
        /// <param name="maps">List of Map resources to include in this collection</param>
        /// <param name="model">Path to a model file with metadata about the maps</param>
        public MapList(List<Map> maps, string model = null)
        {
            Maps = maps.ToList().AsReadOnly();
            Model = model;
        }

        /// <summary>
        /// Returns a collection of all downloadable resources from all maps in the collection.
        /// </summary>
        /// <returns>Flattened collection of downloadable resources from all contained maps</returns>
        public override IEnumerable<Downloadable> GetDownloadables()
        {
            var result = Maps.SelectMany(m => m.GetDownloadables()).ToList();
            if (!string.IsNullOrEmpty(Model))
                result.Add(new Downloadable("Model Metadata", "STAR file containing metadata about the maps", Model));
            
            return result;
        }
    }
}
