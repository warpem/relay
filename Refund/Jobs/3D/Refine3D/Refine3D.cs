using System.Globalization;
using Refund.Components.TomogramViewer;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Tools;

namespace Refund.Jobs._3D.Refine3D;

[GenerateReadOnly]
/// <summary>
/// Implementation of the 3D refinement job based on RELION's 'refine' command.
/// Performs high-resolution 3D reconstruction using the gold-standard approach
/// to prevent overfitting by refining two half-sets independently.
/// Used extensively throughout the application for obtaining high-resolution
/// structures from particle sets.
/// </summary>
public class Refine3D : RelionJob, IClusterJob
{
    public override string TypeGuid => "2c3b1d9f-e226-48fd-a74a-1b2b319fae52";

    /// <summary>
    /// The job type category identifier used for job creation, cloning and metadata.
    /// Used in the data repository when creating new jobs of this type or cloning
    /// existing jobs.
    /// </summary>
    public override string TypeCategory => "3D.Refine3D";

    /// <summary>
    /// Full display name for the job type. Used in the UI for job creation,
    /// and in property panels to identify the job type to users.
    /// </summary>
    public override string TypeName => "3D refinement";

    /// <summary>
    /// Short display name for the job type. Used for compact displays
    /// such as job cards and menu items.
    /// </summary>
    public override string TypeNameShort => "Refine3D";

    /// <summary>
    /// Description of the job type to provide context to users.
    /// Displayed in job creation dialogs and property panels.
    /// </summary>
    public override string TypeDescription => "High-resolution 3D refinement based on a single reference";

    /// <summary>
    /// Determines which queue type this job should use (GPU or CPU).
    /// Dynamically determined based on the UseGpu parameter to allow
    /// users to choose the appropriate processing resources.
    /// </summary>
    public override JobQueueType QueueType => UseGpu ? JobQueueType.GPU : JobQueueType.CPU;

    /// <summary>
    /// Indicates that this job performs multiple iterations.
    /// Used by the queue system to properly track progress and
    /// by visualization components to show per-iteration results.
    /// </summary>
    public override bool IsIterative => true;

    /// <summary>
    /// The component type to use for expanded view of this job.
    /// References the Refine3DExpandedView component.
    /// </summary>
    public override Type ExpandedViewType => typeof(Refine3DExpandedView);

    /// <summary>
    /// Defines the grid layout for job card contents (2x1).
    /// Used by the card visualization system to layout components.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string[] SupportedModules => base.SupportedModules.Concat(["gpu", "cpu"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(UseGpu ? ["gpu"] : ["cpu"]).ToArray();

    public override int CoreCount => NThreads;

    public override int MemoryGb => (NProcesses - 1) * MemoryPerWorker;

    public override int GpuCount => UseGpu ? NGpus : 0;

    public override int ProcessCount => NProcesses;

    public override bool CanBeFinalized => true;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInParticles = "Particles";
    public const string PortInReference = "Reference";
    public const string PortInMask = "Mask";
    public const string PortOutParticles = "Particles";
    public const string PortOutReference = "Reference";
    public const string PortOutMask = "Mask";

    #region Parameters

    #region Reference

    [UiFieldGroup("Reference", 0)]
    [UiSymmetry("sym", "Symmetry",
                helpText: "The symmetry of the reference map. This is used to speed up the calculations. " +
                          "If you are unsure, use C1.")]
    [RelayProperty]
    public string Symmetry { get; set; } = "C1";

    [UiDecimal("ini_high", "Initial low-pass filter",
               min: 0.0,
               max: 10000.0,
               stepSize: 0.1,
               isAdvanced: true,
               helpText: "It is recommended to strongly low-pass filter your initial reference map. If " +
                         "it has not yet been low-pass filtered, it may be done internally using this " +
                         "option. If set to 0, no low-pass filter will be applied to the initial reference(s).",
               Unit = "Å")]
    [RelayProperty]
    public decimal InitialLowPass { get; set; } = 60m;

