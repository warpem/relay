using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Tools;

namespace Refund.Jobs.Refinement.Masks.CreateMask;

[GenerateReadOnly]
public class CreateMask : RelionJob, IClusterJob
{
    /// <summary>
    /// Defines the size of the job card in the workflow view.
    /// </summary>
    public override int2 CardSquareCount { get; set; } = new(3, 1);

    public override string TypeGuid => "673b59a0-0580-426a-b069-0c991799a2df";

    /// <summary>
    /// The category path for job type selection in the UI.
    /// </summary>
    public override string TypeCategory => "Refinement.Masks.Create mask";
    
    /// <summary>
    /// The full display name of this job type.
    /// </summary>
    public override string TypeName => "Create mask";
    
    /// <summary>
    /// The abbreviated name used in space-constrained UI elements.
    /// </summary>
    public override string TypeNameShort => "Create mask";
    
    /// <summary>
    /// A brief description of the job's purpose.
    /// </summary>
    public override string TypeDescription => "Binarize a map and, optionally, extend and smooth it to create a mask for refinement or post-processing";
    
    /// <summary>
    /// Specifies that this job runs on CPU resources rather than GPU.
    /// Post-processing is typically CPU-bound and doesn't require GPU acceleration.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.CPU;
    
    /// <summary>
    /// This job doesn't have a custom expanded view - it uses the standard job view.
    /// </summary>
    public override Type ExpandedViewType => null;

    /// <summary>
    /// Indicates that this job doesn't support multiple iterations.
    /// Post-processing is a one-time operation applied to a final map.
    /// </summary>
    public override bool IsIterative => false;

    public override string[] SupportedModules => base.SupportedModules.Concat(["cpu"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(["cpu"]).ToArray();

    public override int CoreCount => NThreads;

    public override int MemoryGb => 8;

    /// <summary>CPU-only tool; requests no GPUs.</summary>
    public override int GpuCount => 0;
    
    #region Parameters
    
    #region Mask

    [UiFieldGroup("Mask", 0)]
    [UiDecimalNullable("lowpass", "Low-pass filter", 
                       min: 1, max: 100000, stepSize: 0.1,
                       unit: "Å",
                       helpText: "Lowpass filter for the input map, prior to binarization.")]
    [RelayProperty]
    public decimal? Lowpass { get; set; } = null;
    
    [UiDecimal("ini_threshold", "Binarization threshold",
               min: -100000, max: 100000, stepSize: 0.000001,
               helpText: "Threshold for binarizing the input map to create the initial mask. " +
                         "Everything smaller than this value will be set to zero, and everything equal " +
                         "or larger will be set to one.")]
    [RelayProperty]
    public decimal BinarizationThreshold { get; set; } = 0.01M;
    
    [UiInt("extend_inimask", "Extend mask by",
           min: 0, max: 1000, stepSize: 1,
           unit: "px",
           helpText: "Number of pixels to extend the initial binary mask in all directions. " +
                     "This helps to ensure that the mask fully encompasses the density and " +
                     "doesn't have sharp features.")]
    [RelayProperty]
    public int ExtendMaskBy { get; set; } = 3;
    
    [UiInt("width_soft_edge", "Soft edge width",
           min: 0, max: 1000, stepSize: 1,
           unit: "px",
           helpText: "Width of the soft edge to apply to the mask. " +
                     "A soft edge helps to avoid masking artifacts by " +
                     "gradually tapering the mask to zero.")]
    [RelayProperty]
    public int SoftEdgeWidth { get; set; } = 6;
    
    [UiBool("invert", "Invert mask",
            helpText: "If set to Yes, the mask will be inverted after creation.")]
    [RelayProperty]
    public bool InvertMask { get; set; } = false;
    
    #endregion
    
    #region Helical
    
    [UiFieldGroup("Helical", 1)]
    [UiBool("helix", "Helix",
            helpText: "Generate a mask for a 3D helix.")]
    [RelayProperty]
    public bool Helix { get; set; } = false;

    [UiDecimal("z_percentage", "Z fraction",
               min: 0, max: 1, stepSize: 0.001,
               unit: "%",
               helpText: "Fraction of box size in Z that contains good information of the helix.",
               ConditionalOnField = nameof(Helix),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal ZFraction { get; set; } = 0.3M;
    
    #endregion
    
    #region Compute

    /// <summary>
    /// The number of CPU threads to use for computation.
    /// Post-processing can be parallelized to speed up processing.
    /// </summary>
    [UiFieldGroup("Compute", 2)]
    [UiInt("j", "Number of threads",
           1,
           99999,
           1,
           helpText: "Number of threads to parallelize the computation.")]
    [RelayProperty]
    public int NThreads { get; set; } = 1;

    /// <summary>
    /// Additional command-line arguments to pass to the post-processing program.
    /// This allows for advanced options not exposed in the standard UI.
    /// </summary>
    [UiString("", "Additional arguments",
              isAdvanced: true,
              helpText: "In this box command-line arguments may be provided that are not generated " +
                        "by the GUI. This may be useful for testing developmental options and/or " +
                        "expert use of the program. Specify as --option1 value1 --option2 value2")]
    [RelayProperty]
    public string AdditionalArguments { get; set; } = "";
    
    #endregion
    
    #endregion
    
    #region Results paths

    /// <summary>
    /// Path to the unmasked post-processed map file.
    /// </summary>
    private string ResMaskFile => Path.Combine(DirectoryPath, "mask.mrc");
    
    #endregion
    
    #region Visualization paths

    public string VisLargePath => Path.Combine(DirectoryPath, "orthoslices.png");
    
    #endregion
    
    public const string PortInMap = "Map";
    public const string PortOutMask = "Mask";

    public CreateMask()
    {
        var portInMap = new PortIn(this, typeof(MapList), PortInMap, "Map", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInMap.Name] = portInMap
        });

        var portOutMask = new PortOut(this, typeof(Mask), PortOutMask, "Mask", GetMaskResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutMask.Name] = portOutMask
        });
    }
    
