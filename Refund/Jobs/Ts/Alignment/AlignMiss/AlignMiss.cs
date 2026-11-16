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
/// outside the WarpTools ecosystem, so this job derives directly from <see cref="Job"/> and uses
/// MissAlignment's own worker pool rather than the WarpTools <c>WarpToolsJobGpu</c> pool.
/// </summary>
[GenerateReadOnly]
public class AlignMiss : Job, IClusterJob, IItemProgress
{
    public override string TypeGuid => "94136ed6-ce7c-4688-8c06-e655f14129f3";

    public override string TypeCategory => "Tilt-series.Alignment.MISS patch tracking";

    public override string TypeName => "MissAlignment";

    public override string TypeNameShort => "MissAlignment";

    public override string TypeDescription => "Creates tilt series stacks and runs MissAlignment to improve alignments";

    public override Type ExpandedViewType => typeof(AlignMissExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    // MissAlignment runs a single GPU command (not a WarpTools per-item worker pool), so it derives
    // directly from Job rather than WarpToolsJobGpu. It still needs GPU cluster resources; QueueType and
    // GpuCount are the WarpToolsJobGpu bits it genuinely uses, replicated here.
    public override JobQueueType QueueType => JobQueueType.GPU;

    public override int GpuCount => NGpus;

    // CPU cores follow miss-alignment's documented sizing:
    // len(reconstruction-devices) + n_training_devices * dataloaders_per_trainer, plus a small
    // orchestrator overhead.
    public override int CoreCount => DoTraining
        ? ReconstructionDevices.Length + TrainingDevices.Length * NWorkers + 2
        : NGpus * 2 + 2;

    public override int MemoryGb => DoTraining
        ? ReconstructionDevices.Length * 4 + TrainingDevices.Length * NWorkers + 8
        : NGpus * 4 + 8;

    // MissAlignment lives outside the WarpTools ecosystem, so it does not inherit WarpToolsJob's module
    // set. It still needs the "warp" module in its runtime environment for now; drop "warp" here if
    // the miss-alignment binary is confirmed independent of it.
    public override string[] SupportedModules => base.SupportedModules.Concat(["warp", "missalignment"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(["warp", "gpu", "missalignment"]).ToArray();

    public override bool CanBeFinalized => true;

    #region Progress tracking

    /// <summary>Number of items processed so far (null until the job starts reporting).</summary>
    [RelayProperty]
    [Clearable]
    public int? NItemsProcessed { get; set; }

    /// <summary>Number of items that failed processing (null until the job starts reporting).</summary>
    [RelayProperty]
    [Clearable]
    public int? NItemsFailed { get; set; }

    /// <summary>Total number of items to process (null until the job starts reporting).</summary>
    [RelayProperty]
    [Clearable]
    public int? NItemsTotal { get; set; }

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

    [RelayProperty]
    [UiFieldGroup("Model", 0)]
    [UiBool("", "Train models",
            "Train models on this dataset before alignment. Leave off to use existing models.")]
    public bool DoTraining { get; set; } = false;

    [RelayProperty]
    [UiBool("", "Use general pretrained models",
            "Use the general easymode models by Mart So-Last and their eight-step alignment schedule. " +
            "Turn off to use the connected model run and its original schedule.",
            ConditionalOnField = nameof(DoTraining), ConditionalOnValue = false)]
    public bool UseGeneralModel { get; set; } = true;

    /// <summary>
    /// Rescale tilt images to this pixel size; normally 10–15 for cryo data
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Pre-processing", 1)]
    [UiDecimal("", "Pixel size", min: 1, max: 100000, stepSize: 0.1, unit: "Å",
               "Rescale tilt images to this pixel size; normally 10–15 A for cryo data")]
    public decimal AngPix { get; set; } = 10;
    
    [UiFieldGroup("Alignment", 2)]
    [RelayProperty]
    [UiInt("", "Coarse iterations", min: 0,
            helpText: "Number of coarse iterations done on 2x downsampled data before fine iterations",
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    public int NCoarse { get; set; } = 2;
    
    [RelayProperty]
    [UiInt("", "Fine iterations", min: 0,
           helpText: "Number of fine iterations done at the full requested resolution after coarse iterations",
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    public int NFine { get; set; } = 1;
    
    [RelayProperty]
    [UiBool("", "Model local deformations",
            "Fit local deformations using Warp's spline grid model",
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
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
    [UiFieldGroup("Training", 3)]
    [UiInt("", "Batch size", min: 0,
           helpText: "Mini-batch size used during model training",
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    public int BatchSize { get; set; } = 32;
    
    [RelayProperty]
    [UiInt("", "Window size", min: 0,
           helpText: "Size of the reconstructed sub-volumes (in pixels) used for training and alignment",
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    public int WindowSize { get; set; } = 96;
    
    [RelayProperty]
    [UiInt("", "Maximum number of epochs", min: 0,
           helpText: "Training in each iteration will continue for this many epochs, unless convergence is reached earlier",
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    public int MaxEpochs { get; set; } = 30;
    
    [RelayProperty]
    [UiDecimal("", "Learning rate", min: 0, stepSize: 1e-5,
           helpText: "Learning rate at the beginning of training",
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    public decimal LearningRate { get; set; } = 1e-3M;

    [RelayProperty]
    [UiInt("", "Steps", min: 1,
            helpText: "Number of steps per epoch",
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    public int NSteps { get; set; } = 1000;

    /// <summary>
    /// Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Post-processing", 4)]
    [UiDecimal("", "Minimum FOV fraction", 0, 1, 0.01,
               helpText: "Disable tilts that contain less than this fraction of the tomogram's field of view due to excessive shifts")]
    public decimal MinFov { get; set; } = 0;

    #region GPU options

    [UiFieldGroup("Resources", 999)]
    [UiInt("", "Number of GPUs",
           helpText: "Number of GPUs to request for this job.",
           min: 1)]
    [RelayProperty]
    public int NGpus { get; set; } = 2;

    // Composed into the --reconstruction-devices list (each GPU index repeated PerDevice times),
    // so the CliName is empty; ComposeCommandArguments builds the list explicitly.
    [UiInt("", "Reconstruction workers per GPU",
           helpText: "Number of reconstruction workers to run on each GPU. Higher values may improve " +
                     "GPU utilization but increase GPU memory consumption.",
           min: 1,
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    [RelayProperty]
    public int PerDevice { get; set; } = 8;

    [UiInt("", "DataLoader workers per trainer",
           helpText: "Number of CPU data-loading workers per training GPU (--dataloaders-per-trainer).",
           min: 1,
           max: 99999,
           ConditionalOnField = nameof(DoTraining), ConditionalOnValue = true)]
    [RelayProperty]
    public int NWorkers { get; set; } = 5;

    [UiFieldGroup("Resources", 999)]
    [UiBool("", "Use worker pool",
            "Let MissAlignment spawn its own cluster worker pool for stack preparation and alignment " +
            "(the tool's internal --n-cluster-workers pool, managed inside the run — not a Relay pool).")]
    [RelayProperty]
    public bool UseWorkerPool { get; set; } = false;

    [UiInt("n-cluster-workers", "Pool size", min: 1,
           helpText: "Number of cluster workers in MissAlignment's alignment pool.",
           ConditionalOnField = nameof(UseWorkerPool), ConditionalOnValue = true)]
    [RelayProperty]
    public int PoolSize { get; set; } = 4;

    #endregion
    
    #endregion

    public AlignMiss()
    {
        var portInTiltSeriesSet = new PortIn(this, typeof(TiltSeriesSet), PortInTiltSeriesSet, "Pre-aligned tilt-series", 1, 1);
        var portInModel = new PortIn(this, typeof(MissAlignmentModel), PortInModel, "Trained models", 0, 1,
            isActiveDelegate: job => ((AlignMiss)job).DoTraining || !((AlignMiss)job).UseGeneralModel);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTiltSeriesSet] = portInTiltSeriesSet,
            [PortInModel] = portInModel
        });

        var portOutTiltSeriesSet = new PortOut(this, typeof(TiltSeriesSet), PortOutTiltSeriesSet, "Improved alignments", GetTiltSeriesResource);
        var portOutModel = new PortOut(this, typeof(MissAlignmentModel), PortOutModel, "Trained models", GetModelResource);

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

        // miss-alignment writes the prepared tilt stacks to the job directory and never deletes them
        // (it only auto-cleans its own temporary reconstruction pool), so always expose them.
        previousTs.TiltStackDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.TiltStackDirName);
        previousTs.ToTiltStackPath = (name) => WarpHelper.PathCombine(DirectoryPath, TiltSeries.ToTiltStackPath(name));
        previousTs.ToTiltStackThumbnailPath = (tsName, fsName) => Path.Combine(DirectoryPath, TiltSeries.ToTiltStackThumbnailPath(tsName, fsName));

        return previousTs;
    }
    
    private MissAlignmentModel GetModelResource(int iter)
    {
        if (!DoTraining)
            return UseGeneralModel ? null : PortsIn[PortInModel].GetSingleResource<MissAlignmentModel>();

        return new MissAlignmentModel
        {
            ModelPath = ResModelPath,
            ModelRunDirectory = DirectoryPath,
            ConfigPath = ResConfigPath
        };
    }

    /// <summary>Creates the SUCCESS marker on the compute node when the command exits cleanly.</summary>
    public override string CommandSuffix => $" && touch {PathSuccess}";

    /// <summary>
    /// Selects training or inference using MissAlignment's corresponding CLI subcommand.
    /// </summary>
    public override string CommandName => DoTraining ? "miss-alignment train" : "miss-alignment infer";

    // miss-alignment takes explicit device-index lists, not counts. The cluster exposes the requested
    // GPUs as indices 0..NGpus-1. Training runs on a single GPU (device 0); every other GPU is
    // dedicated to reconstruction. Empirically this beats splitting GPUs evenly — training gains
    // little from extra devices while reconstruction throughput scales with them. With a single GPU
    // the two necessarily share device 0.
    private int[] TrainingDevices => new[] { 0 };

    // GPUs dedicated to reconstruction: every GPU except the training one, or device 0 itself in the
    // single-GPU case where training and reconstruction must share.
    private int[] ReconstructionGpus =>
        NGpus <= 1 ? new[] { 0 } : Enumerable.Range(1, NGpus - 1).ToArray();

    // One --reconstruction-devices entry per worker; each reconstruction GPU hosts PerDevice workers.
    private int[] ReconstructionDevices =>
        ReconstructionGpus.SelectMany(gpu => Enumerable.Repeat(gpu, PerDevice)).ToArray();

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["config-file"] = ResConfigPath;
        result["prepare-stacks"] = AngPix.ToString(CultureInfo.InvariantCulture);

        if (DoTraining)
        {
            result["training-devices"] = string.Join(",", TrainingDevices);
            result["reconstruction-devices"] = string.Join(",", ReconstructionDevices);
            result["dataloaders-per-trainer"] = NWorkers.ToString(CultureInfo.InvariantCulture);
        }

        return result;
    }

    public override void ReadFromJson(JsonNode reader)
    {
        base.ReadFromJson(reader);
        // Older saved jobs always trained; only newly created jobs default to inference.
        if (reader[nameof(DoTraining)] == null)
            DoTraining = true;
    }

    public override Dictionary<string, List<string>> ValidatePortInputs()
    {
        var errors = base.ValidatePortInputs();
        if (!DoTraining && !UseGeneralModel && !PortsIn[PortInModel].IsConnected)
            AddPortValidationError(errors, PortInModel, "Connect a trained model run or enable the general pretrained models.");
        return errors;
    }

    public override void Stage()
    {
        base.Stage();

        var tiltSeriesSet = PortsIn[PortInTiltSeriesSet].GetSingleResource<TiltSeriesSet>();
        var modelResource = PortsIn[PortInModel].IsActive()
            ? PortsIn[PortInModel].GetSingleResource<MissAlignmentModel>()
            : null;

        if (tiltSeriesSet == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        if (!tiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt-series input must have metadata.");

        // Validate the selected model before copying or changing any tilt-series metadata.
        string inferenceConfig = DoTraining ? null
            : GenerateInferenceConfig(modelResource, tiltSeriesSet.HasCtf && UseCtf);

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

        if (!DoTraining)
        {
            File.WriteAllText(ResConfigPath, inferenceConfig);
            return;
        }

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

    private string GenerateInferenceConfig(MissAlignmentModel model, bool useCtf)
    {
        var general = new Dictionary<string, object>
        {
            ["data_directory"] = DirectoryPath,
            ["apply_ctf"] = useCtf,
            ["seed"] = 123
        };
        var config = new Dictionary<string, object> { ["general"] = general };

        if (!UseGeneralModel)
        {
            if (model == null)
                throw new InvalidOperationException("Connect a trained model run or enable the general pretrained models.");
            string modelRunDirectory = model.ModelRunDirectory;
            if (string.IsNullOrWhiteSpace(modelRunDirectory) && !string.IsNullOrWhiteSpace(model.ModelPath))
                modelRunDirectory = Path.GetDirectoryName(model.ModelPath);
            if (string.IsNullOrWhiteSpace(modelRunDirectory))
                throw new InvalidOperationException("The connected model does not identify a training run.");
            string configPath = string.IsNullOrWhiteSpace(model.ConfigPath)
                ? Path.Combine(modelRunDirectory, "config.yaml")
                : model.ConfigPath;
            if (!File.Exists(configPath))
                throw new InvalidOperationException("Inference requires the connected model's complete training run, " +
                    "including config.yaml and iterN/model.ckpt files; a single checkpoint is insufficient.");
            var trained = YamlSerializer.Deserialize<MissAlignmentConfig>(File.ReadAllBytes(configPath));
            var iterations = trained?.General?.IterationSettings;
            if (iterations == null || iterations.Count == 0 || trained.TiltSeriesAlignment == null)
                throw new InvalidDataException("The connected model run is missing its alignment schedule or patch settings.");
            for (int i = 1; i <= iterations.Count; i++)
            {
                string checkpoint = Path.Combine(modelRunDirectory, $"iter{i}", "model.ckpt");
                if (!File.Exists(checkpoint))
                    throw new FileNotFoundException($"The connected model run is missing iteration {i}.", checkpoint);
            }
            general["model_run_directory"] = modelRunDirectory;
            general["iteration_settings"] = iterations.Select(setting => new Dictionary<string, object>
            {
                ["downsample"] = setting.Downsample,
                ["alignment"] = setting.Alignment
            }).ToArray();
            config["tilt_series_alignment"] = new Dictionary<string, object>
            {
                ["patch_size"] = trained.TiltSeriesAlignment.PatchSize,
                ["patch_overlap"] = trained.TiltSeriesAlignment.PatchOverlap,
                ["batch_size"] = trained.TiltSeriesAlignment.BatchSize
            };
        }
        // Omit schedule/model keys for the general models so the CLI uses its pinned defaults.
        return YamlSerializer.SerializeToString(config);
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
    /// Monitors and parses log output from the running job. Replicated from WarpToolsJob so MissAlignment
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