    [UiBool("trust_ref_size", "Resize reference if needed",
            isAdvanced: true,
            helpText: "If true, and if the input reference map (and mask) do not have the same pixel size " +
                      "and/or box size, then they will be re-scaled and re-boxed accordingly. If this " +
                      "option is set to false, then the program will die with an error if the reference " +
                      "does not have the correct pixel and/or box size.")]
    [RelayProperty]
    public bool AutoResizeReference { get; set; } = true;

    [UiDecimal("low_resol_join_halves", "Half-map join resolution",
               min: 0.0,
               max: 10000.0,
               stepSize: 0.1,
               isAdvanced: true,
               helpText: "The resolution up to which the two half-maps will be joined between iterations " +
                         "to prevent them from drifting apart.",
               Unit = "Å")]
    [RelayProperty]
    public decimal HalfmapJoinResolution { get; set; } = 40m;

    #endregion

    #region Optimization

    [UiFieldGroup("Optimization", 1)]
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

    [UiBool("blush", "Use Blush for regularization",
            helpText: "If set to Yes, refinement will use a neural network to perform regularisation " +
                      "by denoising at every iteration, instead of the standard smoothness regularisation.")]
    [RelayProperty]
    public bool UseBlush { get; set; } = false;

    [UiDecimalNullable("strict_highres_exp", "Limit alignment resolution",
                       min: 0,
                       max: 10000,
                       stepSize: 0.1,
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
    public decimal? LimitAlignmentResolution { get; set; } = null;

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
                         "have been observed to be useful for 3D classification, values of 1-2 " +
                         "for 2D classification. Too small values yield too-low resolution " +
                         "structures; too high values result in over-estimated resolutions, " +
                         "mostly notable by the apparition of high-frequency noise in the references.")]
    [RelayProperty]
    public decimal TauFudge { get; set; } = 1;

    #endregion

    #region Alignment

    [UiFieldGroup("Alignment", 2)]
    [UiHealpix("healpix_order", "Initial angular sampling",
               helpText: "There are only a few discrete angular samplings possible because we use " +
                         "the HealPix library to generate the sampling of the first two Euler angles " +
                         "on the sphere. The samplings are approximate numbers and vary slightly over " +
                         "the sphere. hp2=15deg, hp3=7.5deg, etc")]
    [RelayProperty]
    public int HealpixOrder { get; set; } = 3;

    [UiDecimal("offset_range", "Initial offset search range",
               min: 0.1,
               max: 1000,
               stepSize: 0.1,
               helpText: "Probabilities will be calculated only for translations in a circle " +
                         "with this radius (in Angstrom). The center of this circle changes at " +
                         "every iteration and is placed at the optimal translation for each " +
                         "image in the previous iteration.",
               Unit = "Å")]
    [RelayProperty]
    public decimal OffsetRange { get; set; } = 5m;

    [UiDecimal("offset_step", "Initial offset search step",
               min: 0.1,
               max: 1000,
               stepSize: 0.1,
               helpText: "Translations will be sampled with this step-size (in Angstrom).",
               Unit = "Å")]
    [RelayProperty]
    public decimal OffsetSampling { get; set; } = 1m;

    [UiHealpix("auto_local_healpix_order", "Local searches from angular sampling",
               helpText: "In the automated procedure to increase the angular samplings, local " +
                         "angular searches of -6/+6 times the sampling rate will be used from this " +
                         "angular sampling rate onwards. For most lower-symmetric particles a value of " +
                         "1.8 degrees will be sufficient. Perhaps icosahedral symmetries may benefit from " +
                         "a smaller value such as 0.9 degrees.")]
    [RelayProperty]
    public int LocalSearchesFrom { get; set; } = 5;

    [UiSymmetry("relax_sym", "Relax symmetry",
                helpText: "With this option, poses related to the standard local angular search range " +
                          "by the given point group will also be explored. For example, if you have a " +
                          "pseudo-symmetric dimer A-A', refinement or classification in C1 with symmetry " +
                          "relaxation by C2 might be able to improve distinction between A and A'. Note " +
                          "that the reference must be more-or-less aligned to the convention of (pseudo-)" +
                          "symmetry operators. For details, see Ilca et al 2019 and Abrishami et al 2020 " +
                          "cited in the About dialog.")]
    [RelayProperty]
    public string RelaxSymmetry { get; set; } = null;