    /// <summary>
    /// Creates a Map resource from the unmasked post-processed map output.
    /// </summary>
    /// <param name="iter">The iteration number (always 0 for non-iterative jobs).</param>
    /// <returns>A Map resource containing the post-processed map and FSC data.</returns>
    private Resource GetMaskResource(int iter)
    {
        return new Mask(ResMaskFile);
    }
    

    /// <summary>
    /// Gets the name of the RELION command used for post-processing.
    /// </summary>
    public override string CommandName => "relion_mask_create";

    /// <summary>
    /// Composes the command-line arguments for the post-processing job.
    /// This prepares input/output paths and parameter settings for RELION.
    /// </summary>
    /// <returns>A dictionary of command arguments to be passed to the RELION post-processing program.</returns>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();
        
        if (!string.IsNullOrWhiteSpace(AdditionalArguments))
            foreach (var kv in ArgumentStringToDictionary(AdditionalArguments))
                result[kv.Key] = kv.Value;

        var map = (PortsIn["Map"].Edges.First().Source.GetResource() as MapList).Maps.First();
        
        // We don't know the types of maps available, so this is the order of preference
        List<string> possibleMaps = new()
        {
            map.PostprocessedVolumePath,
            map.AverageVolumePath,
            map.Half1VolumePath,
            map.Half2VolumePath,
            map.MaskVolumePath
        };
        var mapPath = possibleMaps.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        
        result["i"] = Space.GetRelativePath(mapPath);
        
        result["o"] = Space.GetRelativePath(ResMaskFile);

        return result;
    }
    /// <summary>
    /// Tracks the size of the log file to detect changes.
    /// </summary>
    private long LastLogSize = -1;

    /// <summary>
    /// Monitors and processes log output from the running job.
    /// This method reads the RELION output log, processes it, and makes it
    /// available for display in the UI.
    /// </summary>
    /// <returns>An action to update the log availability status, or null if no update is needed.</returns>
    public override Action TrackProgressLogs()
    {
        var result = base.TrackProgressLogs();
        
        // Ensure results directory exists
        JobTools.EnsureResultsDirectory(RelayResultsDirectoryPath);
        
        int MaxLogsExist = -1;

        #region Track logs
        
        // Check if log file has changed since last check
        if (JobTools.HasLogFileChanged(PathStdOut, ref LastLogSize))
        {
            MaxLogsExist = 0;
            
            // Read and clean log lines
            string[] logLines = File.ReadAllText(PathStdOut).Split('\n');
            logLines = JobTools.CleanProgressBarLines(logLines);

            // Save processed logs
            JobTools.WriteLogFile(string.Join('\n', logLines), LogFilePath(0));
        }
        else if (File.Exists(PathStdOut))
        {
            // No change in log file, keep previous iteration value
            MaxLogsExist = Math.Max(MaxLogsExist, LogsAvailableIteration);
        }
        
        #endregion

        bool ReportUpdate = MaxLogsExist > LogsAvailableIteration;

        if (ReportUpdate)
            return () =>
            {
                result?.Invoke();
                LogsAvailableIteration = MaxLogsExist;
            };
        else
            return result;
    }
    
    /// <summary>
    /// Generates visualizations of the post-processing results when available.
    /// This method creates orthoslice views of the map, FSC curves, and Guinier plots
    /// to help users assess the quality of the post-processing.
    /// </summary>
    /// <returns>An action to update the visualization availability status, or null if no update is needed.</returns>
    public override Action TrackProgressResults()
    {
        var result = base.TrackProgressResults();
        
        // Ensure results directory exists
        JobTools.EnsureResultsDirectory(RelayResultsDirectoryPath);

        int MaxResultsExist = -1;

        if (!File.Exists(ResMaskFile))
            return null;
        
        if (!File.Exists(VisLargePath))
        {
            File.WriteAllText(Path.Combine(DirectoryPath, "dummy.txt"), "hello");
            BakeryWrapper.MapOrthosliceAtlas(ResMaskFile, 1, VisLargePath);
            BakeryWrapper.MapOrthosliceAtlas(ResMaskFile, 1, VisCard(0));
        }

        MaxResultsExist = 0;

        bool ReportUpdate = MaxResultsExist > VisAvailableIteration;

        if (ReportUpdate)
            return () =>
            {
                result?.Invoke();
                VisAvailableIteration = MaxResultsExist;
            };
        else
            return result;
    }
}