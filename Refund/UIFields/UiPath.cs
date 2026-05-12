using Microsoft.IdentityModel.Tokens;
using Refund.Components.FileBrowser;

namespace Refund.UIFields;

/// <summary>
/// Field attribute for file or directory path selection. Renders as a path selector with file browser integration.
/// Used when jobs need to reference external files or directories, such as input data, reference maps,
/// or output directories. Supports filtering by file extension.
/// </summary>
/// <remarks>
/// Key usage patterns:
/// 1. In import jobs (ImportMap, ImportParticles) to specify source data files/directories with appropriate extension filtering
/// 2. In processing jobs like PostProcess3D for optional auxiliary files (e.g., detector MTF files)
/// 3. Used with PathType.Directory for directory selection and PathType.File for specific file selection
/// 4. Provides built-in validation through the ImportParticles.ValidateInputs() method
/// </remarks>
public class UiPath : UiFieldBase
{
    /// <summary>
    /// List of allowed file extensions for file selection (ignored for directory selection).
    /// Used for filtering files in the file browser and for path validation.
    /// Common patterns include ["*.map", "*.mrc"] for volume maps and ["*.star"] for particle metadata.
    /// </summary>
    public List<string> FileExtensions;
    
    /// <summary>
    /// Specifies whether this path selector should select files or directories.
    /// PostProcess3D and similar advanced processing jobs typically use PathType.File for auxiliary files like MTF curves,
    /// while import jobs often use PathType.Directory for specifying data sources.
    /// </summary>
    public SelectionMode SelectionMode;

    /// <summary>
    /// Gets the Blazor component type used to render this field (UiPathView)
    /// </summary>
    public override Type ViewType => typeof(UiPathView);

    /// <summary>
    /// Creates a new path field for file or directory selection
    /// </summary>
    /// <param name="cliName">Command-line argument name</param>
    /// <param name="label">Display label in the UI</param>
    /// <param name="selectionMode">The type of path to select (File, Save file, Directory)</param>
    /// <param name="fileExtensions">Optional array of allowed file extensions (e.g., "*.mrc", "*.star")</param>
    /// <param name="helpText">Optional tooltip text</param>
    /// <param name="isAdvanced">Whether this is an advanced option (e.g., detector MTF selection is often an advanced option)</param>
    public UiPath(string cliName, string label, SelectionMode selectionMode, string[] fileExtensions = null, string helpText = "", bool isAdvanced = false)
        : base(cliName, label, helpText, isAdvanced)
    {
        SelectionMode = selectionMode;
        FileExtensions = fileExtensions?.ToList() ?? new List<string>();
    }

    /// <summary>
    /// Gets the full label including permitted file extensions if specified.
    /// </summary>
    public override string FullLabel => $"{Label} {(FileExtensions.Any() ? $"({string.Join(" | ", FileExtensions)})" : "")}";
}