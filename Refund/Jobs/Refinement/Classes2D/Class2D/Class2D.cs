using System.Globalization;
using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Serilog;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Refinement.Classes2D.Class2D;

/// <summary>
/// Implements RELION's 2D classification functionality for unsupervised classification 
/// of particles into 2D classes. This job is used to group similar particle views together
/// and to separate good particles from junk or contamination.
/// </summary>
/// <remarks>
/// 2D classification is typically used early in data processing to:
/// 1. Assess data quality by visualizing class averages
/// 2. Remove bad particles that group into classes showing ice, carbon, or aggregates
/// 3. Obtain initial insights into the structural heterogeneity
/// 4. Improve the signal-to-noise ratio of the particle images by averaging
/// </remarks>
[GenerateReadOnly]
public class Class2D : RelionJob, IClusterJob
{
    public override string TypeGuid => "e903dcc5-cf6f-4ddb-b0c5-1b23292d8d30";

    /// <summary>
    /// The category path for this job type in the job creation menu
    /// </summary>
    public override string TypeCategory => "Refinement.2D classes.Classify 2D";

    /// <summary>
    /// The full descriptive name of this job type
    /// </summary>
    public override string TypeName => "2D classification";

    /// <summary>
    /// The abbreviated name of this job type
    /// </summary>
    public override string TypeNameShort => "Class2D";

    /// <summary>
    /// A brief description of what this job does
    /// </summary>
    public override string TypeDescription => "Unsupervised classification of particles into 2D classes; useful for initial sorting";

    /// <summary>
    /// The queue type requirement for this job, which depends on whether GPU acceleration is used
    /// </summary>
    public override JobQueueType QueueType => UseGpu ? JobQueueType.GPU : JobQueueType.CPU;

    /// <summary>
    /// relion_refine is passed --gpu when UseGpu is set, and it uses a single device;
    /// there is no per-job GPU count to configure. Requests none when running on the CPU.
    /// </summary>
    public override int GpuCount => UseGpu ? 1 : 0;

    /// <summary>
    /// Indicates that this job produces intermediate results through multiple iterations
    /// </summary>
    public override bool IsIterative => true;

    /// <summary>
    /// Defines the aspect ratio of the job card in the workspace view
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    /// <summary>
    /// Specifies the component type to use for the expanded view of this job
    /// </summary>
    public override Type ExpandedViewType => typeof(Class2DExpandedView);

    #region Parameters

    #region Optimization

    [UiFieldGroup("Optimization", 0)]
    [UiInt("K", "Number of classes",
           min: 1,
           max: 10000,
           stepSize: 1,
           helpText: "The number of classes (K) for a multi-reference refinement. These " +
                     "classes will be made in an unsupervised manner from a single reference " +
                     "by division of the data into random subsets during the first iteration.")]
    [RelayProperty]
    public int NClasses { get; set; } = 100;

    [UiDecimal("tau2_fudge", "Regularization parameter T",
               min: 0.1,
               max: 1000,
               stepSize: 0.1,
               isAdvanced: true,
               helpText: "Bayes law strictly determines the relative weight between the " +
                         "contribution of the experimental data and the prior. However, in " +
                         "practice one may need to adjust this weight to put slightly more " +
                         "weight on the experimental data to allow optimal results. Values " +
                         "greater than 1 for this regularisation parameter (T in the JMB2011 " +
                         "paper) put more weight on the experimental data. Values around 2-4 " +
                         "have been observed to be useful for 3D refinements, values of 1-2 " +
                         "for 2D refinements. Too small values yield too-low resolution " +
                         "structures; too high values result in over-estimated resolutions, " +
                         "mostly notable by the apparition of high-frequency noise in the references.")]
    [RelayProperty]
    public decimal TauFudge { get; set; } = 2;

    [UiEnum("", "Algorithm",
            enumType: typeof(Class2DAlgorithm),
            helpText: "Algorithm to be used for classification.\n" +
                      "VDAM (variable metric gradient descent with adaptive moments): This algorithm " +
                      "was introduced with relion-4.0. It is significantly faster than EM on large data sets.\n" +
                      "EM: (expectation-maximization): This was the default option in releases prior to " +
                      "4.0-beta.")]
    [RelayProperty]
    public Class2DAlgorithm Algorithm { get; set; } = Class2DAlgorithm.VDAM;