    [UiBool("auto_ignore_angles", "Use finer sampling faster",
            helpText: "If set to Yes, then let auto-refinement proceed faster with finer angular " +
                      "and offset samplings. This option will make the computation faster, but hasn't " +
                      "been tested for many cases for potential loss in reconstruction quality upon convergence.")]
    [RelayProperty]
    public bool UseFinerSamplingFaster { get; set; } = false;

    #endregion

    #region Helical

    [UiFieldGroup("Helical", 3)]
    [UiBool("helix", "Do helical reconstruction",
            helpText: "If set to Yes, the program will perform 3D helical reconstruction. " +
                      "This requires that the particles have been picked as filaments.")]
    [RelayProperty]
    public bool DoHelical { get; set; } = false;

    [UiRange("helical_inner_diameter,helical_outer_diameter", "Tube diameter",
             min: -1,
             max: 10000,
             stepSize: 1,
             helpText: "Inner and outer diameter (in Angstroms) of the reconstructed helix spanning " +
                       "across Z axis. Set the inner diameter to negative value if the helix is not " +
                       "hollow in the center. The outer diameter should be slightly larger than the " +
                       "actual width of helical tubes because it also decides the shape of 2D particle " +
                       "mask for each segment. If the psi priors of the extracted segments are not " +
                       "accurate enough due to high noise level or flexibility of the structure, then " +
                       "set the outer diameter to a large value.",
             Unit = "Å",
             ConditionalOnField = nameof(DoHelical),
             ConditionalOnValue = true)]
    [RelayProperty]
    public float2 HelicalTubeDiameter { get; set; } = new(-1, -1);

    [UiFloat3("sigma_rot,sigma_tilt,sigma_psi", "Angular search range (rot/tilt/psi)",
              min: -1,
              max: 180,
              stepSize: 0.1f,
              helpText: "Local angular searches will be performed within +/- the given amount (in " +
                        "degrees) from the optimal orientation in the previous iteration. A Gaussian " +
                        "prior (also see previous option) will be applied, so that orientations closer " +
                        "to the optimal orientation in the previous iteration will get higher weights " +
                        "than those further away.\n\n" +
                        "These ranges will only be applied to the rot, tilt and psi angles in the first " +
                        "few iterations(global searches for orientations) in 3D helical reconstruction. " +
                        "Values of 9 or 15 degrees are commonly used.Higher values are recommended for " +
                        "more flexible structures and more memory and computation time will be used. A " +
                        "range of 15 degrees means sigma = 5 degrees.\n\n" +
                        "These options will be invalid if you choose to perform local angular searches " +
                        "or not to perform image alignment on 'Sampling' tab.",
              Unit = "°",
              ConditionalOnField = nameof(DoHelical),
              ConditionalOnValue = true)]
    [RelayProperty]
    public float3 HelicalAngleRange { get; set; } = new(-1, 15, 10);

