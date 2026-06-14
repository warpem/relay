using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Headers;
using Warp.Tools;

namespace Refund.Jobs.Common.Import.ImportMap;

/// <summary>
/// Job for importing 3D density maps into the system.
/// This job copies an existing map file (in MRC/MAP format) into the project workspace,
/// making it available for further processing and visualization.
/// </summary>
/// <remarks>
/// Maps are fundamental data types in cryo-EM processing that represent 3D density distributions. 
/// This is typically one of the first jobs created when working with existing map data.
/// The job supports standard cryo-EM map formats (MRC, MAP) and can optionally override metadata.
/// </remarks>
[GenerateReadOnly]
public class ImportMap : Job, ILocalJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import map job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(3, 1);

    public override string TypeGuid => "86880100-8c4e-4d66-992a-9e0796368397";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "Common.Import.Map";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Import map";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Import map";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Imports a single map";

    /// <summary>
    /// Gets the queue type this job should be submitted to.
    /// Import jobs run locally as they typically involve only file I/O operations.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;

    /// <summary>
    /// Gets whether this job produces iterative results.
    /// Import jobs are non-iterative as they simply copy existing files.
    /// </summary>
    public override bool IsIterative => false;

    /// <summary>
    /// Gets the type of the expanded view component for this job.
    /// Import jobs do not have a specialized expanded view.
    /// </summary>
    public override Type ExpandedViewType => typeof(ImportMapExpandedView);

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortOutMap = "Map";

    #region Parameters

    /// <summary>
    /// Gets or sets the path to the map file to be imported.
    /// Must point to a valid MRC/MAP file on the filesystem.
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiPath("", "File path",
            SelectionMode.SingleFile,
            ["*.map" , "*.mrc"],
            helpText: "Path to the MRC file to be imported.")]
    [RelayProperty]
    public string FilePath { get; set; } = "";

    /// <summary>
    /// Gets or sets the pixel size (voxel dimension) override.
    /// When specified, this value replaces the pixel size stored in the map header.
    /// This is useful when the map's embedded metadata is incorrect or missing.
    /// </summary>
    [UiDecimalNullable("", "Pixel size",
                       min: 0.001,
                       max: 1000.0,
                       stepSize: 0.001,
                       helpText: "Override the pixel size value stored in the map's header.",
                       Unit = "Å")]
    [RelayProperty]
    public decimal? PixelSize { get; set; } = null;

    #endregion

    /// <summary>
    /// Gets the path where the imported map will be stored within the job directory.
    /// </summary>
    public string ResMapPath => Path.Combine(DirectoryPath, "map.mrc");

    /// <summary>
    /// Gets the path where the visualization of the imported map will be stored.
    /// </summary>
    public string VisLargePath => Path.Combine(RelayResultsDirectoryPath, "orthoslices.png");

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public ImportMap()
    {
        PortsIn = new(new Dictionary<string, PortIn>());

        var portOutMap = new PortOut(this, typeof(MapList), PortOutMap, "Map", GetMap);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutMap.Name] = portOutMap
        });
    }

    /// <summary>
    /// Validates the job parameters before execution.
    /// Checks if the specified file path exists and has a valid format.
    /// </summary>
    /// <returns>A dictionary of validation errors, if any</returns>
    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = new Dictionary<string, string>();
        
        var propertyName = nameof(ImportMap.FilePath);
        var property = typeof(ImportMap).GetProperty(propertyName);
        var uiPath = property?.GetCustomAttributes(typeof(UiPath), false).FirstOrDefault() as UiPath;
        var messageFilePath = Helper.ValidatePath(FilePath, uiPath?.FileExtensions);
        if (!string.IsNullOrWhiteSpace(messageFilePath))
            errors[propertyName] = messageFilePath;

        //TODO: Implement validation for the rest of the parameters
        return errors;
    }

    /// <summary>
    /// Creates and returns a Map resource from the imported map file.
    /// This method is called by the output port to provide data to downstream jobs.
    /// </summary>
    /// <param name="iter">The iteration number (not used as this job is non-iterative)</param>
    /// <returns>A Map resource pointing to the imported map file</returns>
    private MapList GetMap(int iter)
    {
        return new MapList([new Map(averageVolumePath: ResMapPath)]);
    }

    /// <summary>
    /// Executes the map import operation locally.
    /// Copies the source file to the job directory and logs file metadata.
    /// </summary>
    /// <param name="token">Cancellation token for aborting the operation</param>
    public void RunLocal(CancellationToken token)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            ((StreamWriter)logger).AutoFlush = true;
            
            logger.WriteLine($"Importing map from {FilePath}");

            logger.Write("Copying map file... ");
            {
                File.Copy(FilePath, Path.Combine(DirectoryPath, ResMapPath));
            }
            logger.WriteLine("Done.");

            MapHeader Header = MapHeader.ReadFromFile(Path.Combine(DirectoryPath, ResMapPath));
            logger.WriteLine($"Map dimensions: {Header.Dimensions}");
            logger.WriteLine($"Pixel size: {Header.PixelSize.X} Å");

            logger.WriteLine("Map imported successfully");
        }
    }

    /// <summary>
    /// Tracks the progress of log generation for this job.
    /// Used to notify the UI when logs become available.
    /// </summary>
    /// <returns>An action to execute when logs become available, or null if no update is needed</returns>
    public override Action TrackProgressLogs()
    {
        if (LogsAvailableIteration < 0)
            return () =>
            {
                LogsAvailableIteration = 0;
            };
        
        return null;
    }

    /// <summary>
    /// Tracks the progress of results generation for this job.
    /// For import map jobs, this generates orthoslice visualizations of the map.
    /// </summary>
    /// <returns>An action to execute when results become available, or null if no update is needed</returns>
    public override Action TrackProgressResults()
    {
        var result = base.TrackProgressResults();
        
        if (VisAvailableIteration < 0 &&
            File.Exists(ResMapPath) &&
            !File.Exists(VisLargePath))
        {
            BakeryWrapper.MapOrthosliceAtlas(ResMapPath, 1, VisLargePath);
            BakeryWrapper.MapOrthosliceAtlas(ResMapPath, 1, VisCard(0));

            return () =>
            {
                result?.Invoke();
                VisAvailableIteration = 0;
            };
        }

        return result;
    }
}