    [UiInt("iter", "Number of iterations",
           min: 1,
           max: 10000,
           stepSize: 1,
           helpText: "Number of mini-batches to be processed using the VDAM algorithm. " +
                     "Using 200 has given good results for many data sets. Using 100 will " +
                     "run faster, at the expense of some quality in the results.",
           ConditionalOnField = nameof(Algorithm),
           ConditionalOnValue = Class2DAlgorithm.VDAM)]
    [RelayProperty]
    public int NIterationsVDAM { get; set; } = 200;

    [UiInt("iter", "Number of iterations",
           min: 1,
           max: 10000,
           stepSize: 1,
           helpText: "Number of EM iterations to be performed. Note that the current " +
                     "implementation of 2D classification does NOT comprise a convergence " +
                     "criterium. Therefore, the calculations will need to be stopped by the " +
                     "user if further iterations do not yield improvements in resolution or classes.",
           ConditionalOnField = nameof(Algorithm),
           ConditionalOnValue = Class2DAlgorithm.EM)]
    [RelayProperty]
    public int NIterationsEM { get; set; } = 25;

    [UiInt("particle_diameter", "Mask diameter",
           min: 1,
           max: 10000,
           stepSize: 1,
           helpText: "The experimental images will be masked with a soft circular mask with " +
                     "this diameter. Make sure this radius is not set too small because that " +
                     "may mask away part of the signal! If set to a value larger than the image " +
                     "size no masking will be performed. The same diameter will also be used " +
                     "for a spherical mask of the reference structures if no user-provided " +
                     "mask is specified.",
           Unit = "Å")]
    [RelayProperty]
    public int MaskDiameter { get; set; } = 200;

    [UiBool("zero_mask", "Mask particles with zeros",
            isAdvanced: true,
            helpText: "If set to Yes, then in the individual particles, the area outside a " +
                      "circle with the radius of the particle will be set to zeros prior to " +
                      "taking the Fourier transform. This will remove noise and therefore " +
                      "increase sensitivity in the alignment and classification. However, it " +
                      "will also introduce correlations between the Fourier components that " +
                      "are not modelled. When set to No, then the solvent area is filled with " +
                      "random noise, which prevents introducing correlations. High-resolution " +
                      "refinements (e.g. ribosomes or other large complexes in 3D auto-refine) " +
                      "tend to work better when filling the solvent area with random noise " +
                      "(i.e. setting this option to No), refinements of smaller complexes and " +
                      "most classifications go better when using zeros (i.e. setting this option " +
                      "to Yes).")]
    [RelayProperty]
    public bool MaskWithZeros { get; set; } = true;

    [UiInt("strict_highres_exp", "Limit alignment resolution",
           min: 0,
           max: 10000,
           stepSize: 1,
           isAdvanced: true,
           helpText: "If set to a positive number, then the expectation step (i.e. the alignment) " +
                     "will be done only including the Fourier components up to this resolution " +
                     "(in Angstroms). This is useful to prevent overfitting, as the classification " +
                     "runs in RELION are not to be guaranteed to be 100% overfitting-free (unlike " +
                     "the 3D auto-refine with its gold-standard FSC). In particular for very " +
                     "difficult data sets, e.g. of very small or featureless particles, this " +
                     "has been shown to give much better class averages. In such cases, values " +
                     "in the range of 7-12 Angstroms have proven useful.",
           Unit = "Å")]
    [RelayProperty]
    public int LimitAlignmentResolution { get; set; } = 0;

    [UiBool("center_classes", "Center averages",
            isAdvanced: true,
            helpText: "If set to Yes, every iteration the class average images will be centered " +
                      "on their center-of-mass. This will only work for positive signals, so the " +
                      "particles should be white.")]
    [RelayProperty]
    public bool CenterAverages { get; set; } = true;

    #endregion

    #region Alignment

    [UiFieldGroup("Alignment", 1)]
    [UiBool("skip_align", "Perform alignment",
            helpText: "If set to No, then rather than performing both alignment and classification, " +
                      "only classification will be performed. This allows the use of very focused " +
                      "masks. This requires that the particles already have optimal orientations " +
                      "associated with them.",
            reverse: true)]
    [RelayProperty]
    public bool DoAlignment { get; set; } = true;