    [UiDecimal("helical_sigma_distance", "Local averaging range factor",
               min: -1,
               max: 5,
               stepSize: 0.1,
               helpText: "Local averaging of orientations and translations will be performed within a " +
                         "range of +/- this value * the box size. Polarities are also set to be the same " +
                         "for segments coming from the same tube during local refinement. Values of ~ 2.0 " +
                         "are recommended for flexible structures such as MAVS-CARD filaments, ParM, " +
                         "MamK, etc. This option might not improve the reconstructions of helices formed " +
                         "from curled 2D lattices (TMV and VipA/VipB). Set to negative to disable this option.",
               ConditionalOnField = nameof(DoHelical),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal HelicalRangeFactor { get; set; } = -1;

    [UiBool("helical_keep_tilt_prior_fixed", "Keep tilt prior fixed",
            isAdvanced: true,
            helpText: "If set to yes, the tilt prior will not change during the optimisation. If set to " +
                      "No, at each iteration the tilt prior will move to the optimal tilt value for that " +
                      "segment from the previous iteration.",
            ConditionalOnField = nameof(DoHelical),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool HelicalKeepTiltPriorFixed { get; set; } = true;

    [UiBool("ignore_helical_symmetry", "Apply helical symmetry",
            helpText: "If set to Yes, helical symmetry will be applied in every iteration. Set to No if " +
                      "you have just started a project, helical symmetry is unknown or not yet estimated.",
            reverse: true,
            ConditionalOnField = nameof(DoHelical),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool HelicalApplySymmetry { get; set; } = true;

    [UiInt("helical_nr_asu", "Unique asymmetrical unit count",
           min: 1,
           max: 10000,
           stepSize: 1,
           helpText: "Number of unique helical asymmetrical units in each segment box. If the inter-box " +
                     "distance (set in segment picking step) is 100 Angstroms and the estimated helical " +
                     "rise is ~20 Angstroms, then set this value to 100 / 20 = 5 (nearest integer). This " +
                     "integer should not be less than 1. The correct value is essential in measuring the " +
                     "signal to noise ratio in helical reconstruction.",
           ConditionalOnField = nameof(HelicalApplySymmetry),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int HelicalNumberUniqueUnits { get; set; } = 1;

    [UiDecimal("helical_twist_initial", "Helical twist",
               min: -180,
               max: 180,
               stepSize: 0.001,
               helpText: "Initial helical symmetry. Set helical twist (in degrees) to positive value if " +
                         "it is a right-handed helix. If local searches of helical symmetry are planned, " +
                         "initial values of helical twist and rise should be within their respective ranges.",
               Unit = "°",
               ConditionalOnField = nameof(HelicalApplySymmetry),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal HelicalTwist { get; set; } = 0m;

    [UiDecimal("helical_rise_initial", "Helical rise",
               min: 0,
               max: 10000,
               stepSize: 0.001,
               helpText: "Initial helical symmetry. Helical rise is a positive value in Angstrom. If local " +
                         "searches of helical symmetry are planned, initial values of helical twist and " +
                         "rise should be within their respective ranges.",
               Unit = "Å",
               ConditionalOnField = nameof(HelicalApplySymmetry),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal HelicalRise { get; set; } = 0m;

    [UiInt("helical_z_percentage", "Central Z length",
           min: 1,
           max: 100,
           stepSize: 1,
           helpText: "Reconstructed helix suffers from inaccuracies of orientation searches. The central " +
                     "part of the box contains more reliable information compared to the top and bottom " +
                     "parts along Z axis, where Fourier artefacts are also present if the number of helical " +
                     "asymmetrical units is larger than 1. Therefore, information from the central part of " +
                     "the box is used for searching and imposing helical symmetry in real space. Set this " +
                     "value (%) to the central part length along Z axis divided by the box size. Values " +
                     "around 30% are commonly used.",
           Unit = "%",
           ConditionalOnField = nameof(HelicalApplySymmetry),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int HelicalCentralZLength { get; set; } = 30;

    [UiBool("helical_symmetry_search", "Do local searches of symmetry",
            helpText: "If set to Yes, then perform local searches of helical twist and rise within given ranges.",
            ConditionalOnField = nameof(DoHelical),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool HelicalDoSymmetrySearch { get; set; } = false;

    [UiFloat3("helical_twist_min,helical_twist_max,helical_twist_inistep", "Twist search range (min/max/step)",
              min: -180,
              max: 180,
              stepSize: 0.001f,
              helpText: "Minimum, maximum and initial step for helical twist search. Set helical twist (in " +
                        "degrees) to positive value if it is a right-handed helix. Generally it is not " +
                        "necessary for the user to provide an initial step (less than 1 degree, 5~1000 " +
                        "samplings as default). But it needs to be set manually if the default value does " +
                        "not guarantee convergence. The program cannot find a reasonable symmetry if the " +
                        "true helical parameters fall out of the given ranges. Note that the final " +
                        "reconstruction can still converge if wrong helical and point group symmetry are provided.",
              Unit = "°",
              ConditionalOnField = nameof(HelicalDoSymmetrySearch),
              ConditionalOnValue = true)]
    [RelayProperty]
    public float3 HelicalTwistRange { get; set; } = new(0);

    [UiFloat3("helical_rise_min,helical_rise_max,helical_rise_inistep", "Rise search range (min/max/step)",
              min: 0,
              max: 10000,
              stepSize: 0.001f,
              helpText: "Minimum, maximum and initial step for helical rise search. Helical rise is a positive " +
                        "value in Angstroms. Generally it is not necessary for the user to provide an initial " +
                        "step (less than 1% the initial helical rise, 5~1000 samplings as default). But it " +
                        "needs to be set manually if the default value does not guarantee convergence. The " +
                        "program cannot find a reasonable symmetry if the true helical parameters fall out " +
                        "of the given ranges. Note that the final reconstruction can still converge if wrong " +
                        "helical and point group symmetry are provided.",
              Unit = "Å",
              ConditionalOnField = nameof(HelicalDoSymmetrySearch),
              ConditionalOnValue = true)]
    [RelayProperty]
    public float3 HelicalRiseRange { get; set; } = new(0);

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

    [UiBool("gpu", "Use GPU",
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

    [UiInt("j", "Number of threads",
           1,
           99999,
           1,
           helpText: "Number of threads running in parallel on each worker. Threads don't increase " +
                     "the memory usage as much as processes do, but the performance gain is smaller when " +
                     "compared to processes distributed over the same number of CPU cores.")]
    [RelayProperty]
    public int NThreads { get; set; } = 1;

    [UiInt("", "Number of workers",
           3,
           99999,
           2,
           helpText: "The number of workers to use for the job. This is the number of MPI processes " +
                     "that will be started. 3D Refinement requires 2n+1 processes, where n>0. The number of workers " +
                     "should not exceed the number of available CPU cores.")]
    [RelayProperty]
    public int NProcesses { get; set; } = 3;

    [UiInt("", "Memory per worker",
           1,
           99999,
           1,
           unit: "GB",
           helpText: "Memory requested per each worker launched in GB.")]
    [RelayProperty]
    public int MemoryPerWorker { get; set; } = 8;

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

    private string ResFinalMapFile => Path.Combine(DirectoryPath, "run_class001.mrc");
    private string ResFinalDataStarFile => Path.Combine(DirectoryPath, "run_data.star");
    private string ResFinalOptimisationSetStarFile => Path.Combine(DirectoryPath, "run_optimisation_set.star");
    private string ResFinalHalf1MapFile => Path.Combine(DirectoryPath, "run_half1_class001_unfil.mrc");
    private string ResFinalHalf2MapFile => Path.Combine(DirectoryPath, "run_half2_class001_unfil.mrc");
    private string ResFinalModelStarFile => Path.Combine(DirectoryPath, "run_model.star");
    private string ResFinalOptimiserStarFile => Path.Combine(DirectoryPath, "run_optimiser.star");
    private string ResFinalSamplingStarFile => Path.Combine(DirectoryPath, "run_sampling.star");

    private string ResDataStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_data.star");
    private string ResOptimisationSetStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_optimisation_set.star");
    private string ResHalf1MapFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_half1_class001.mrc");
    private string ResHalf2MapFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_half2_class001.mrc");
    private string ResHalf1MapUnfilFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_half1_class001_unfil.mrc");
    private string ResHalf2MapUnfilFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_half2_class001_unfil.mrc");
    private string ResHalf1ModelStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_half1_model.star");
    private string ResHalf2ModelStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_half2_model.star");
    private string ResOptimiserStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_optimiser.star");
    private string ResSamplingStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_sampling.star");

    #endregion

    #region Visualization paths

    public bool VisHasUnfiltered => PortsIn["Mask"].Count > 0;

    public string VisFilteredSlices(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                           $"filtered_slices_it{i:D4}.png");

    public string VisUnfilteredSlices(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                             $"unfiltered_slices_it{i:D4}.png");

    public string VisFilteredProjections(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                                $"filtered_projections_it{i:D4}.png");

    public string VisUnfilteredProjections(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                                  $"unfiltered_projections_it{i:D4}.png");

    public string VisFsc(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                $"fsc_it{i:D4}.png");

    public string VisAngularDistribution(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                                $"angular_distribution_it{i:D4}.png");

    public string VisFourierSampling(int i) => Path.Combine(RelayResultsDirectoryPath,
                                                            $"fourier_sampling_it{i:D4}.png");
    
    public string VisResolutionJson => Path.Combine(RelayResultsDirectoryPath, "resolution.json");

    public string VisMap3d(int i)
    {
        bool hasFinished = false;

        if (Status != JobStatus.Finalizing)
            hasFinished = File.Exists(PathSuccess);
        else
            hasFinished = File.Exists(ResFinalMapFile);
        
        bool breakConvention = i == LogsAvailableIteration && !File.Exists(ResFinalMapFile);

        if (!hasFinished || i < LogsAvailableIteration || breakConvention)
            return ResHalf1MapFile(i);
        else
            return ResFinalMapFile;
    }

    #endregion

    public Refine3D()
    {
        var portInParticles = new PortIn(this, typeof(ParticleSet), PortInParticles, "Particles", 1, int.MaxValue);
        var portInMap = new PortIn(this, typeof(MapList), PortInReference, "Reference", 1, 1);
        var portInMask = new PortIn(this, typeof(Mask), PortInMask, "Mask", 0, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInParticles.Name] = portInParticles,
            [portInMap.Name] = portInMap,
            [portInMask.Name] = portInMask
        });

        var portOutParticles = new PortOut(this, typeof(ParticleSet), PortOutParticles, "Particles", GetParticlesResource);
        var portOutMap = new PortOut(this, typeof(MapList), PortOutReference, "Refined map", GetMapsResource);
        var portOutMask = new PortOut(this, typeof(Mask), PortOutMask, "Mask", GetMaskResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutParticles.Name] = portOutParticles,
            [portOutMap.Name] = portOutMap,
            [portOutMask.Name] = portOutMask
        });
    }

    private ParticleSet GetParticlesResource(int iter)
    {
        if (iter < 0)
            iter = VisAvailableIteration;

        bool isFinal = iter == VisAvailableIteration && HasRunToCompletion;

        var result = PortsIn[PortInParticles].GetSingleResource<ParticleSet>();

        result.ParticlesSingleStarPath = isFinal ? ResFinalDataStarFile : ResDataStarFile(iter);
        result.ParticlesMultiStarDirectory = string.Empty;
        result.ToMultiStarPath = null;
        result.HasAngles = true;
        result.HasScale = true;

        if (result.IsTomo)
        {
            result.OptimisationSetStarPath = isFinal ? ResFinalOptimisationSetStarFile : ResOptimisationSetStarFile(iter);
        }

        return result;
    }

    private MapList GetMapsResource(int iter)
    {
        if (iter < 0)
            iter = VisAvailableIteration;

        bool isFinal = iter == VisAvailableIteration && HasRunToCompletion;

        Map map = new(half1VolumePath: isFinal ? ResFinalHalf1MapFile : ResHalf1MapFile(iter),
                      half2VolumePath: isFinal ? ResFinalHalf2MapFile : ResHalf2MapFile(iter),
                      averageVolumePath: isFinal ? ResFinalMapFile : null,
                      isAbsoluteScale: true);

        return new MapList(new List<Map>([map]),
                           isFinal ? ResFinalModelStarFile : ResHalf1ModelStarFile(iter));
    }

    private Mask GetMaskResource(int iter)
    {
        return PortsIn[PortInMask].GetSingleResource<Mask>();
    }

    /// <summary>
    /// The command name to use when running this job on a compute cluster.
    /// Uses MPI to distribute the processing across multiple nodes.
    /// Referenced by ClusterQueue when constructing the command for job submission.
    /// </summary>
    public override string CommandName => $"mpirun -n {NProcesses} relion_refine_mpi";

    /// <summary>
    /// Builds the command line arguments dictionary for the refine_mpi command.
    /// Used by both ClusterQueue and other job implementations that extend this one.
    /// Handles specialized RELION-specific parameter adjustments, like doubling the
    /// offset step size and calculating the appropriate healpix order.
    /// </summary>
    /// <returns>Dictionary mapping parameter names to their string values</returns>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["offset_step"] = (OffsetSampling * 2).ToString(CultureInfo.InvariantCulture);
        result["healpix_order"] = Math.Max(1, HealpixOrder - 1).ToString(CultureInfo.InvariantCulture);

        // add CLI options based on input resources
        ParticleSet particleSet = PortsIn["Particles"].GetSingleResource<ParticleSet>();
        Map reference = PortsIn["Reference"].GetSingleResource<MapList>().Maps.First();

        if (particleSet.IsTomo)
            result.Add("ios", Space.GetRelativePath(particleSet.OptimisationSetStarPath));
        else
            result.Add("i", Space.GetRelativePath(particleSet.ParticlesSingleStarPath));

        result.Add("o", Space.GetRelativePath(Path.Combine(DirectoryPath, "run")));
        result.Add("ref", reference.AverageVolumePath);

        if (PortsIn["Mask"].Edges.Count > 0)
        {
            var mask = PortsIn["Mask"].Edges.First().Source.GetResource() as Mask;
            result["solvent_mask"] = Space.GetRelativePath(mask.MaskVolumePath);
            result.TryAdd("solvent_correct_fsc", "");
        }

        // Add Refine3D setup options
        result.TryAdd("auto_refine", "");
        result.TryAdd("split_random_halves", "");

        // image processing options
        if (!reference.IsAbsoluteScale)
            result.TryAdd("firstiter_cc", "");    // only necessary if map is not on same scale
        result.TryAdd("oversampling", "1");   // adaptive oversampling of angles
        result.TryAdd("flatten_solvent", ""); // apply mask to reference
        result.TryAdd("norm", "");            // apply normalisation error correction, on by default
        result.TryAdd("scale", "");           // apply per-group scale correction
        result.TryAdd("pad", "2");            // oversampling factor for fourier transforms

        // compute options
        result.TryAdd("pool", "10");
        result.TryAdd("dont_combine_weights_via_disc", "");

        result.TryAdd("pipeline_control", DirectoryName);

        if (UseGpu)
            result.TryAdd("gpu", "\"\"");

        // Add additional arguments
        if (!string.IsNullOrWhiteSpace(AdditionalArguments))
            foreach (var kv in ArgumentStringToDictionary(AdditionalArguments))
                result[kv.Key] = kv.Value;

        return result;
    }
    private long LastLogSize = -1;

    /// <summary>
    /// Tracks progress from the job's log files and updates the job status.
    /// Called by the QueueRepository during job execution to monitor logs
    /// and update the UI with progress information.
    /// </summary>
    /// <returns>An action that updates the job's state, or null if no update is needed</returns>
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
                    if (logLines[i].StartsWith(" Auto-refine: Iteration="))
                    {
                        string[] parts = logLines[i].Split('=', StringSplitOptions.TrimEntries);

                        try
                        {
                            iterationLines[int.Parse(parts[1].Trim())] = i;
                        }
                        catch { }
                    }
                }

                if (iterationLines.Count > 0)
                {
                    maxLogsExist = iterationLines.Select(kvp => kvp.Key).Max();

                    foreach (var kvp in iterationLines)
                    {
                        // Skip updating logs for iterations that won't be updated anymore
                        if (kvp.Key < maxLogsExist - 3)
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
    /// Tracks job results and generates visualizations for completed iterations.
    /// Called by the QueueRepository to process output files and generate
    /// visualization images using BakeryWrapper utility.
    /// </summary>
    /// <returns>An action that updates the job's state, or null if no update is needed</returns>
    public override Action TrackProgressResults()
    {
        var baseResult = base.TrackProgressResults();

        Directory.CreateDirectory(RelayResultsDirectoryPath);

        int maxResultsExist = -1;
        bool hasFinished = false;

        if (Status != JobStatus.Finalizing)
            hasFinished = File.Exists(PathSuccess);
        else
            hasFinished = File.Exists(ResFinalMapFile);

        for (int ires = 0; ires < LogsAvailableIteration + (hasFinished ? 1 : 0); ires++)
        {
            // If HasFinished == true, visualize all remaining results,
            // otherwise only the first iterations that doesn't have vis yet
            if (!File.Exists(VisFilteredSlices(ires)))
            {
                List<Task> visTasks = new();

                // This is relevant when we're finalizing a job that didn't run to completion
                bool breakConvention = ires == LogsAvailableIteration && !File.Exists(ResFinalMapFile);

                // Final iteration may have different file name conventions
                if (!hasFinished || ires < LogsAvailableIteration || breakConvention)
                {
                    if (File.Exists(ResHalf1MapFile(ires)))
                        visTasks.Add(Task.Run(() => BakeryWrapper.MapOrthosliceAtlas(ResHalf1MapFile(ires),
                                                                                                   1,
                                                                                                   VisFilteredSlices(ires))));

                    if (File.Exists(ResHalf1MapUnfilFile(ires)))
                        visTasks.Add(Task.Run(() => BakeryWrapper.MapOrthosliceAtlas(ResHalf1MapUnfilFile(ires),
                                                                                                   1,
                                                                                                   VisUnfilteredSlices(ires))));

                    if (File.Exists(ResHalf1ModelStarFile(ires)))
                        visTasks.Add(Task.Run(() => BakeryWrapper.FSCFromModelStar(ResHalf1ModelStarFile(ires),
                                                                                                 VisFsc(ires))));

                    if (File.Exists(ResDataStarFile(ires)))
                        visTasks.Add(Task.Run(() => BakeryWrapper.OrientationAndFourierSamplingHexBin(particlesFile: ResDataStarFile(ires),
                                                                                                                    outputOrientationFile: VisAngularDistribution(ires),
                                                                                                                    outputFourierSamplingFile: VisFourierSampling(ires),
                                                                                                                    symmetry: Symmetry == "C1" ? null : Symmetry)));

                    visTasks.Add(Task.Run(() => BakeryWrapper.Refine3DJobCard(ResHalf1MapFile(ires),
                                                                                            ResHalf1ModelStarFile(ires),
                                                                                            VisCard(ires))));
                }
                else
                {
                    if (File.Exists(ResFinalMapFile))
                        visTasks.Add(Task.Run(() => BakeryWrapper.MapOrthosliceAtlas(ResFinalMapFile,
                                                                                                   1,
                                                                                                   VisFilteredSlices(ires))));

                    if (File.Exists(ResFinalHalf1MapFile))
                        visTasks.Add(Task.Run(() => BakeryWrapper.MapOrthosliceAtlas(ResFinalHalf1MapFile,
                                                                                                   1,
                                                                                                   VisUnfilteredSlices(ires))));

                    if (File.Exists(ResFinalModelStarFile))
                        visTasks.Add(Task.Run(() => BakeryWrapper.FSCFromModelStar(ResFinalModelStarFile,
                                                                                                 VisFsc(ires))));

                    if (File.Exists(ResFinalDataStarFile))
                        visTasks.Add(Task.Run(() => BakeryWrapper.OrientationAndFourierSamplingHexBin(particlesFile: ResFinalDataStarFile,
                                                                                                                    outputOrientationFile: VisAngularDistribution(ires),
                                                                                                                    outputFourierSamplingFile: VisFourierSampling(ires),
                                                                                                                    symmetry: Symmetry == "C1" ? null : Symmetry)));

                    visTasks.Add(Task.Run(() => BakeryWrapper.Refine3DJobCard(ResFinalMapFile,
                                                                                            ResFinalModelStarFile,
                                                                                            VisCard(ires))));
                }

                visTasks.Add(Task.Run(() =>
                {

                }));

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
                HasRunToCompletion = hasFinished;
            };
        else
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