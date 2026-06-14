using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs._3D.Class3D;
using Refund.UIFields;
using Refund.Utils;
using Serilog;
using Warp;
using Warp.Tools;

namespace Refund.Jobs._3D.InitialReference3D;

[GenerateReadOnly]
public class InitialReference : RelionJob, IClusterJob
{
    public override string TypeGuid => "bb1636d7-cd2c-423b-b410-8bb324d9cfa2";
    public override string TypeCategory => "Refinement.Initial model.Initial reference";

    public override string TypeName => "Initial reference";

    public override string TypeNameShort => "IniRef";

    public override string TypeDescription => "Prepares one or multiple initial, unbiased 3D references";

    public override Type ExpandedViewType => typeof(InitialReferenceExpandedView);

    public override JobQueueType QueueType => UseGpu ? JobQueueType.GPU : JobQueueType.CPU;

    public override bool IsIterative => true;

    public override bool CanBeFinalized => true;

    public override int2 CardSquareCount
    {
        get
        {
            var nClasses = NClasses;

            if (nClasses <= 5)
                return new int2(Math.Max(2, nClasses), 1);
            else
                return new int2(Math.Min(5, (nClasses + 3) / 4), 1);
        }
        set { }
    }

    public override string[] SupportedModules => base.SupportedModules.Concat(["gpu", "cpu"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(UseGpu ? ["gpu"] : ["cpu"]).ToArray();

    public int ResultsEveryNIterations { get; set; } = 10;

    public override int CoreCount => NThreads;

    public override int MemoryGb => MemoryPerWorker;

    public override int GpuCount => UseGpu ? NGpus : 0;

    #region Parameters

    #region Optimization

    [UiFieldGroup("Optimization", 0)]
    [UiInt("K", "Number of references",
           min: 1,
           max: 100,
           stepSize: 1,
           helpText: "The number of classes for a multi-reference ab initio VDAM refinement. " +
                     "These classes will be made in an unsupervised mannger, starting from a " +
                     "single reference in the initial iterations, and the references will become " +
                     "increasingly dissimilar during the later iterations.")]
    [RelayProperty]
    public int NClasses { get; set; } = 1;

    [UiInt("iter", "Number of iterations",
           min: 10,
           max: 10000,
           stepSize: 10,
           helpText: "The number of mini-batch iterations for the VDAM refinement.")]
    [RelayProperty]
    public int NIterations { get; set; } = 100;

    [UiDecimal("tau2_fudge", "Regularization parameter T",
               min: 0.1,
               max: 100.0,
               stepSize: 0.1,
               isAdvanced: true,
               helpText: "Bayes law strictly determines the relative weight between the contribution " +
                         "of the experimental data and the prior. However, in practice one may need " +
                         "to adjust this weight to put slightly more weight on the experimental data " +
                         "to allow optimal results. Values greater than 1 for this regularisation " +
                         "parameter (T in the JMB2011 paper) put more weight on the experimental data. " +
                         "Values around 2-4 have been observed to be useful for 3D initial model calculations.")]
    [RelayProperty]
    public decimal TauFudge { get; set; } = 4m;

    [UiInt("particle_diameter", "Mask diameter",
           min: 1,
           max: 10000,
           stepSize: 1,
           helpText: "The experimental images will be masked with a soft circular mask with this " +
                     "diameter. Make sure this radius is not set too small because that may mask " +
                     "away part of the signal! If set to a value larger than the image size no masking " +
                     "will be performed.")]
    [RelayProperty]
    public int MaskDiameter { get; set; } = 200;

    [UiBool("flatten_solvent", "Flatten and enforce non-negative solvent",
            isAdvanced: true,
            helpText: "If set to Yes, the algorithm will apply a spherical mask and enforce all " +
                      "values in the reference to be non-negative.")]
    [RelayProperty]
    public bool FlattenSolvent { get; set; } = true;

    [UiSymmetry("sym", "Symmetry",
                helpText: "The symmetry of the refined references. The default is C1, which means no " +
                          "symmetry. If you know the symmetry of the particle, you can set it here. " +
                          "This will help the algorithm to converge faster.")]
    [RelayProperty]
    public string Symmetry { get; set; } = "C1";

    [UiBool("denovo_3dref", "Run in C1 and apply symmetry later",
            isAdvanced: true,
            helpText: "If set to Yes, the gradient-driven optimization is run in C1 and the symmetry " +
                      "orientation is searched and applied later. If set to No, the entire " +
                      "optimization is run in the symmetry point group indicated above.")]
    [RelayProperty]
    public bool ApplySymmetryLater { get; set; } = false;

    [UiDecimal("sigma_tilt", "Tilt prior",
               min: -1,
               max: 180,
               stepSize: 0.1,
               isAdvanced: true,
               helpText: "The width of the prior on the tilt angle: angular searches will be +/-3 " +
                         "times this value. Tilt priors will be defined when particles have been " +
                         "picked as filaments, on spheres or on manifolds. Setting this width to a " +
                         "negative value will lead to no prior being used on the tilt angle.",
               Unit = "°")]
    [RelayProperty]
    public decimal TiltPrior { get; set; } = -1m;

    #endregion

    #region CTF

    [UiFieldGroup("CTF", 1)]
    [UiBool("ctf", "Do CTF-correction?",
            isAdvanced: true,
            helpText: "If set to Yes, CTFs will be corrected inside the MAP refinement. " +
                      "The resulting algorithm intrinsically implements the optimal linear, " +
                      "or Wiener filter. \n\n" +
                      "Also make sure that your data's pixel size is correct!")]
    [RelayProperty]
    public bool DoCtfCorrection { get; set; } = true;

    [UiBool("ctf_intact_first_peak", "Ignore CTF until first peak?",
            isAdvanced: true,
            helpText: "If set to Yes, then CTF-amplitude correction will only be performed " +
                      "from the first peak of each CTF onward. This can be useful if the " +
                      "CTF model is inadequate at the lowest resolution. Still, in general " +
                      "using higher amplitude contrast on the CTFs (e.g. 0.1–0.2%) often " +
                      "yields better results. Therefore, this option is not generally " +
                      "recommended: Try processing your data with higher amplitude contrast first!")]
    [RelayProperty]
    public bool IgnoreCtfUntilFirstPeak { get; set; } = false;

    #endregion

    #region Compute

    [UiFieldGroup("Compute", 2)]
    [UiString("scratch_dir", "Use scratch directory",
              isAdvanced: true,
              helpText: "If a directory is provided here, then the job will create a sub-directory " +
                        "in it called relion_volatile. If that relion_volatile directory already " +
                        "exists, it will be wiped. Then, the program will copy all input particles " +
                        "into a large stack inside the relion_volatile subdirectory. Provided this " +
                        "directory is on a fast local drive (e.g. an SSD drive), processing in all " +
                        "the iterations will be faster. If the job finishes correctly, the " +
                        "relion_volatile directory will be wiped. If the job crashes, you may want " +
                        "to remove it yourself.")]
    [RelayProperty]
    public string UseScratch { get; set; } = null;

    [UiInt("j", "Number of threads",
           1, 99999, 1,
           helpText: "Number of threads running in parallel on each worker. Threads don't increase " +
                     "the memory usage as much as processes do, but the performance gain is smaller when " +
                     "compared to processes distributed over the same number of CPU cores.")]
    [RelayProperty]
    public int NThreads { get; set; } = 16;

    [UiBool("", "Use GPU",
            helpText: "If set to Yes, the program will use the GPU for calculations. " +
                      "This will speed up the calculations significantly. If set to No, " +
                      "the calculations will be done on the CPU.")]
    [RelayProperty]
    public bool UseGpu { get; set; } = true;

    [UiInt("", "Number of GPUs",
           min: 1,
           max: 99999,
           helpText: "The number of GPUs to use for the job. The GPUs will be distributed " +
                     "automatically between workers and threads.",
           ConditionalOnField = nameof(UseGpu),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int NGpus { get; set; } = 1;

    [UiInt("", "Memory per worker",
           1,
           99999,
           1,
           unit: "GB",
           helpText: "Memory requested per each worker launched in GB.")]
    [RelayProperty]
    public int MemoryPerWorker { get; set; } = 16;

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

    private string ResDataStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{(i * ResultsEveryNIterations):D3}_data.star");
    private string ResOptimizationSetStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{(i * ResultsEveryNIterations):D3}_optimisation_set.star");
    private string ResMapFile(int i, int c) => Path.Combine(DirectoryPath, $"run_it{(i * ResultsEveryNIterations):D3}_class{c:D3}.mrc");
    private string ResModelStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{(i * ResultsEveryNIterations):D3}_model.star");
    private string ResOptimiserStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{(i * ResultsEveryNIterations):D3}_optimiser.star");
    private string ResSamplingStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{(i * ResultsEveryNIterations):D3}_sampling.star");

    #endregion

    #region Visualization paths

    public string VisFilteredSlices(int i, int c) => Path.Combine(RelayResultsDirectoryPath,
                                                                  $"filtered_slices_it{i:D4}_class{c:D3}.png");

    public string VisFsc(int i, int c) => Path.Combine(RelayResultsDirectoryPath,
                                                       $"fsc_it{i:D4}_class{c:D3}.png");

    public string VisAngularDistribution(int i, int c) => Path.Combine(RelayResultsDirectoryPath,
                                                                       $"angular_distribution_it{i:D4}_class{c:D3}.png");

    public string VisFourierSampling(int i, int c) => Path.Combine(RelayResultsDirectoryPath,
                                                                   $"fourier_sampling_it{i:D4}_class{c:D3}.png");

    public string VisClassStats(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                       $"stats_it{i:D4}.json");

    public string VisMap3d(int i, int c) => ResMapFile(i, c);

    #endregion

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInParticles = "Particles";

    public const string PortOutParticles = "Particles";
    public const string PortOutMaps = "Maps";

    public InitialReference()
    {
        var portInParticles = new PortIn(this, typeof(ParticleSet), PortInParticles, "Particles", 1, int.MaxValue);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInParticles.Name] = portInParticles
        });

        var portOutParticles = new PortOut(this, typeof(ParticleSet), PortOutParticles, "Particles", GetParticlesResource);
        var portOutMaps = new PortOut(this, typeof(MapList), PortOutMaps, "Classes", GetClassesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutParticles.Name] = portOutParticles,
            [portOutMaps.Name] = portOutMaps
        });
    }

    private ParticleSet GetParticlesResource(int iter)
    {
        if (iter == -1)
            iter = VisAvailableIteration;

        ParticleSet result = PortsIn[PortInParticles].GetSingleResource<ParticleSet>(iter);

        result.ParticlesSingleStarPath = ResDataStarFile(iter);
        result.HasClasses = true;
        result.HasScale = true;
        result.HasAngles = true;

        if (result.IsTomo)
            result.OptimisationSetStarPath = ResOptimizationSetStarFile(iter);

        return result;
    }

    private MapList GetClassesResource(int iter)
    {
        if (iter == -1)
            iter = VisAvailableIteration;

        List<Map> maps = new();

        for (int c = 1; c <= NClasses; c++)
            maps.Add(new Map(averageVolumePath: ResMapFile(iter, c),
                             visualizationPaths: new()
                             {
                                 { Map.VisTypes.OrthoSlices, VisFilteredSlices(iter, c) },
                                 { Map.VisTypes.Fsc, VisFsc(iter, c) },
                                 { Map.VisTypes.AngularDistribution, VisAngularDistribution(iter, c) },
                                 { Map.VisTypes.FourierSampling, VisFourierSampling(iter, c) },
                                 { Map.VisTypes.Statistics, VisClassStats(iter) }
                             },
                             isAbsoluteScale: true));

        return new MapList(maps, ResModelStarFile(iter));
    }

    public override string CommandName => "relion_refine";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        if (!string.IsNullOrWhiteSpace(AdditionalArguments))
            foreach (var kv in ArgumentStringToDictionary(AdditionalArguments))
                result[kv.Key] = kv.Value;

        if (UseGpu)
            result.TryAdd("gpu", "\"\"");

        result.TryAdd("grad", "");
        result.TryAdd("denovo_3dref", "");
        result.TryAdd("healpix_order", "2");
        result.TryAdd("offset_range", "6");
        result.TryAdd("offset_step", "2");
        result.TryAdd("pool", "30");
        result.TryAdd("pad", "1");
        result.TryAdd("dont_combine_weights_via_disc", "");
        result.TryAdd("flatten_solvent", "");
        result.TryAdd("oversampling", "1");
        result.TryAdd("pipeline_control", DirectoryName);

        var particleSet = PortsIn["Particles"].GetSingleResource<ParticleSet>();

        if (particleSet.IsTomo)
            result.Add("ios", Space.GetRelativePath(particleSet.OptimisationSetStarPath));
        else
            result.Add("i", Space.GetRelativePath(particleSet.ParticlesSingleStarPath));

        result["o"] = Space.GetRelativePath(Path.Combine(DirectoryPath, "run"));

        return result;
    }

    private long LastLogSize = -1;

    public override Action TrackProgressLogs()
    {
        var baseResult = base.TrackProgressLogs();

        Directory.CreateDirectory(RelayResultsDirectoryPath);

        int maxLogsExist = -1;
        bool reportUpdate = false;

        #region Track logs

        if (File.Exists(PathStdOut))
        {
            maxLogsExist = 0;
            long currentSize = new FileInfo(PathStdOut).Length;

            if (currentSize != LastLogSize)
            {
                reportUpdate = true;
                LastLogSize = currentSize;
                Span<string> logLines = File.ReadAllText(PathStdOut).Split('\n');

                // Take care of progress bar mice
                for (int i = 0; i < logLines.Length; i++)
                    if (logLines[i].Contains('\r'))
                        logLines[i] = logLines[i].Substring(logLines[i].LastIndexOf('\r') + 1);

                Dictionary<int, int> iterationLines = new() { { 0, 0 } }; // Iteration 0 is always there and starts at 0

                for (int i = 0; i < logLines.Length; i++)
                {
                    if (logLines[i].StartsWith(" Auto-refine: Estimated"))
                    {
                        //try
                        {
                            iterationLines[iterationLines.Count] = i;
                        }
                        //catch { }

                        while (i < logLines.Length && logLines[i].StartsWith(" Auto-refine:"))
                            i++;

                        i = Math.Min(i, logLines.Length - 1);
                    }
                    else if (logLines[i].StartsWith(" CurrentResolution"))
                    {
                        //try
                        {
                            iterationLines[iterationLines.Count] = i;
                        }
                        //catch { }
                    }
                }

                if (ResultsEveryNIterations > 1)
                {
                    Dictionary<int, int> newIterationLines = new() { { 0, 0 } };

                    foreach (var kvp in iterationLines)
                        if (kvp.Key % ResultsEveryNIterations == 1)
                            newIterationLines[(kvp.Key + ResultsEveryNIterations - 1) / ResultsEveryNIterations] = kvp.Value;

                    iterationLines = newIterationLines;
                }

                if (iterationLines.Count > 0)
                {
                    maxLogsExist = iterationLines.Select(kvp => kvp.Key).Max();

                    foreach (var kvp in iterationLines)
                    {
                        // Skip updating logs for iterations that won't be updated anymore
                        if (kvp.Key < maxLogsExist - 2)
                            continue;

                        int start = kvp.Value;
                        int end = iterationLines.ContainsKey(kvp.Key + 1) ? iterationLines[kvp.Key + 1] : logLines.Length;

                        JobTools.WriteLogFile(string.Join('\n', logLines.Slice(start, end - start).ToArray()),
                                              LogFilePath(kvp.Key));
                    }
                }
            }
            else
            {
                maxLogsExist = Math.Max(maxLogsExist, LogsAvailableIteration);
            }
        }

        #endregion

        reportUpdate |= maxLogsExist > LogsAvailableIteration;

        if (reportUpdate)
            return () =>
            {
                baseResult?.Invoke();
                LogsAvailableIteration = maxLogsExist;
            };
        else
            return baseResult;
    }

    /// <summary>
    /// Tracks and processes job results, generating visualizations for new iterations.
    /// </summary>
    /// <returns>
    /// An action that updates the job's VisAvailableIteration property when new results are found,
    /// or null if no new results are available.
    /// </returns>
    /// <remarks>
    /// This method is a critical component of the job's integration with the QueueRepository's
    /// progress tracking system. It's called periodically by the repository to detect and process
    /// new results from the job:
    /// 
    /// ```csharp
    /// // From QueueRepository:
    /// if (job.TrackProgressResults() is { } updateAction)
    ///     _jobUpdateCallback(job, _ => updateAction());
    /// ```
    /// 
    /// The method performs several important tasks:
    /// 1. Checks for new map files and model files for each iteration
    /// 2. Generates orthoslice visualizations for 3D volumes
    /// 3. Creates angular distribution and Fourier sampling plots
    /// 4. Generates FSC (Fourier Shell Correlation) plots
    /// 5. Extracts and saves class statistics as JSON
    /// 6. Creates a job card visualization showing all classes
    /// 
    /// When new results are found, it returns an action that updates the job's 
    /// VisAvailableIteration property, triggering UI updates through the data binding system.
    /// </remarks>
    public override Action TrackProgressResults()
    {
        var baseResult = base.TrackProgressResults();

        Directory.CreateDirectory(RelayResultsDirectoryPath);

        int maxResultsExist = -1;
        bool hasFinished = File.Exists(PathSuccess);

        for (int ires = 0; ires < LogsAvailableIteration + (hasFinished ? 1 : 0); ires++)
        {
            if (!File.Exists(ResMapFile(ires, 1)))
                break;

            // If HasFinished == true, visualize all remaining results,
            // otherwise only the first iterations that doesn't have vis yet
            if (!File.Exists(VisFilteredSlices(ires, 1)))
            {
                List<Task> visTasks = new();

                // All iterations have the same name convention
                visTasks.Add(Task.Run(() =>
                {
                    for (int c = 1; c <= NClasses; c++)
                        if (File.Exists(ResMapFile(ires, c)))
                            BakeryWrapper.MapOrthosliceAtlas(ResMapFile(ires, c),
                                                             1,
                                                             VisFilteredSlices(ires, c));
                }));

                visTasks.Add(Task.Run(() =>
                {
                    if (File.Exists(ResDataStarFile(ires)))
                        BakeryWrapper.OrientationAndFourierSamplingHexBinClass3D(ResDataStarFile(ires),
                                                                                 NClasses,
                                                                                 3,
                                                                                 Path.Combine(RelayResultsDirectoryPath,
                                                                                              $"angular_distribution_it{ires:D4}.png"),
                                                                                 Path.Combine(RelayResultsDirectoryPath,
                                                                                              $"fourier_sampling_it{ires:D4}.png"),
                                                                                 Symmetry);
                }));

                visTasks.Add(Task.Run(() =>
                {
                    if (File.Exists(ResModelStarFile(ires)))
                        BakeryWrapper.Class3DPerClassFscPlots(ResModelStarFile(ires),
                                                              Path.Combine(RelayResultsDirectoryPath,
                                                                           $"fsc_it{ires:D4}.png"));
                }));

                visTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        if (File.Exists(ResModelStarFile(ires)))
                        {
                            Star tableIn = new(ResModelStarFile(ires), "model_classes");

                            Class3DModel[] models = new Class3DModel[NClasses];

                            for (int c = 0; c < NClasses; c++)
                            {
                                float? distribution = tableIn.HasColumn("rlnClassDistribution") ? tableIn.GetRowValueFloat(c, "rlnClassDistribution") : null;

                                if (distribution.HasValue && !float.IsFinite(distribution.Value))
                                    distribution = 0;

                                float? resolution = tableIn.HasColumn("rlnEstimatedResolution") ? tableIn.GetRowValueFloat(c, "rlnEstimatedResolution") : null;

                                if (resolution.HasValue && !float.IsFinite(resolution.Value))
                                    resolution = 999;

                                float? accuracyRotations = tableIn.HasColumn("rlnAccuracyRotations") ? tableIn.GetRowValueFloat(c, "rlnAccuracyRotations") : null;

                                if (accuracyRotations.HasValue && !float.IsFinite(accuracyRotations.Value))
                                    accuracyRotations = 999;

                                float? accuracyTranslations = tableIn.HasColumn("rlnAccuracyTranslationsAngst") ?
                                                                  tableIn.GetRowValueFloat(c, "rlnAccuracyTranslationsAngst") :
                                                                  null;

                                if (accuracyTranslations.HasValue && !float.IsFinite(accuracyTranslations.Value))
                                    accuracyTranslations = 999;

                                models[c] = new Class3DModel
                                {
                                    Id = c + 1,
                                    Distribution = distribution,
                                    Resolution = resolution,
                                    AccuracyRotations = accuracyRotations,
                                    AccuracyTranslations = accuracyTranslations
                                };
                            }

                            File.WriteAllText(VisClassStats(ires),
                                              JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = true }));
                        }
                    }
                    catch (Exception e)
                    {
                        Log.ForContext<InitialReference>().Error(e, "Error processing class statistics for initial reference iteration {Iteration}", ires);
                    }
                }));

                visTasks.Add(Task.Run(() =>
                                          BakeryWrapper.InitialReference3DJobCard(
                                                                                  volumeFiles: GetClassesResource(ires)
                                                                                               .Maps.Take(20)
                                                                                               .Select(m => m.AverageVolumePath)
                                                                                               .ToArray(),
                                                                                  classNumbers: Enumerable.Range(start: 1, count: NClasses).Take(20).ToArray(),
                                                                                  outputImageFile: VisCard(ires))
                                     ));

                Task.WaitAll(visTasks.ToArray());

                maxResultsExist = ires;

                if (!hasFinished)
                    break;
            }
        }

        bool reportUpdate = maxResultsExist > VisAvailableIteration;

        if (reportUpdate)
            return () =>
            {
                baseResult?.Invoke();
                VisAvailableIteration = maxResultsExist;
            };
        else
            return baseResult;
    }
}