    [UiDecimal("psi_step", "Angular sampling",
               min: 0.1,
               max: 180,
               stepSize: 0.1,
               helpText: "The sampling rate for the in-plane rotation angle (psi) in degrees. " +
                         "Using fine values will slow down the program. Recommended value for " +
                         "most 2D refinements: 6 degrees.",
               Unit = "°",
               ConditionalOnField = nameof(DoAlignment),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal AngularSampling { get; set; } = 6m;

    [UiDecimal("offset_range", "Offset search range",
               min: 0.1,
               max: 1000,
               stepSize: 0.1,
               helpText: "Probabilities will be calculated only for translations in a circle " +
                         "with this radius (in Angstrom). The center of this circle changes at " +
                         "every iteration and is placed at the optimal translation for each " +
                         "image in the previous iteration.",
               Unit = "Å",
               ConditionalOnField = nameof(DoAlignment),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal OffsetRange { get; set; } = 5m;

    [UiDecimal("offset_step", "Offset search step",
               min: 0.1,
               max: 1000,
               stepSize: 0.1,
               helpText: "Translations will be sampled with this step-size (in Angstrom).",
               Unit = "Å",
               ConditionalOnField = nameof(DoAlignment),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal OffsetSampling { get; set; } = 1m;

    [UiBool("allow_coarser_sampling", "Allow coarser sampling",
            isAdvanced: true,
            helpText: "If set to Yes, the program will use coarser angular and translational " +
                      "sampling if the estimated accuracy of the assignments is still low in " +
                      "the earlier iterations. This may speed up the calculations.",
            ConditionalOnField = nameof(DoAlignment),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool AllowCoarserSampling { get; set; } = false;

    #endregion

    #region Helical

    [UiFieldGroup("Helical", 2)]
    [UiBool("helix", "Classify helical segments",
            helpText: "Set to Yes if you want to classify 2D helical segments. Note that the " +
                      "helical segments should come with priors for psi angles")]
    [RelayProperty]
    public bool DoHelical { get; set; } = false;

    [UiInt("helical_outer_diameter", "Tube diameter",
           min: 1,
           max: 1000,
           stepSize: 1,
           helpText: "Outer diameter (in Angstrom) of helical tubes. This value should be " +
                     "slightly larger than the actual width of the tubes. You may want to " +
                     "copy the value from previous particle extraction job. If negative " +
                     "value is provided, this option is disabled and ordinary circular " +
                     "masks will be applied. Sometimes '--dont_check_norm' option is useful " +
                     "to prevent errors in normalisation of helical segments.",
           Unit = "Å",
           ConditionalOnField = nameof(DoHelical),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int TubeDiameter { get; set; } = 200;

    [UiBool("bimodal_psi", "Do bimodal angular searches",
            isAdvanced: true,
            helpText: "Do bimodal search for psi angles? Set to Yes if you want to classify " +
                      "2D helical segments with priors of psi angles. The priors should be " +
                      "bimodal due to unknown polarities of the segments. Set to No if the " +
                      "3D helix looks the same when rotated upside down. If it is set to No, " +
                      "ordinary angular searches will be performed.",
            ConditionalOnField = nameof(DoHelical),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool DoBimodalSearches { get; set; } = true;

    [UiDecimal("sigma_psi", "Angular search range",
               min: 0.1,
               max: 180,
               stepSize: 0.1,
               helpText: "Local angular searches will be performed within +/- the given amount " +
                         "(in degrees) from the psi priors estimated through helical segment " +
                         "picking. A range of 15 degrees is the same as sigma = 5 degrees. " +
                         "Note that the ranges of angular searches should be much larger than " +
                         "the sampling.",
               Unit = "°",
               ConditionalOnField = nameof(DoHelical),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal HelicalAngleRange { get; set; } = 6m;

    [UiBool("helical_restrict_offset_to_rise", "Restrict helical offsets to rise",
            isAdvanced: true,
            helpText: "Set to Yes if you want to restrict the translational offsets along the " +
                      "helices to the rise of the helix given below. Set to No to allow free " +
                      "(conventional) translational offsets.",
            ConditionalOnField = nameof(DoHelical),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool RestrictHelicalOffsetsToRise { get; set; } = true;

    [UiDecimal("helical_rise", "Helical rise",
               min: 0.001,
               max: 10000,
               stepSize: 0.001,
               helpText: "The helical rise (in Angstroms). Translational offsets along " +
                       "the helical axis will be limited from -rise/2 to +rise/2, " +
                       "with a flat prior.",
               Unit = "Å",
               ConditionalOnField = nameof(DoHelical),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal HelicalRise { get; set; } = 4.75m;

    #endregion

    #region CTF

    [UiFieldGroup("CTF", 3)]
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

    [UiFieldGroup("Compute", 4)]
    [UiBool("scratch_dir", "Use scratch directory",
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
    public bool UseScratch { get; set; } = false;

    [UiBool("", "Use GPU",
            helpText: "If set to Yes, the program will use the GPU for calculations. " +
                       "This will speed up the calculations significantly. If set to No, " +
                       "the calculations will be done on the CPU.")]
    [RelayProperty]
    public bool UseGpu { get; set; } = true;

    [UiString("gpu", "Which GPUs to use",
              isAdvanced: true,
              helpText: "This argument is not necessary. If left empty, the job itself will try to " +
                        "allocate available GPU resources. You can override the default allocation by " +
                        "providing a list of which GPUs (0,1,2,3, etc) to use. MPI-processes are " +
                        "separated by ':', threads by ','. For example: '0,0:1,1:0,0:1,1'",
              ConditionalOnField = nameof(UseGpu),
              ConditionalOnValue = true)]
    [RelayProperty]
    public string GpuIds { get; set; } = "";
    
    [UiDecimal("j", "Number of threads",
               1,
               99999,
               1,
               helpText: "Number of threads running in parallel on each worker. Threads don't increase " +
                         "the memory usage as much as processes do, but the performance gain is smaller when " +
                         "compared to processes distributed over the same number of CPU cores.")]
    [RelayProperty]
    public decimal NThreads { get; set; } = 1;
    
    [UiDecimal("", "Number of workers",
               1,
               99999,
               1,
               helpText: "The number of workers to use for the job. This is the number of MPI processes " +
                         "that will be started. When >1, 1 process is reserved for the work manager. The number of workers " +
                         "should not exceed the number of available CPU cores.",
               ConditionalOnField = nameof(Algorithm),
               ConditionalOnValue = Class2DAlgorithm.EM)]
    [RelayProperty]
    public decimal NProcesses { get; set; } = 1;

    [UiString("", "Additional arguments",
              isAdvanced: true,
              helpText: "In this box command-line arguments may be provided that are not generated " +
                        "by the GUI. This may be useful for testing developmental options and/or " +
                        "expert use of the program. Specify as --option1 value1 --option2 value2")]
    [RelayProperty]
    public string AdditionalArguments { get; set; } = "";

    #endregion

    #endregion

    /// <summary>
    /// Determines how often results are saved based on the algorithm type.
    /// EM algorithm saves results every iteration, while VDAM saves every 10 iterations.
    /// </summary>
    public int ResultsEveryNIterations => Algorithm == Class2DAlgorithm.EM ? 1 : 10;
    
    #region Results paths
    
    /// <summary>
    /// Path to the note text file containing user comments about the job
    /// </summary>
    private string ResNoteTxtFile => Path.Combine(DirectoryPath, "note.txt");
    
    /// <summary>
    /// Path to the standard output log file produced by RELION
    /// </summary>
    private string ResRunOutFile => Path.Combine(DirectoryPath, "run.out");
    
    /// <summary>
    /// Path to the standard error log file produced by RELION
    /// </summary>
    private string ResRunErrFile => Path.Combine(DirectoryPath, "run.err");
    
    /// <summary>
    /// Path to the file indicating successful job completion
    /// </summary>
    private string ResJobExitSuccessFile => Path.Combine(DirectoryPath, "SUCCESS");

    /// <summary>
    /// Returns the path to the particle data STAR file for a specific iteration
    /// </summary>
    /// <param name="i">The iteration number</param>
    /// <returns>Path to the particle data STAR file</returns>
    private string ResDataStarFile(int i) => Path.Combine(DirectoryPath, 
                                                          $"run_it{(i * ResultsEveryNIterations):D3}_data.star");
    
    /// <summary>
    /// Returns the path to the class average images MRC file for a specific iteration
    /// </summary>
    /// <param name="i">The iteration number</param>
    /// <returns>Path to the class averages MRC file</returns>
    private string ResClassesFile(int i) => Path.Combine(DirectoryPath, 
                                                         $"run_it{(i * ResultsEveryNIterations):D3}_classes.mrcs");
    
    /// <summary>
    /// Returns the path to the model parameters STAR file for a specific iteration
    /// </summary>
    /// <param name="i">The iteration number</param>
    /// <returns>Path to the model STAR file</returns>
    private string ResModelStarFile(int i) => Path.Combine(DirectoryPath, 
                                                           $"run_it{(i * ResultsEveryNIterations):D3}_model.star");
    
    /// <summary>
    /// Returns the path to the optimizer parameters STAR file for a specific iteration
    /// </summary>
    /// <param name="i">The iteration number</param>
    /// <returns>Path to the optimizer STAR file</returns>
    private string ResOptimiserStarFile(int i) => Path.Combine(DirectoryPath, 
                                                               $"run_it{(i * ResultsEveryNIterations):D3}_optimiser.star");
    
    #endregion
    
    #region Visualization paths
    
    /// <summary>
    /// Returns the path to the visualization image atlas of class averages for a specific iteration
    /// </summary>
    /// <param name="i">The iteration number</param>
    /// <returns>Path to the class atlas image file</returns>
    public string VisClassAtlas(int i) => Path.Combine(RelayResultsDirectoryPath, 
                                                       $"classes_it{i:D4}.png");
    
    /// <summary>
    /// Returns the path to the class statistics JSON file for a specific iteration
    /// </summary>
    /// <param name="i">The iteration number</param>
    /// <returns>Path to the class statistics JSON file</returns>
    public string VisClassStats(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                       $"stats_it{i:D4}.json");
    
    #endregion

    /// <summary>
    /// Initializes a new instance of the Class2D job
    /// </summary>
    /// <remarks>
    /// Creates input ports for particles and templates, and output ports for
    /// classified particles and 2D class averages.
    /// </remarks>
    public Class2D()
    {
        var portInParticles = new PortIn(this, typeof(ParticleSet), "Particles", "Particles", 1, int.MaxValue);
        var portInTemplates = new PortIn(this, typeof(TemplateSet), "Templates", "Templates", 0, int.MaxValue);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInParticles.Name] = portInParticles,
            [portInTemplates.Name] = portInTemplates
        });

        var portOutParticles = new PortOut(this, typeof(ParticleSet), "Particles", "Particles", GetParticlesResource);
        var portOutTemplates = new PortOut(this, typeof(TemplateSet), "Templates", "2D Classes", GetTemplatesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutParticles.Name] = portOutParticles,
            [portOutTemplates.Name] = portOutTemplates
        });
    }

    /// <summary>
    /// Creates and returns a ParticleSet resource for the output port
    /// </summary>
    /// <param name="iter">The iteration number for which to retrieve results, or -1 for latest available</param>
    /// <returns>A ParticleSet resource containing the classified particles</returns>
    /// <remarks>
    /// The returned particle set includes class assignments, which can be used for
    /// further filtering and analysis. This allows subsequent jobs to select specific classes.
    /// </remarks>
    private Resource GetParticlesResource(int iter)
    {
        if (iter < 0)
            iter = VisAvailableIteration;
        
        ParticleSet result = PortsIn["Particles"].Edges.First().Source.GetResource() as ParticleSet;

        result.ParticlesSingleStarPath = ResDataStarFile(iter);
        result.HasClasses = true;
        result.HasScale = true;

        return result;
    }

    /// <summary>
    /// Creates and returns a TemplateSet resource for the output port
    /// </summary>
    /// <param name="iter">The iteration number for which to retrieve results (not used - always returns latest)</param>
    /// <returns>A TemplateSet resource containing the 2D class averages</returns>
    /// <remarks>
    /// The returned template set includes the class averages and their associated metadata,
    /// which can be used for visualization and further processing.
    /// </remarks>
    private Resource GetTemplatesResource(int iter) => new TemplateSet(ResModelStarFile(VisAvailableIteration),
                                                                       ResClassesFile(VisAvailableIteration),
                                                                       VisClassStats(VisAvailableIteration));

    /// <summary>
    /// Gets the command name to run RELION for 2D classification
    /// </summary>
    /// <remarks>
    /// The command differs based on the selected algorithm and number of processes:
    /// - For single-process jobs or EM algorithm: uses standard relion_refine
    /// - For multi-process VDAM jobs: uses MPI-enabled relion_refine_mpi
    /// </remarks>
    public override string CommandName => Algorithm == Class2DAlgorithm.EM || NProcesses == 1 ? 
                                              "relion_refine" : 
                                              $"mpirun -n {NProcesses} relion_refine_mpi";

    /// <summary>
    /// Builds the command-line arguments for the RELION 2D classification job
    /// </summary>
    /// <returns>A dictionary of command arguments and their values</returns>
    /// <remarks>
    /// This method constructs the command-line arguments that control the behavior of RELION's
    /// 2D classification. It applies different parameters based on the selected algorithm (EM or VDAM)
    /// and sets up input/output paths. The arguments include angular and offset sampling, optimization
    /// parameters, and various flags to control RELION's behavior.
    /// </remarks>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        // Double the sampling steps for RELION's command line
        result["offset_step"] = (OffsetSampling * 2).ToString(CultureInfo.InvariantCulture);
        result["psi_step"] = (AngularSampling * 2).ToString(CultureInfo.InvariantCulture);

        // Add any user-specified additional arguments
        if (!string.IsNullOrWhiteSpace(AdditionalArguments))
            foreach (var kv in ArgumentStringToDictionary(AdditionalArguments))
                result[kv.Key] = kv.Value;

        // Apply VDAM-specific parameters if using that algorithm
        if (Algorithm == Class2DAlgorithm.VDAM)
        {
            result["grad"] = ""; // Enable gradient-based optimization
            result["grad_write_iter"] = "10"; // Write results every 10 iterations
            result.TryAdd("class_inactivity_threshold", "0.1"); // Classes below this occupancy are considered inactive
        }

        // Set GPU flag if enabled
        if (UseGpu)
            result.TryAdd("gpu", "\"\"");
            
        // Add standard parameters that improve performance and results
        result.TryAdd("pool", "30"); // Number of pooled particles for faster I/O
        result.TryAdd("pad", "2"); // Padding factor for Fourier transforms
        result.TryAdd("dont_combine_weights_via_disc", ""); // Keep weight files in memory
        result.TryAdd("flatten_solvent", ""); // Flatten solvent regions
        result.TryAdd("oversampling", "1"); // Oversampling factor
        result.TryAdd("norm", ""); // Normalize particles
        result.TryAdd("scale", ""); // Scale particles
        result.TryAdd("pipeline_control", DirectoryName); // Enable pipeline control
        
        // Set input particles file path
        var particles = PortsIn["Particles"].Edges.First().Source.GetResource() as ParticleSet;
        result["i"] = Space.GetRelativePath(particles.ParticlesSingleStarPath);
        
        // Set output prefix path
        result["o"] = Space.GetRelativePath(Path.Combine(DirectoryPath, "run"));

        return result;
    }

    private long LastLogSize = -1;


    /// <summary>
    /// Tracks the progress of the job by parsing the log files produced by RELION
    /// </summary>
    /// <returns>An action to update the job's iteration state, or null if no update is needed</returns>
    /// <remarks>
    /// This method reads the RELION output log file and extracts information about the current
    /// job progress. It identifies iteration boundaries in the log and creates per-iteration
    /// log files for easier tracking and display. The method is called periodically to 
    /// update the job's state while it is running.
    /// </remarks>
    public override Action TrackProgressLogs()
    {
        var baseResult = base.TrackProgressLogs();

        Directory.CreateDirectory(RelayResultsDirectoryPath);

        int MaxLogsExist = -1;
        bool logFileChanged = false;

        #region Track logs

        if (File.Exists(ResRunOutFile))
        {
            MaxLogsExist = 0;
            long CurrentSize = new FileInfo(ResRunOutFile).Length;

            if (CurrentSize != LastLogSize)
            {
                logFileChanged = true;
                LastLogSize = CurrentSize;

                Span<string> LogLines = File.ReadAllText(ResRunOutFile).Split('\n');

                // Clean up carriage returns from progress bars in log
                for (int i = 0; i < LogLines.Length; i++)
                    if (LogLines[i].Contains('\r'))
                        LogLines[i] = LogLines[i].Substring(LogLines[i].LastIndexOf('\r') + 1);

                // Map iterations to their starting line in the log file
                Dictionary<int, int> IterationLines = new() { { 0, 0 } };   // Iteration 0 is always there and starts at 0

                // Parse log to find iteration boundaries
                for (int i = 0; i < LogLines.Length; i++)
                {
                    if (LogLines[i].StartsWith(" Auto-refine: Estimated"))
                    {
                        try
                        {
                            IterationLines[IterationLines.Count] = i;
                        }
                        catch { }

                        i++;
                    }
                    else if (LogLines[i].StartsWith(" CurrentResolution"))
                    {
                        try
                        {
                            IterationLines[IterationLines.Count] = i;
                        }
                        catch { }
                    }
                }

                // Adjust iteration numbers if using VDAM which only saves every N iterations
                if (ResultsEveryNIterations > 1)
                {
                    Dictionary<int, int> NewIterationLines = new() { { 0, 0 } };

                    foreach (var kvp in IterationLines)
                        if (kvp.Key % ResultsEveryNIterations == 1)
                            NewIterationLines[(kvp.Key + ResultsEveryNIterations - 1) / ResultsEveryNIterations] = kvp.Value;

                    IterationLines = NewIterationLines;
                }

                // Process each iteration's log section
                if (IterationLines.Count > 0)
                {
                    MaxLogsExist = IterationLines.Select(kvp => kvp.Key).Max();

                    foreach (var kvp in IterationLines)
                    {
                        // Skip updating logs for iterations that won't be updated anymore
                        if (kvp.Key < MaxLogsExist - 2)
                            continue;

                        int Start = kvp.Value;
                        int End = IterationLines.ContainsKey(kvp.Key + 1) ? IterationLines[kvp.Key + 1] : LogLines.Length;

                        // Write this iteration's log section to a separate file
                        JobTools.WriteLogFile(string.Join('\n', LogLines.Slice(Start, End - Start).ToArray()),
                                                    LogFilePath(kvp.Key));
                    }
                }
            }
            else
            {
                MaxLogsExist = Math.Max(MaxLogsExist, LogsAvailableIteration);
            }
        }

        #endregion

        bool ReportUpdate = logFileChanged || MaxLogsExist > LogsAvailableIteration;

        if (ReportUpdate)
            return () =>
            {
                baseResult?.Invoke();
                LogsAvailableIteration = MaxLogsExist;
            };
        else
            return baseResult;
    }

    /// <summary>
    /// Tracks and processes the results produced by RELION for visualization
    /// </summary>
    /// <returns>An action to update the job's visualization state, or null if no update is needed</returns>
    /// <remarks>
    /// This method scans for output files from each completed iteration and generates visualizations
    /// including class average atlases and statistics. It processes the results in parallel
    /// to speed up visualization generation, particularly for large datasets with many classes.
    /// </remarks>
    public override Action TrackProgressResults()
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        int MaxResultsExist = -1;
        bool HasFinished = File.Exists(ResJobExitSuccessFile);

        // Loop through iterations we know exist from log tracking
        for (int ires = 0; ires < LogsAvailableIteration + (HasFinished ? 1 : 0); ires++)
        {
            // Stop if we can't find result files for this iteration
            if (!File.Exists(ResClassesFile(ires)))
                break;
            
            // Generate visualizations for iterations that don't have them yet
            if (!File.Exists(VisClassAtlas(ires)))
            {
                List<Task> VisTasks = new();

                // Task 1: Generate class average atlas visualization
                VisTasks.Add(Task.Run(() =>
                {
                    if (File.Exists(ResClassesFile(ires)))
                        BakeryWrapper.Class2DImageAtlas(ResClassesFile(ires),
                                                        VisClassAtlas(ires));
                }));

                // Task 2: Extract and process class statistics from model star file
                VisTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        Log.ForContext<Class2D>().Information("Processing class statistics file {ClassStatsFile}", VisClassStats(ires));
                        if (File.Exists(ResModelStarFile(ires)))
                        {
                            Star TableIn = new(ResModelStarFile(ires), "model_classes");

                            Class2DModel[] Models = new Class2DModel[NClasses];
                            for (int c = 0; c < NClasses; c++)
                            {
                                // Extract class distribution (percentage of particles)
                                float? Distribution = TableIn.HasColumn("rlnClassDistribution")
                                                      ? TableIn.GetRowValueFloat(c, "rlnClassDistribution")
                                                      : null;
                                if (Distribution.HasValue && !float.IsFinite(Distribution.Value))
                                    Distribution = 0;
                                
                                // Extract estimated resolution of class average
                                float? Resolution = TableIn.HasColumn("rlnEstimatedResolution")
                                                    ? TableIn.GetRowValueFloat(c, "rlnEstimatedResolution")
                                                    : null;
                                if (Resolution.HasValue && !float.IsFinite(Resolution.Value))
                                    Resolution = 999;
                                
                                // Extract angular accuracy for rotations
                                float? AccuracyRotations = TableIn.HasColumn("rlnAccuracyRotations")
                                                          ? TableIn.GetRowValueFloat(c, "rlnAccuracyRotations")
                                                          : null;
                                if (AccuracyRotations.HasValue && !float.IsFinite(AccuracyRotations.Value))
                                    AccuracyRotations = 999;
                                
                                // Extract translational accuracy
                                float? AccuracyTranslations = TableIn.HasColumn("rlnAccuracyTranslationsAngst")
                                                             ? TableIn.GetRowValueFloat(c, "rlnAccuracyTranslationsAngst")
                                                             : null;
                                if (AccuracyTranslations.HasValue && !float.IsFinite(AccuracyTranslations.Value))
                                    AccuracyTranslations = 999;

                                // Create model object for this class
                                Models[c] = new Class2DModel
                                {
                                    Id = c + 1,
                                    Distribution = Distribution,
                                    Resolution = Resolution,
                                    AccuracyRotations = AccuracyRotations,
                                    AccuracyTranslations = AccuracyTranslations
                                };
                            }

                            // Save class statistics as JSON for UI visualization
                            File.WriteAllText(VisClassStats(ires),
                                              JsonSerializer.Serialize(Models, new JsonSerializerOptions { WriteIndented = true }));
                        }
                        Log.ForContext<Class2D>().Information("Completed processing class statistics file {ClassStatsFile}", VisClassStats(ires));
                    }
                    catch (Exception e)
                    {
                        Log.ForContext<Class2D>().Error(e, "Error processing class statistics for iteration {Iteration}", ires);
                    }
                }));

                // Task 3: Generate job card visualization showing class averages
                VisTasks.Add(Task.Run(() => BakeryWrapper.Class2DJobCard(
                    classImagesMrcsFile: ResClassesFile(ires),
                    imageIndices: Enumerable.Range(0, NClasses).ToArray(),
                    imageLabels: Enumerable.Range(1, NClasses).Select(i => i.ToString()).ToArray(),
                    outputImageFile: VisCard(ires)))
                );

                // Wait for all visualization tasks to complete
                Task.WaitAll(VisTasks.ToArray());

                MaxResultsExist = ires;
                break;
            }
        }

        bool ReportUpdate = MaxResultsExist > VisAvailableIteration;

        // Return an action to update the job state if we have new visualizations
        if (ReportUpdate)
            return () =>
            {
                VisAvailableIteration = MaxResultsExist;
            };
        else
            return null;
    }
}

/// <summary>
/// Defines the available algorithms for 2D classification
/// </summary>
public enum Class2DAlgorithm
{
    /// <summary>
    /// Variable metric gradient descent with adaptive moments - Default in RELION 4.0+.
    /// Significantly faster than EM on large datasets with comparable results.
    /// </summary>
    VDAM = 0,
    
    /// <summary>
    /// Expectation-Maximization - Traditional algorithm used in earlier RELION versions.
    /// Can be more stable for difficult datasets but is computationally more expensive.
    /// </summary>
    EM = 1
}

/// <summary>
/// Represents statistics and metadata for a single 2D class
/// </summary>
/// <remarks>
/// This structure stores the key metrics for each 2D class, including particle distribution
/// and alignment accuracy. These metrics help users evaluate the quality of each class
/// and decide which classes to select for further processing.
/// </remarks>
public struct Class2DModel
{
    /// <summary>
    /// The class index (1-based)
    /// </summary>
    public int Id { get; set; }
    
    /// <summary>
    /// The fraction of particles assigned to this class (0.0-1.0)
    /// </summary>
    public float? Distribution { get; set; } = null;
    
    /// <summary>
    /// The estimated resolution of this class average in Angstroms
    /// </summary>
    public float? Resolution { get; set; } = null;
    
    /// <summary>
    /// The angular accuracy of in-plane rotation alignment in degrees
    /// </summary>
    public float? AccuracyRotations { get; set; } = null;
    
    /// <summary>
    /// The translational accuracy of alignment in Angstroms
    /// </summary>
    public float? AccuracyTranslations { get; set; } = null;

    /// <summary>
    /// Creates a new, uninitialized Class2DModel instance
    /// </summary>
    public Class2DModel() { }
}