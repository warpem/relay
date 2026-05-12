namespace Refund.DataModel;

/// <summary>
/// Abstract base class for all resource types that flow between job ports.
/// Resources represent scientific data (images, volumes, coordinate sets, etc.) 
/// that are produced and consumed by jobs in the processing pipeline.
/// </summary>
public abstract class Resource
{
    /// <summary>
    /// Creates a new Resource instance.
    /// </summary>
    public Resource()
    {
    }

    /// <summary>
    /// Resolves a resource path to obtain specific sub-resources.
    /// This is used for hierarchical resources where a path can specify a particular component.
    /// The default implementation simply returns this resource.
    /// </summary>
    /// <param name="path">The path to resolve, as a sequence of identifiers</param>
    /// <returns>The resources matching the specified path</returns>
    public virtual IEnumerable<Resource> ResolveResource(IEnumerable<string> path)
    {
        return new[] { this };
    }
    
    /// <summary>
    /// Gets a collection of downloadable items from this resource.
    /// Downloadables represent files that can be downloaded by the user.
    /// The default implementation returns an empty collection.
    /// </summary>
    /// <returns>Collection of downloadable items</returns>
    public virtual IEnumerable<Downloadable> GetDownloadables() => Array.Empty<Downloadable>();
}

public class DependsOnSubResourceAttribute(params string[] dependsOn) : Attribute
{
    public string[] DependsOn = dependsOn;
}

public interface ICountableResource
{
    /// <summary>
    /// Gets the number of items in this resource.
    /// This is used to determine how many sub-resources are available for processing.
    /// </summary>
    int Count { get; }
}

/// <summary>
/// Represents a downloadable file from a resource.
/// Downloadables provide metadata and paths for files that can be downloaded by the user.
/// </summary>
/// <param name="name">Display name of the downloadable</param>
/// <param name="description">Description of the downloadable's contents</param>
/// <param name="serverPath">Path to the file on the server</param>
/// <param name="visualizationPath">Optional path to a visualization of the file</param>
public class Downloadable(string name, string description, string serverPath, string visualizationPath = null)
{
    /// <summary>
    /// Display name of the downloadable, shown in the UI.
    /// </summary>
    public string Name { get; set; } = name;
    
    /// <summary>
    /// Description of the downloadable's contents, shown as a tooltip in the UI.
    /// </summary>
    public string Description { get; set; } = description;
    
    /// <summary>
    /// Path to the file on the server.
    /// This path is used to retrieve the file when the user requests a download.
    /// </summary>
    public string ServerPath { get; set; } = serverPath;
    
    /// <summary>
    /// Optional path to a visualization of the file.
    /// If provided, this visualization can be displayed in the UI instead of the raw file.
    /// </summary>
    public string VisualizationPath { get; set; } = visualizationPath;
}