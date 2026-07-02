using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using VYaml.Serialization;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Alignment.AlignMiss;

/// <summary>
/// Job that prepares tilt series stacks and runs MissAlignment (a learning-based tilt-series
/// alignment tool) to improve tilt-series alignments. MissAlignment is a standalone GPU tool
/// outside the WarpTools ecosystem, so this job derives directly from <see cref="Job"/> rather
/// than the WarpTools <c>WarpJobGpu</c> base (and therefore does not support GPU worker pools).
/// </summary>
[GenerateReadOnly]
public class AlignMiss : Job, IClusterJob
{
    public override string TypeGuid => "94136ed6-ce7c-4688-8c06-e655f14129f3";

    public override string TypeCategory => "Tilt-series.Alignment.MISS patch tracking";

    public override string TypeName => "MissAlignment";

    public override string TypeNameShort => "MissAlignment";

    public override string TypeDescription => "Creates tilt series stacks and runs MissAlignment to improve alignments";

    public override Type ExpandedViewType => typeof(AlignMissExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    // MissAlignment runs a single GPU command (not a WarpTools per-item worker pool), so it derives
    // directly from Job rather than WarpJobGpu. It still needs GPU cluster resources; QueueType and
    // GpuCount are the WarpJobGpu bits it genuinely uses, replicated here.
    public override JobQueueType QueueType => JobQueueType.GPU;

    public override int GpuCount => NGpus;

    public override int CoreCount => NWorkers * 4 + 4;

    public override int MemoryGb => NWorkers * 6 + 20;

    // MissAlignment lives outside the WarpTools ecosystem, so it does not inherit WarpJob's module
    // set. It still needs the "warp" module in its runtime environment for now; drop "warp" here if
    // the miss-alignment binary is confirmed independent of it.
    public override string[] SupportedModules => base.SupportedModules.Concat(["warp", "missalignment"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(["warp", "gpu", "missalignment"]).ToArray();

    public override bool CanBeFinalized => true;

    #region Progress tracking

    /// <summary>Number of items processed by the job so far.</summary>
    [RelayProperty]
    [Clearable]
    public int NItemsProcessed { get; set; }

    /// <summary>Number of items that failed processing.</summary>
    [RelayProperty]
    [Clearable]
    public int NItemsFailed { get; set; }

    /// <summary>Total number of items to process.</summary>
    [RelayProperty]
    [Clearable]
    public int NItemsTotal { get; set; }

    #endregion

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTiltSeriesSet = "DataSetTs";
    public const string PortInModel = "TrainedModel";
    public const string PortOutTiltSeriesSet = "TiltSeries";
    public const string PortOutModel = "UpdatedModel";
    
    public string ResConfigPath => Path.Combine(DirectoryPath, "config.yaml");
    public string ResModelPath => Path.Combine(DirectoryPath, "model.ckpt");

    /// <summary>Per-item summary JSON emitted by the run; consumed by the expanded view.</summary>
    public string ResProcessedItemsJson => Path.Combine(DirectoryPath, "processed_items.json");
    
    #region Parameters

    /// <summary>
    /// Rescale tilt images to this pixel size; normally 10–15 for cryo data
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Pre-processing", 0)]
    [UiDecimal("", "Pixel size", min: 1, max: 100000, stepSize: 0.1, unit: "Å",
               "Rescale tilt images to this pixel size; normally 10–15 A for cryo data")]
    public decimal AngPix { get; set; } = 10;

    /// <summary>
    /// Apply mask to each image if available; masked areas will be filled with Gaussian noise
    /// </summary>
    // [RelayProperty]
    // [UiBool("mask", "Apply mask",
    //         "Apply mask to each image if available; masked areas will be filled with Gaussian noise")]
    // public bool ApplyMask { get; set; } = true;

    /// <summary>
    /// Override initial tilt axis angle with this value
    /// </summary>
    // [RelayProperty]
    // [UiFieldGroup("Alignment", 1)]
    // [UiDecimalNullable("initial_axis", "Override initial axis angle", -360, 360, 0.001, "°",
    //                    "Override initial tilt axis angle with this value")]
    // public decimal? InitialAxisAngle { get; set; } = null;

    /// <summary>
    /// Fit a new tilt axis angle for the whole dataset
    /// </summary>
    // [RelayProperty]
    // [UiBool("do_axis_search", "Search for tilt axis angle",
    //         "Fit a new tilt axis angle for the whole dataset")]
    // public bool DoAxisAngleSearch { get; set; } = false;
    
    [UiFieldGroup("Alignment", 1)]
    [RelayProperty]
    [UiInt("", "Coarse iterations", min: 0,
            helpText: "Number of coarse iterations done on 2x downsampled data before fine iterations")]
    public int NCoarse { get; set; } = 2;
    
    [RelayProperty]
    [UiInt("", "Fine iterations", min: 0,
           helpText: "Number of fine iterations done at the full requested resolution after coarse iterations")]
    public int NFine { get; set; } = 1;
    
    [RelayProperty]
    [UiBool("", "Model local deformations",
            "Fit local deformations using Warp's spline grid model")]
    public bool DoLocalDeformations { get; set; } = true;

    [RelayProperty]
    [UiInt2("", "Local deformation model", min: 1,
           helpText: "Dimensions of the local deformation model grid in X and Y",
           ConditionalOnField = nameof(DoLocalDeformations), ConditionalOnValue = true)]
    public int2 LocalModel { get; set; } = new int2(3, 3);
    
    [RelayProperty]
    [UiInt("", "Local iterations", min: 0,
           helpText: "Number of iterations done at the full requested resolution to refine the local deformation model",
           ConditionalOnField = nameof(DoLocalDeformations), ConditionalOnValue = true)]
    public int NLocal { get; set; } = 2;
    
    [RelayProperty]
    [UiBool("", "Use CTF if available",
            "Use CTF information during alignment if available in the tilt series metadata")]
    public bool UseCtf { get; set; } = false;
    
    [RelayProperty]
    [UiBool("", "Use different tomogram dimensions",
            helpText: "Use different tomogram dimensions than those specified for the tilt series dataset")]
    public bool UseDifferentTomogramDimensions { get; set; } = false;

    [RelayProperty]
    [UiInt3("", "Tomogram dimensions", min: 1, stepSize: 2,
            helpText: "Change tomogram dimensions to these values",
            ConditionalOnField = nameof(UseDifferentTomogramDimensions), ConditionalOnValue = true,
            Unit = "unbinned pixels")]
    public int3 AlternativeTomogramDimensions { get; set; } = new(4096, 4096, 1000);
    
    [RelayProperty]
    [UiInt("", "Batch size", min: 0,
           helpText: "Mini-batch size used during model training")]
    public int BatchSize { get; set; } = 32;
    
    [RelayProperty]
    [UiInt("", "Window size", min: 0,
           helpText: "Size of the reconstructed sub-volumes (in pixels) used for training and alignment")]
    public int WindowSize { get; set; } = 96;
    
    [RelayProperty]
    [UiInt("", "Maximum number of epochs", min: 0,
           helpText: "Training in each iteration will continue for this many epochs, unless convergence is reached earlier")]
    public int MaxEpochs { get; set; } = 30;
    
    [RelayProperty]
    [UiDecimal("", "Learning rate", min: 0, stepSize: 1e-10,
           helpText: "Learning rate at the beginning of training")]
    public decimal LearningRate { get; set; } = 1e-3M;

    [RelayProperty]
    [UiInt("", "Steps", min: 1,
            helpText: "Number of steps per epoch")]
    public int NSteps { get; set; } = 1000;

    /// <summary>
    /// Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Post-processing", 2)]
    [UiDecimal("", "Minimum FOV fraction", 0, 1, 0.01,
               helpText: "Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts")]
    public decimal MinFov { get; set; } = 0;

    /// <summary>
    /// Delete tilt series stacks generated for Etomo
    /// </summary>
    [RelayProperty]
    [UiBool("delete_intermediate", "Delete intermediate files",
            "Delete tilt series stacks generated for Etomo afterwards")]
    public bool DeleteIntermediate { get; set; } = false;
    
    #region GPU options

    [UiFieldGroup("Resources", 999)]
    [UiInt("", "Number of GPUs",
           helpText: "Number of GPUs to request for this job.",
           min: 1)]
    [RelayProperty]
    public int NGpus { get; set; } = 1;

    [UiInt("perdevice", "Workers per GPU",
           helpText: "Number of workers to use per GPU. Higher values may improve GPU utilization, " +
                     "but will also increase GPU memory consumption.",
           min: 1)]
    [RelayProperty]
    public int PerDevice { get; set; } = 1;

    [UiInt("n-workers", "Reconstruction workers",
           helpText: "Number of reconstruction workers for MissAlignment to use for its data preparation.",
           min: 1,
           max: 99999)]
    [RelayProperty]
    public int NWorkers { get; set; } = 5;

    #endregion
    
    #endregion

    public AlignMiss()
    {
        var portInTiltSeriesSet = new PortIn(this, typeof(TiltSeriesSet), PortInTiltSeriesSet, "Pre-aligned tilt-series", 1, 1);
        var portInModel = new PortIn(this, typeof(MissAlignmentModel), PortInModel, "Pre-trained model", 0, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTiltSeriesSet] = portInTiltSeriesSet,
            [PortInModel] = portInModel
        });

        var portOutTiltSeriesSet = new PortOut(this, typeof(TiltSeriesSet), PortOutTiltSeriesSet, "Improved alignments", GetTiltSeriesResource);
        var portOutModel = new PortOut(this, typeof(MissAlignmentModel), PortOutModel, "Updated model", GetModelResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTiltSeriesSet] = portOutTiltSeriesSet,
            [PortOutModel] = portOutModel
        });
    }

    private TiltSeriesSet GetTiltSeriesResource(int iter)
    {
        if (!PortsIn[PortInTiltSeriesSet].IsConnected)
            return null;

        var previousTs = PortsIn[PortInTiltSeriesSet].GetSingleResource<TiltSeriesSet>();
        
        previousTs.DataSet.SettingsPath = Path.Combine(DirectoryPath, "processing.settings");

        if (previousTs.DataSet == null)
            throw new InvalidOperationException("Tilt-series data set input not found.");
        
        if (previousTs.DataSet.Micrographs == null)
            throw new InvalidOperationException("Tilt-series data set must include micrographs.");

        previousTs.HasMetadata = true;
        previousTs.LatestMetadataDirectory = DirectoryPath;

        if (!DeleteIntermediate)
        {
            previousTs.TiltStackDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.TiltStackDirName);
            previousTs.ToTiltStackPath = (name) => WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToTiltStackPath(name));
            previousTs.ToTiltStackThumbnailPath = (tsName, fsName) => Path.Combine(DirectoryPath, TiltSeries.ToTiltStackThumbnailPath(tsName, fsName));
        }

        return previousTs;
    }
    
    private MissAlignmentModel GetModelResource(int iter)
    {
        return new MissAlignmentModel()
        {
            ModelPath = ResModelPath
        };
    }

    /// <summary>Creates the SUCCESS marker on the compute node when the command exits cleanly.</summary>
    public override string CommandSuffix => $" && touch {PathSuccess}";

    /// <summary>
    /// Gets the name of the command used to run MissAlignment.
    /// </summary>
    public override string CommandName => $"miss-alignment";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["config-file"] = ResConfigPath;
        result["prepare-stacks"] = AngPix.ToString(CultureInfo.InvariantCulture);

        return result;
    }

    public override void Stage()
    {
        base.Stage();

        var tiltSeriesSet = PortsIn[PortInTiltSeriesSet].GetSingleResource<TiltSeriesSet>();
        var modelResource = PortsIn[PortInModel].GetSingleResource<MissAlignmentModel>();

        if (tiltSeriesSet == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        if (!tiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt-series input must have metadata.");

        Directory.CreateDirectory(DirectoryPath);
        
        foreach (var file in Directory.EnumerateFiles(tiltSeriesSet.LatestMetadataDirectory, "*.xml"))
            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), true);
        
        #region Set physical volume dimensions in the metadata
        
        if (UseDifferentTomogramDimensions)
            tiltSeriesSet.DataSet.TomogramDimensions = AlternativeTomogramDimensions;
        
        var optionsWarp = tiltSeriesSet.DataSet.ToOptionsWarp().GetProcessingTomoFullReconstruction();
        
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.xml"))
        {
            var ts = new TiltSeries(file);
            ts.VolumeDimensionsPhysical = optionsWarp.DimensionsPhysical;
            ts.SaveMeta();
        }
        
        #endregion

        List<MissIterationSetting> iterationSettings = new();

        for (int i = 0; i < NCoarse; i++)
            iterationSettings.Add(new MissIterationSetting
            {
                Downsample = 2, 
                Alignment = "anchoring"
            });

        for (int i = 0; i < NFine; i++)
            iterationSettings.Add(new MissIterationSetting
            {
                Downsample = 1, 
                Alignment = "global"
            });
        
        if (DoLocalDeformations)
            for (int i = 0; i < NLocal; i++)
                iterationSettings.Add(new MissIterationSetting()
                {
                    Downsample = 1,
                    Alignment = new int[] { LocalModel.X, LocalModel.Y }
                });
        
        GenerateConfig(ResConfigPath,
                       DirectoryPath,
                       iterationSettings,
                       tiltSeriesSet.HasCtf && UseCtf,
                       modelResource?.ModelPath,
                       (double)LearningRate,
                       MaxEpochs,
                       BatchSize,
                       WindowSize,
                       NSteps,
                       "default");
    }
    private void GenerateConfig(string outputPath, 
                                    string trainingDir,
                                    List<MissIterationSetting> iterations,
                                    bool useCtf,
                                    string? modelCheckpoint,
                                    double learningRate,
                                    int epochsPerIteration,
                                    int batchSize,
                                    int patchSize,
                                    int stepsPerEpoch,
                                    string modelArch)
    {
        var config = new MissAlignmentConfig
        {
            General = new MissGeneralConfig
            {
                TrainingDirectory = trainingDir,
                ApplyCtf = useCtf,
                IterationSettings = iterations,
                Seed = 123
            },
            ModelTraining = new MissModelTrainingConfig
            {
                ModelArchitecture = modelArch,
                ModelCheckpoint = modelCheckpoint,
                LossMargin = 0.5,
                LearningRate = learningRate,
                WeightDecay = 1e-4,
                MaxEpochsPerIteration = epochsPerIteration,
                WarmupSteps = 500,
                MultistepLrScheduler = new MissLrSchedulerConfig
                {
                    Milestones = [ 5, 15 ],
                    Gamma = 0.5
                }
            },
            DataLoading = new MissDataLoadingConfig
            {
                BatchSize = batchSize,
                PatchSize = patchSize,
                StepsPerEpoch = stepsPerEpoch
            },
            ShiftGeneration = new MissShiftGenerationConfig
            {
                TrajectoryProbability = 0.5,
                TrajectoryMaxShift = 10.0,
                JitterProbability = 0.5,
                JitterMaxStd = 2.0,
                OutlierProbability = 0.5,
                OutlierMaxShift = 20.0,
                FractureProbability = 0.5,
                FractureMaxShift = 30.0
            },
            TiltSeriesAlignment = new MissTiltSeriesAlignmentConfig
            {
                PatchSize = patchSize,
                PatchOverlap = 0.5,
                BatchSize = 16
            }
        };

        var yaml = YamlSerializer.SerializeToString(config);
        File.WriteAllText(outputPath, yaml);
    }

    /// <summary>
    /// Monitors and parses log output from the running job. Replicated from WarpJob so MissAlignment
    /// no longer depends on the WarpTools hierarchy. Currently reuses the WarpTools progress format;
    /// this is the seam to swap in a miss-alignment-specific parser once its output is finalized.
    /// </summary>
    public override Action TrackProgressLogs()
    {
        var baseResult = base.TrackProgressLogs();

        if (!File.Exists(PathStdOut))
            return baseResult;

        // Read log file
        string[] logLines = File.ReadAllText(PathStdOut).Split('\n');

        if (logLines.Length == 0)
            return null;

        // Clean log lines to remove progress bar characters
        logLines = JobTools.CleanProgressBarLines(logLines);

        // Parse progress information
        WarpTools.ExtractProgressInfo(logLines,
                                      out int itemsProcessed,
                                      out int itemsTotal,
                                      out int itemsFailed,
                                      out string remainingTime);

        // Save processed logs
        JobTools.WriteLogFile(string.Join('\n', logLines), LogFilePath(0));

        // Parse remaining time string and update TimeRemaining property
        TimeSpan? timeRemaining = WarpTools.ParseRemainingTimeToCompletion(remainingTime);

        // Return update action if needed
        if (LogsAvailableIteration < 0 ||
            NItemsProcessed != itemsProcessed ||
            NItemsTotal != itemsTotal ||
            NItemsFailed != itemsFailed ||
            TimeRemaining != timeRemaining)
            return () =>
            {
                baseResult?.Invoke();

                NItemsProcessed = itemsProcessed;
                NItemsTotal = itemsTotal;
                NItemsFailed = itemsFailed;
                TimeRemaining = timeRemaining;
                LogsAvailableIteration = 0;
            };

        return baseResult;
    }

    public override void FinalizeRun(Action<Job, Action<Job>> updateCallback)
    {
        base.FinalizeRun(updateCallback);

        using (File.CreateText(PathSuccess)) ;

        {
            var action = TrackProgressLogs();

            if (action != null)
                updateCallback(this, _ => action());
        }

        while (TrackProgressResults() is { } updateActionResults)
        {
            var action = updateActionResults;
            updateCallback(this, _ => action());
        }
    }
}

public enum MissModel
{
    simple,
    attention
}