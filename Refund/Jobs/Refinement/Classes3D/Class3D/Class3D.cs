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

namespace Refund.Jobs.Refinement.Classes3D.Class3D;

/// <summary>
/// Represents a 3D classification job that classifies particle images into multiple 3D classes.
/// This job uses the RELION engine to perform maximum likelihood-based 3D classification of
/// particle images. It can perform classification with or without alignment, and supports
/// various refinement parameters to optimize the classification process.
/// </summary>
/// <remarks>
/// The Class3D job is a core component of the heterogeneity analysis workflow in cryo-EM.
/// It takes a set of particle images and sorts them into distinct 3D structural classes,
/// revealing structural variability in the sample. This job type is used extensively in
/// testing and development environments, where it is often instantiated programmatically
/// and integrated with other job types like Class3DSelect for downstream processing.
/// 
/// In the application architecture, this job type is deeply integrated with the QueueRepository
/// for progress tracking and visualization generation, as well as with the DataManager for
/// job creation and cloning operations.
/// </remarks>
[GenerateReadOnly]
public class Class3D : RelionJob, IClusterJob, IPooledJob, IPoolStatus
{
    public override string TypeGuid => "e7f98e89-7ddc-48a0-89c8-6030f033f2d6";

    /// <summary>
    /// The unique category identifier for 3D classification jobs in the job type system.
    /// </summary>
    /// <remarks>
    /// This property is used by the DataRepository during job cloning and by the DataManager
    /// during job creation through Class3DExpandedView. It uniquely identifies this job
    /// type in the system's job type registry.
    /// </remarks>
    public override string TypeCategory => "Refinement.3D classes.Classify 3D";

    /// <summary>
    /// The full display name for this job type to be shown in the UI.
    /// </summary>
    /// <remarks>
    /// Used in job listings, menus, and when displaying QualifiedName properties in the
    /// user interface. Also used during job type registration in the DataModel.
    /// </remarks>
    public override string TypeName => "3D classification";

    /// <summary>
    /// A shortened display name for this job type, used in space-constrained UI elements.
    /// </summary>
    /// <remarks>
    /// Accessed through the ReadOnlyJob wrapper for display in compact UI components.
    /// </remarks>
    public override string TypeNameShort => "Class3D";

    /// <summary>
    /// Descriptive text explaining the purpose of this job type.
    /// </summary>
    /// <remarks>
    /// Used in job creation dialogs and tooltips to inform users about this job's functionality.
    /// Accessed through the ReadOnlyJob wrapper for display in UI elements.
    /// </remarks>
    public override string TypeDescription => "(Semi-)supervised classification of particles into multiple 3D classes with or without alignment";

    /// <summary>
    /// Determines the type of queue this job requires based on whether GPU acceleration is enabled.
    /// </summary>
    /// <remarks>
    /// This property is consulted by the QueueRepository to determine which queue manager should
    /// handle the job execution. When UseGpu is true, the job is routed to GPU-capable nodes.
    /// </remarks>
    public override JobQueueType QueueType =>
        IsPooled ? JobQueueType.CPU : (UseGpu ? JobQueueType.GPU : JobQueueType.CPU);

    /// <summary>
    /// Indicates that this job processes data in multiple iterations with intermediate results.
    /// </summary>
    /// <remarks>
    /// This property affects how the job is displayed and interacted with in the UI. Iterative
    /// jobs like Class3D allow navigation between intermediate results in the expanded view.
    /// Accessed through the ReadOnlyJob wrapper in UI components.
    /// </remarks>
    public override bool IsIterative => true;

    /// <summary>
    /// The UI component type to use for expanded view of this job.
    /// </summary>
    /// <remarks>
    /// Specifies that Class3DExpandedView should be used when displaying details of this job.
    /// This component provides specialized visualization and interaction for 3D classification results,
    /// including iteration navigation and class selection functionality.
    /// </remarks>
    public override Type ExpandedViewType => typeof(Class3DExpandedView);

    /// <summary>
    /// Calculates the grid dimensions for displaying class thumbnails in the job card view.
    /// The number of squares is determined by the number of classes, with special handling for
    /// different class count ranges to ensure an aesthetically pleasing layout.
    /// </summary>
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

    /// <summary>True when this job runs as a RELION disk-pool manager (CPU-only, relion_refine_pool).</summary>
    public bool IsPooled => UseWorkerPool;

    /// <summary>True when the pool workers run on GPUs (the manager stays CPU-only regardless).</summary>
    public bool IsGpuPool => UseWorkerPool && UseGpuWorkers;

    /// <summary>Name of the RELION pool coordination directory (--pool_dir) created under the job directory.</summary>
    public const string PoolDirName = "pool";

    public override string[] SupportedModules =>
        base.SupportedModules.Concat(["gpu", "cpu", "relion-pool"]).ToArray();

    // When pooled, relion-pool replaces the ordinary "relion" software tag, but "cpu" is retained:
    // the {{cpu}} template block carries the CPU partition/queue #SBATCH directives, so dropping it
    // would submit a partition-less job.
    public override string[] RequiredModules =>
        IsPooled ? ["cpu", "relion-pool"]
                 : base.RequiredModules.Concat(UseGpu ? ["gpu"] : ["cpu"]).ToArray();

    /// <summary>
    /// Fixed core/thread budget for the pool manager. It runs the CPU-side angular-accuracy
    /// estimation (now multithreaded), reconstruction and maximization, which scale with threads —
    /// so it gets a generous budget independent of the (typically smaller) per-worker count. Not
    /// user-exposed; the manager's cores are decoupled from the workers' in pool mode.
    /// </summary>
    private const int ManagerPoolCores = 16;

    public override int CoreCount => IsPooled ? ManagerPoolCores : NThreads;

    public override int MemoryGb => IsPooled ? MemoryPerWorker
                                             : Math.Max(NProcesses - 1, 1) * MemoryPerWorker;

    public override int GpuCount => IsPooled ? 0 : (UseGpu ? NGpus : 0);

    public override int ProcessCount => IsPooled ? 1 : NProcesses;

    public override bool CanBeFinalized => true;
    
    [Clearable]
    [RelayProperty]
    public int ContinuingFromIteration { get; set; } = 0;

    #region Parameters

    #region Reference

    [UiFieldGroup("Reference", 0)]
    [UiSymmetry("sym", "Symmetry",
                helpText: "The symmetry of the reference map. This is used to speed up the calculations. " +
                          "If you are unsure, use C1.")]
    [RelayProperty]
    public virtual string Symmetry { get; set; } = "C1";

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
    public virtual decimal InitialLowPass { get; set; } = 60m;

    [UiBool("trust_ref_size", "Resize reference if needed",
            isAdvanced: true,
            helpText: "If true, and if the input reference map (and mask) do not have the same pixel size " +
                      "and/or box size, then they will be re-scaled and re-boxed accordingly. If this " +
                      "option is set to false, then the program will die with an error if the reference " +
                      "does not have the correct pixel and/or box size.")]
    [RelayProperty]
    public virtual bool AutoResizeReference { get; set; } = true;

    [UiDecimal("low_resol_join_halves", "Half-map join resolution",
               min: 0.0,
               max: 10000.0,
               stepSize: 0.1,
               isAdvanced: true,
               helpText: "The resolution up to which the two half-maps will be joined between iterations " +
                         "to prevent them from drifting apart.",
               Unit = "Å")]
    [RelayProperty]
    public virtual decimal HalfmapJoinResolution { get; set; } = 40m;

    #endregion

    #region Optimization

    [UiFieldGroup("Optimization", 1)]
    [UiInt("K", "Number of classes",
           min: 1,
           max: 10000,
           stepSize: 1,
           helpText: "The number of classes (K) for a multi-reference refinement.")]
    [RelayProperty]
    public virtual int NClasses { get; set; } = 4;

    [UiFieldGroup("Optimization", 1)]
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
    public decimal TauFudge { get; set; } = 4;

    [UiInt("iter", "Number of iterations",
           min: 1,
           max: 10000,
           stepSize: 1,
           helpText: "Number of iterations to be performed. Note that the current implementation " +
                     "of 2D class averaging and 3D classification does NOT comprise a convergence " +
                     "criterium. Therefore, the calculations will need to be stopped by the user " +
                     "if further iterations do not yield improvements in resolution or classes.")]
    [RelayProperty]
    public virtual int NIterations { get; set; } = 25;

    [UiBool("fast_subsets", "Use fast subsets (for large data sets)",
            helpText: "If set to Yes, the first 5 iterations will be done with random subsets of " +
                      "only K*1500 particles (K being the number of classes); the next 5 with K*4500 " +
                      "particles, the next 5 with 30% of the data set; and the final ones with all " +
                      "data. This was inspired by a cisTEM implementation by Niko Grigorieff et al.")]
    [RelayProperty]
    public virtual bool UseFastSubsets { get; set; } = false;

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
    public virtual bool MaskWithZeros { get; set; } = true;

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
    public virtual int LimitAlignmentResolution { get; set; } = 0;

    [UiBool("blush", "Use Blush for regularization",
            helpText: "If set to Yes, refinement will use a neural network to perform regularisation " +
                      "by denoising at every iteration, instead of the standard smoothness regularisation.")]
    [RelayProperty]
    public virtual bool UseBlush { get; set; } = false;

    #endregion

    #region Alignment

    [UiFieldGroup("Alignment", 2)]
    [UiBool("skip_align", "Perform alignment",
            helpText: "If set to No, then rather than performing both alignment and classification, " +
                      "only classification will be performed. This allows the use of very focused " +
                      "masks. This requires that the particles already have optimal orientations " +
                      "associated with them.",
            reverse: true)]
    [RelayProperty]
    public bool DoAlignment { get; set; } = true;

    [UiEnum("", "Alignment type",
            enumType: typeof(Class3DAlignmentType),
            helpText: "The alignment type to be used. If set to Global, the entire angular space " +
                      "(ajusted for symmetry) will be explored. If set to Local, only a small " +
                      "angular range around the previous iteration's best pose will be explored, " +
                      "which can be a lot faster, but may get stuck in a local optimum.",
            ConditionalOnField = nameof(DoAlignment),
            ConditionalOnValue = true)]
    [RelayProperty]
    public Class3DAlignmentType AlignmentType { get; set; } = Class3DAlignmentType.Global;

    [UiDecimal("", "Local angular search range",
               min: 0.1,
               max: 180,
               stepSize: 0.1,
               helpText: "Local angular searches will be performed within +/- the given amount (in " +
                         "degrees) from the optimal orientation in the previous iteration. A " +
                         "Gaussian prior (also see previous option) will be applied, so that " +
                         "orientations closer to the optimal orientation in the previous iteration will " +
                         "get higher weights than those further away.",
               Unit = "°",
               ConditionalOnField = nameof(AlignmentType),
               ConditionalOnValue = Class3DAlignmentType.Local)]
    [RelayProperty]
    public decimal AngularSearchRange { get; set; } = 5m;

    [UiSymmetry("relax_sym", "Relax symmetry",
                helpText: "With this option, poses related to the standard local angular search range " +
                          "by the given point group will also be explored. For example, if you have a " +
                          "pseudo-symmetric dimer A-A', refinement or classification in C1 with symmetry " +
                          "relaxation by C2 might be able to improve distinction between A and A'. Note " +
                          "that the reference must be more-or-less aligned to the convention of (pseudo-)" +
                          "symmetry operators. For details, see Ilca et al 2019 and Abrishami et al 2020 " +
                          "cited in the About dialog.",
                ConditionalOnField = nameof(AlignmentType),
                ConditionalOnValue = Class3DAlignmentType.Local)]
    [RelayProperty]
    public string RelaxSymmetry { get; set; } = "";

    [UiHealpix("healpix_order", "Angular sampling",
               helpText: "There are only a few discrete angular samplings possible because we use " +
                         "the HealPix library to generate the sampling of the first two Euler angles " +
                         "on the sphere. The samplings are approximate numbers and vary slightly over " +
                         "the sphere.",
               ConditionalOnField = nameof(DoAlignment),
               ConditionalOnValue = true)]
    [RelayProperty]
    public int HealpixOrder { get; set; } = 3;

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
            helpText: "If set to Yes, the program will use coarser angular and translational " +
                      "sampling if the estimated accuracy of the assignments is still low in " +
                      "the earlier iterations. This may speed up the calculations.",
            ConditionalOnField = nameof(DoAlignment),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool AllowCoarserSampling { get; set; } = false;

    #endregion

    #region Helical

    [UiFieldGroup("Helical", 3)]
    [UiBool("helix", "Do helical reconstruction",
            helpText: "If set to Yes, the program will perform 3D helical reconstruction. " +
                      "This requires that the particles have been picked as filaments.")]
    [RelayProperty]
    public virtual bool DoHelical { get; set; } = false;

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
    public virtual float2 HelicalTubeDiameter { get; set; } = new(-1, -1);

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
    public virtual float3 HelicalAngleRange { get; set; } = new(-1, 15, 10);

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
    public virtual decimal HelicalRangeFactor { get; set; } = -1;

    [UiBool("helical_keep_tilt_prior_fixed", "Keep tilt prior fixed",
            isAdvanced: true,
            helpText: "If set to yes, the tilt prior will not change during the optimisation. If set to " +
                      "No, at each iteration the tilt prior will move to the optimal tilt value for that " +
                      "segment from the previous iteration.",
            ConditionalOnField = nameof(DoHelical),
            ConditionalOnValue = true)]
    [RelayProperty]
    public virtual bool HelicalKeepTiltPriorFixed { get; set; } = true;

    [UiBool("ignore_helical_symmetry", "Apply helical symmetry",
            helpText: "If set to Yes, helical symmetry will be applied in every iteration. Set to No if " +
                      "you have just started a project, helical symmetry is unknown or not yet estimated.",
            reverse: true,
            ConditionalOnField = nameof(DoHelical),
            ConditionalOnValue = true)]
    [RelayProperty]
    public virtual bool HelicalApplySymmetry { get; set; } = true;

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
    public virtual int HelicalNumberUniqueUnits { get; set; } = 1;

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
    public virtual decimal HelicalTwist { get; set; } = 0m;

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
    public virtual decimal HelicalRise { get; set; } = 0m;

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
    public virtual int HelicalCentralZLength { get; set; } = 30;

    [UiBool("helical_symmetry_search", "Do local searches of symmetry",
            helpText: "If set to Yes, then perform local searches of helical twist and rise within given ranges.",
            ConditionalOnField = nameof(DoHelical),
            ConditionalOnValue = true)]
    [RelayProperty]
    public virtual bool HelicalDoSymmetrySearch { get; set; } = false;

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
    public virtual float3 HelicalTwistRange { get; set; } = new(0);

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
    public virtual float3 HelicalRiseRange { get; set; } = new(0);

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
    public virtual bool DoCtfCorrection { get; set; } = true;

    [UiBool("ctf_intact_first_peak", "Ignore CTF until first peak?",
            isAdvanced: true,
            helpText: "If set to Yes, then CTF-amplitude correction will only be performed " +
                      "from the first peak of each CTF onward. This can be useful if the " +
                      "CTF model is inadequate at the lowest resolution. Still, in general " +
                      "using higher amplitude contrast on the CTFs (e.g. 0.1–0.2%) often " +
                      "yields better results. Therefore, this option is not generally " +
                      "recommended: Try processing your data with higher amplitude contrast first!")]
    [RelayProperty]
    public virtual bool IgnoreCtfUntilFirstPeak { get; set; } = false;

    #endregion

    #region Compute

    [UiFieldGroup("Compute", 5)]
    [UiString("scratch_dir", "Use scratch directory",
              helpText: "If a directory is provided here, then the job will create a sub-directory " +
                        "in it called relion_volatile. If that relion_volatile directory already " +
                        "exists, it will be wiped. Then, the program will copy all input particles " +
                        "into a large stack inside the relion_volatile subdirectory. Provided this " +
                        "directory is on a fast local drive (e.g. an SSD drive), processing in all " +
                        "the iterations will be faster. If the job finishes correctly, the " +
                        "relion_volatile directory will be wiped. If the job crashes, you may want " +
                        "to remove it yourself.",
              ConditionalOnField = nameof(UseWorkerPool),
              ConditionalOnValue = false)]
    [RelayProperty]
    public string UseScratch { get; set; } = null;

    [UiBool("", "Use worker pool",
            helpText: "Run this classification through RELION's disk-based worker pool: a CPU-only " +
                      "manager plus a fleet of CPU worker jobs maintained on a cluster queue. Turning " +
                      "this on makes the job CPU-only (RELION's pool has no GPU path yet) and replaces " +
                      "MPI. Leave off for the normal single-job (GPU/MPI) run.")]
    [RelayProperty]
    public bool UseWorkerPool { get; set; } = false;

    [UiQueue("Pool queue",
             helpText: "Cluster queue on which to maintain the CPU pool worker fleet.",
             ConditionalOnField = nameof(UseWorkerPool),
             ConditionalOnValue = true,
             IncludeLocal = false)]
    [RelayProperty]
    public int PoolQueueId { get; set; } = -1;

    [UiInt("", "Cores per worker",
           1, 99999, 1,
           helpText: "CPU cores requested for each pool worker (and the manager). Also sets RELION's " +
                     "--j threads for every pool process.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int CoresPerWorker { get; set; } = 2;

    [UiInt("", "Number of pool workers",
           1, 99999, 1,
           helpText: "Target number of CPU worker jobs maintained in the pool.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int NWorkers { get; set; } = 4;

    [UiInt("", "Particles per task",
           1, 9999999, 1,
           helpText: "Number of particles bundled into each pool task (RELION --pool_batch). Larger " +
                     "tasks amortize the per-task backprojector cost; smaller tasks spread the work " +
                     "more evenly across workers.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int ParticlesPerTask { get; set; } = 128;

    [UiBool("", "GPU workers",
            helpText: "Run the pool workers on GPUs instead of CPUs (the manager stays CPU-only). " +
                      "Requires a RELION-pool build with GPU support. Each worker cluster job is " +
                      "granted one GPU.",
            ConditionalOnField = nameof(UseWorkerPool),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool UseGpuWorkers { get; set; } = true;

    [UiInt("", "Processes per GPU",
           1, 99, 1,
           helpText: "Number of pool-worker processes launched per GPU worker job, all sharing that " +
                     "one GPU. Higher values improve GPU utilization at the cost of GPU memory.",
           ConditionalOnField = nameof(UseGpuWorkers),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int ProcessesPerGpu { get; set; } = 2;

    [UiBool("gpu", "Use GPU",
            helpText: "If set to Yes, the program will use the GPU for calculations. " +
                      "This will speed up the calculations significantly. If set to No, " +
                      "the calculations will be done on the CPU.",
            ConditionalOnField = nameof(UseWorkerPool),
            ConditionalOnValue = false)]
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
                     "compared to processes distributed over the same number of CPU cores.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = false)]
    [RelayProperty]
    public int NThreads { get; set; } = 2;

    [UiInt("", "Number of workers",
           1,
           99999,
           1,
           helpText: "The number of workers to use for the job. This is the number of MPI processes " +
                     "that will be started. When >1, 1 process is reserved for the work manager. The number of workers " +
                     "should not exceed the number of available CPU cores.",
           ConditionalOnField = nameof(UseWorkerPool),
           ConditionalOnValue = false)]
    [RelayProperty]
    public int NProcesses { get; set; } = 1;

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

    // Live pool-worker counters (written by QueueRepository each daemon tick; satisfy IPooledJob).
    [RelayProperty] [Clearable] public int PoolWorkersAlive { get; set; }
    [RelayProperty] [Clearable] public int PoolWorkersRunning { get; set; }
    [RelayProperty] [Clearable] public int PoolWorkersSubmitted { get; set; }

    #endregion

    #endregion

    #region Results paths

    protected string ResDataStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_data.star");
    protected string ResOptimizationSetStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_optimisation_set.star");
    protected string ResMapFile(int i, int c) => Path.Combine(DirectoryPath, $"run_it{i:D3}_class{c:D3}.mrc");
    protected string ResModelStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_model.star");
    protected string ResOptimiserStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_optimiser.star");
    protected string ResSamplingStarFile(int i) => Path.Combine(DirectoryPath, $"run_it{i:D3}_sampling.star");

    #endregion

    #region Visualization paths

    public string VisFilteredSlices(int i, int c) => Path.Combine(RelayResultsDirectoryPath,
                                                                  $"filtered_slices_it{i:D4}_class{c:D3}.png");

    public string VisMaskIsolines() => Path.Combine(RelayResultsDirectoryPath,
                                                    "mask_isolines.png");

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

    public const string PortInMaps = "Maps";
    public const string PortInMask = "Mask";
    public const string PortOutParticles = "Particles";
    public const string PortOutMaps = "Maps";
    public const string PortOutMask = "Mask";
    public const string PortOutOptimizer = "Optimizer";

    /// <summary>
    /// Initializes a new instance of the Class3D job with appropriate input and output ports.
    /// This constructor defines the required inputs (particles, reference map) and optional inputs (mask),
    /// as well as the expected outputs (classified particles, 3D class maps, and mask).
    /// </summary>
    public Class3D()
    {
        // Initialize input ports
        var portInParticles = new PortIn(this, typeof(ParticleSet), PortInParticles, "Particles", 1, int.MaxValue);
        var portInMaps = new PortIn(this, typeof(MapList), PortInMaps, "Reference", 1, 1);
        var portInMask = new PortIn(this, typeof(Mask), PortInMask, "Mask", 0, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInParticles.Name] = portInParticles,
            [portInMaps.Name] = portInMaps,
            [portInMask.Name] = portInMask
        });

        // Initialize output ports with resource delegates
        var portOutParticles = new PortOut(this, typeof(ParticleSet), PortOutParticles, "Particles", GetParticlesResource);
        var portOutMaps = new PortOut(this, typeof(MapList), PortOutMaps, "Classes", GetClassesResource);
        var portOutMask = new PortOut(this, typeof(Mask), PortOutMask, "Mask", GetMaskResource);
        var portOutOptimizer = new PortOut(this, typeof(ContinuableClass3D), PortOutOptimizer, "Continue classification", GetOptimizerResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutParticles.Name] = portOutParticles,
            [portOutMaps.Name] = portOutMaps,
            [portOutMask.Name] = portOutMask,
            [portOutOptimizer.Name] = portOutOptimizer
        });
    }

    /// <summary>
    /// Creates a ParticleSet resource representing the output particles from this job.
    /// </summary>
    /// <param name="iter">The iteration number to retrieve particles from, or -1 for the latest available iteration</param>
    /// <returns>A ParticleSet with paths to the classified particle data and relevant metadata flags</returns>
    protected virtual ParticleSet GetParticlesResource(int iter)
    {
        // Use the latest available iteration if not specified
        if (iter == -1)
            iter = VisAvailableIteration;

        // Start with input particles and update with classification results
        ParticleSet result = PortsIn[PortInParticles].Edges.First().Source.GetResource() as ParticleSet;

        // Update the path to point to the classified particles
        result.ParticlesSingleStarPath = ResDataStarFile(iter);

        // Set flags indicating that these particles have class assignments, scale factors, and orientation angles
        result.HasClasses = true;
        result.HasScale = true;
        result.HasAngles = true;

        if (result.IsTomo)
            result.OptimisationSetStarPath = ResOptimizationSetStarFile(iter);

        return result;
    }

    /// <summary>
    /// Creates a MapList resource representing the output 3D class reconstructions from this job.
    /// </summary>
    /// <param name="iter">The iteration number to retrieve maps from, or -1 for the latest available iteration</param>
    /// <returns>A MapList containing the 3D reconstructions for each class with visualization paths</returns>
    protected MapList GetClassesResource(int iter)
    {
        // Use the latest available iteration if not specified
        if (iter == -1)
            iter = VisAvailableIteration;

        // Create a list of maps, one for each class
        List<Map> Maps = new();

        for (int c = 1; c <= NClasses; c++)
            Maps.Add(new Map(
                             // Path to the actual 3D volume for this class
                             averageVolumePath: ResMapFile(iter, c),
                             isAbsoluteScale: true,
                             // Dictionary of paths to various visualizations for this class
                             visualizationPaths: new()
                             {
                                 { Map.VisTypes.OrthoSlices, VisFilteredSlices(iter, c) },
                                 { Map.VisTypes.Fsc, VisFsc(iter, c) },
                                 { Map.VisTypes.AngularDistribution, VisAngularDistribution(iter, c) },
                                 { Map.VisTypes.FourierSampling, VisFourierSampling(iter, c) },
                                 { Map.VisTypes.Statistics, VisClassStats(iter) }
                             }));

        // Return the MapList with all classes and the path to the model metadata
        return new MapList(Maps, ResModelStarFile(iter));
    }

    /// <summary>
    /// Creates a Mask resource representing the output mask from this job.
    /// </summary>
    /// <param name="iter">The iteration number to retrieve the mask from</param>
    /// <returns>A Mask object with the path to the mask volume</returns>
    /// <exception cref="NotImplementedException">
    /// Currently not implemented as Class3D doesn't typically produce a usable mask output
    /// </exception>
    protected Mask GetMaskResource(int iter)
    {
        return null;
    }
    
    protected ContinuableClass3D GetOptimizerResource(int iter)
    {
        if (iter < 0)
            iter = VisAvailableIteration;
        
        return new ContinuableClass3D(ResOptimiserStarFile(iter));
    }

    /// <summary>
    /// Gets the name of the command to execute for this job. Uses MPI-enabled version when running
    /// with multiple processes.
    /// </summary>
    /// <remarks>
    /// This property is used by the ClusterQueue when constructing the job submission script.
    /// It determines whether to use the standard RELION executable or the MPI-enabled version
    /// based on the specified number of processes. For parallel execution on multiple nodes,
    /// the mpirun command is used with the appropriate process count.
    /// 
    /// The CommandName is queried by DataRepository during job creation and by the QueueRepository 
    /// during job execution. It's combined with the arguments from ComposeCommandArguments to
    /// form the complete command line.
    /// </remarks>
    public override string CommandName =>
        IsPooled ? "relion_refine_pool"
                 : (NProcesses == 1 ? "relion_refine"
                                    : $"mpirun -n {NProcesses} relion_refine_mpi");

    /// <summary>
    /// Composes the command-line arguments for the RELION 3D classification job.
    /// This method builds a comprehensive dictionary of parameter name-value pairs
    /// that will be passed to the RELION command-line program.
    /// </summary>
    /// <returns>A dictionary of command-line arguments to be passed to the RELION program</returns>
    /// <remarks>
    /// This method is called by the ClusterQueue during job submission to generate the appropriate
    /// command-line arguments for RELION. It performs several critical functions:
    /// 
    /// 1. Handles RELION-specific parameter conversions (e.g., doubling the offset step)
    /// 2. Merges in any user-provided additional arguments
    /// 3. Sets up GPU configuration when enabled
    /// 4. Adds optimized default parameters for 3D classification
    /// 5. Retrieves and configures input files from connected resources
    /// 
    /// The method is used in test environments to verify command construction and is also invoked
    /// by JobDev testing tools to generate the full command string for analysis.
    /// 
    /// Like other RelionJob subclasses, this implementation follows a consistent pattern of
    /// parameter handling, making it easier to maintain across the codebase.
    /// </remarks>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        // Start with base arguments from RelionJob, IClusterJob
        var result = base.ComposeCommandArguments();

        var reference = PortsIn[PortInMaps].GetSingleResource<MapList>().Maps.First();

        // Apply RELION-specific adjustments to parameters
        // Note: RELION offset_step is 2x the UI value, and healpix_order is 1 lower than the UI value 
        result["offset_step"] = (OffsetSampling * 2).ToString(CultureInfo.InvariantCulture);
        result["healpix_order"] = Math.Max(1, HealpixOrder - 1).ToString(CultureInfo.InvariantCulture);

        // Add any user-provided additional arguments
        if (!string.IsNullOrWhiteSpace(AdditionalArguments))
            foreach (var kv in ArgumentStringToDictionary(AdditionalArguments))
                result[kv.Key] = kv.Value;

        // Add standard optimized parameters for RELION 3D classification
        result.TryAdd("pool", "10");
        result.TryAdd("pad", "2");
        result.TryAdd("dont_combine_weights_via_disc", "");
        result.TryAdd("flatten_solvent", "");
        result.TryAdd("oversampling", "1");
        result.TryAdd("norm", "");
        result.TryAdd("scale", "");

        if (!reference.IsAbsoluteScale)
            result.TryAdd("firstiter_cc", "");

        result.TryAdd("pipeline_control", DirectoryName);
        
        // RELION expects range / 3 for the sigma value of the Gaussian prior
        if (AlignmentType == Class3DAlignmentType.Local)
            result.TryAdd("sigma_ang", (AngularSearchRange / 3).ToString("F3", CultureInfo.InvariantCulture));

        // Set input files
        var particleSet = PortsIn[PortInParticles].GetSingleResource<ParticleSet>();
        
        if (!particleSet.HasData)
            throw new Exception("Input particles do not have associated particle stacks.");

        if (particleSet.IsTomo)
        {
            if (!string.IsNullOrWhiteSpace(particleSet.OptimisationSetStarPath))
                result.Add("ios", Space.GetRelativePath(particleSet.OptimisationSetStarPath));
            else
                throw new Exception("Input particles are from a tomography project, " +
                                    "but do not have an associated optimisation set.");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(particleSet.ParticlesSingleStarPath))
                result.Add("i", Space.GetRelativePath(particleSet.ParticlesSingleStarPath));
            else
                throw new Exception("Input particles do not have an associated particle star file.");
        }

        if (!string.IsNullOrWhiteSpace(reference.GetAverageOrSimilar()))
            result["ref"] = Space.GetRelativePath(reference.GetAverageOrSimilar());
        else
            throw new Exception("Input reference map is not available.");

        // Add optional mask if provided
        if (PortsIn[PortInMask].Edges.Count > 0)
        {
            var mask = PortsIn[PortInMask].Edges.First().Source.GetResource() as Mask;
            if (!string.IsNullOrWhiteSpace(mask?.MaskVolumePath))
                result["solvent_mask"] = Space.GetRelativePath(mask.MaskVolumePath);
            else
                throw new Exception("Input mask does not have an associated mask volume.");
        }

        // Set output prefix for all generated files
        result["o"] = Space.GetRelativePath(Path.Combine(DirectoryPath, "run"));

        // Applied last so pool-owned arguments win over anything above (incl. AdditionalArguments).
        if (IsPooled)
            ApplyPoolArguments(result);

        return result;
    }

    /// <summary>
    /// Applies the RELION disk-pool argument overrides shared by the manager and every worker:
    /// forces --j to the per-worker core count, points --pool_dir at the shared coordination
    /// directory, and drops CPU-pool-unsupported flags (--gpu, --scratch_dir). Kept as a public seam
    /// so it can be unit-tested without a fully connected input port graph.
    /// </summary>
    public Dictionary<string, string> ApplyPoolArguments(Dictionary<string, string> result)
    {
        // These are the manager's arguments; --j is the manager thread count. The worker command
        // (ComposeWorkerCommand) overrides --j down to CoresPerWorker for the per-worker E-step.
        result["j"] = ManagerPoolCores.ToString(CultureInfo.InvariantCulture);
        result["pool_batch"] = ParticlesPerTask.ToString(CultureInfo.InvariantCulture);
        result["pool_dir"] = Space.GetRelativePath(Path.Combine(DirectoryPath, PoolDirName));
        result.Remove("gpu");
        result.Remove("scratch_dir");
        return result;
    }

    #region Worker pool (IPooledJob)

    // DirectoryPath and the PoolWorkers* counters satisfy IPooledJob implicitly (public members).

    /// <summary>Target number of CPU worker jobs in the pool.</summary>
    public int PoolSize => NWorkers;

    // Explicit: the stored [UiQueue] value persists across toggles, but the pool machinery (which
    // reads PoolQueueId > 0) must only see a queue when the pool is actually on.
    int IPooledJob.PoolQueueId => UseWorkerPool ? PoolQueueId : -1;

    int IPooledJob.PoolSubmissionCap => PoolSize * 100;

    // The manager is always CPU-only; workers are CPU or GPU depending on UseGpuWorkers. A GPU worker
    // cluster job is granted one GPU and runs ProcessesPerGpu worker processes on it, so its node
    // cores/memory scale with that count; a CPU worker is a single process.
    Dictionary<string, string> IPooledJob.GetWorkerResourceValues(string workerLogDir)
    {
        var values = GetResourceValues();
        values["job_id"]      = $"{Id}-worker";
        values["n_processes"] = "1";
        if (IsGpuPool)
        {
            values["n_gpus"]    = "1";
            values["n_cores"]   = (ProcessesPerGpu * CoresPerWorker).ToString(CultureInfo.InvariantCulture);
            values["memory_gb"] = (ProcessesPerGpu * MemoryPerWorker).ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            values["n_gpus"]    = "0";
            values["n_cores"]   = CoresPerWorker.ToString(CultureInfo.InvariantCulture);
            values["memory_gb"] = MemoryPerWorker.ToString(CultureInfo.InvariantCulture);
        }
        values["std_out"]     = Path.Combine(workerLogDir, "%j.out");
        values["std_err"]     = Path.Combine(workerLogDir, "%j.err");
        return values;
    }

    // GPU workers run on the GPU partition; CPU workers on the CPU partition. Both load relion-pool.
    string[] IPooledJob.WorkerRequiredModules => IsGpuPool ? ["gpu", "relion-pool"] : ["cpu", "relion-pool"];

    // A RELION pool worker runs the same run as the manager, so it needs the manager's full science
    // arguments (RELION requires manager/worker arg parity), plus the worker role flags. 3D
    // classification workers all use --half 0.
    string IPooledJob.GetWorkerCommand(int deviceIndex) =>
        ComposeWorkerCommand(ComposeCommandArguments());

    /// <summary>
    /// Wraps a fully-composed argument set into a pool worker command. CPU: one relion_refine_pool
    /// --worker process. GPU: ProcessesPerGpu background processes sharing the single GPU this worker
    /// job was granted, then a wait. Public seam so it is unit-testable without a connected input port
    /// graph (the arg dict is supplied directly).
    ///
    /// GPU launch contract (docs/pool_gpu_runbook.md): each GPU worker gets an explicit empty device
    /// list <c>--gpu ""</c> — bare <c>--gpu</c> would make RELION's getOption() swallow the next token
    /// as a device id — plus <c>--gpu_shares K</c> where K is the number of workers sharing the GPU, so
    /// each reserves ~1/K of VRAM instead of fighting for all of it. ApplyPoolArguments already stripped
    /// --gpu from the args; the GPU worker re-adds the explicit form here.
    /// </summary>
    public string ComposeWorkerCommand(Dictionary<string, string> args)
    {
        // The manager composed these args with its own (larger) thread count; a worker's E-step uses
        // CoresPerWorker threads, so override --j back down here.
        args["j"] = CoresPerWorker.ToString(CultureInfo.InvariantCulture);

        string flat = string.Join(" ", args.Select(kv =>
            string.IsNullOrWhiteSpace(kv.Value) ? $"--{kv.Key}" : $"--{kv.Key} {kv.Value}"));

        if (!IsGpuPool)
            return $"cd {RunDirectory}\nrelion_refine_pool {flat} --worker --half 0";

        var lines = new List<string> { $"cd {RunDirectory}" };
        for (int i = 0; i < ProcessesPerGpu; i++)
            lines.Add($"relion_refine_pool {flat} --gpu \"\" --gpu_shares {ProcessesPerGpu} --worker --half 0 &");
        lines.Add("wait");
        return string.Join("\n", lines);
    }

    #endregion

    /// <summary>
    /// Tracks the size of the log file to detect when new iterations have been completed
    /// </summary>
    private long LastLogSize = -1;

    /// <summary>
    /// Tracks the progress of the job by monitoring and processing log files.
    /// This method reads the RELION output logs, parses them to determine how many iterations
    /// have completed, and extracts relevant sections of the log for each iteration.
    /// </summary>
    /// <returns>
    /// An action to be executed if log updates are detected, or null if no updates are needed
    /// </returns>
    /// <remarks>
    /// This method is a critical component of the progress tracking system and is called
    /// frequently by the QueueRepository during job execution. It performs several key functions:
    /// 
    /// 1. Monitors the RELION log file for changes and updates
    /// 2. Parses the log to identify iteration boundaries based on specific RELION output patterns
    /// 3. Extracts per-iteration log segments into separate files for easier access
    /// 4. Updates the LogsAvailableIteration property to reflect the current job progress
    /// 
    /// The returned Action is executed by the QueueRepository to update the job's state
    /// in the application's job tracking system. This method is designed to be efficient
    /// even when called frequently, as it only processes changes since the last invocation.
    /// 
    /// This method is used in testing environments to simulate job progress and is also
    /// directly invoked by JobDev program for validation of log parsing logic.
    /// </remarks>
    public override Action TrackProgressLogs()
    {
        var baseResult = base.TrackProgressLogs();

        Directory.CreateDirectory(RelayResultsDirectoryPath);

        int MaxLogsExist = -1;
        bool logFileChanged = false;

        #region Track logs

        if (File.Exists(PathStdOut))
        {
            MaxLogsExist = 0;
            long CurrentSize = new FileInfo(PathStdOut).Length;

            if (CurrentSize != LastLogSize)
            {
                // Log file has changed since last check
                logFileChanged = true;
                LastLogSize = CurrentSize;
                Span<string> LogLines = File.ReadAllText(PathStdOut).Split('\n');

                // Clean up RELION progress bar lines (remove carriage returns)
                for (int i = 0; i < LogLines.Length; i++)
                    if (LogLines[i].Contains('\r'))
                        LogLines[i] = LogLines[i].Substring(LogLines[i].LastIndexOf('\r') + 1);

                // Find the starting line for each iteration in the log
                Dictionary<int, int> IterationLines = new() { { ContinuingFromIteration, 0 } }; // Iteration 0 is always at line 0
                bool encounteredIterationStart = false;

                for (int i = 0; i < LogLines.Length; i++)
                    if (ContinuingFromIteration == 0)
                    {
                        if ((LogLines[i].StartsWith(" Auto-refine: Estimated") ||
                             LogLines[i].StartsWith(" Estimating initial noise")) && // C3D quirk - 1st iteration doesn't always start with "Auto-refine: Estimated"
                            (IterationLines.Count < 2 ||                             // But sometimes there is both "auto-refine" and "estimating",
                             i - IterationLines[IterationLines.Count - 1] > 8))      // then we don't want to double-count the iteration, so make sure there are at least 8 lines between them
                            try
                            {
                                IterationLines[IterationLines.Count] = i;
                            }
                            catch { }
                    }
                    else
                    {
                        if (LogLines[i].StartsWith(" Auto-refine: Estimated"))
                        {
                            if (encounteredIterationStart)
                                try
                                {
                                    IterationLines[IterationLines.Count + ContinuingFromIteration] = i;
                                }
                                catch { }
                            else
                                encounteredIterationStart = true;
                        }
                    }
                    

                if (IterationLines.Count > 0)
                {
                    // Get the latest iteration number from the log
                    MaxLogsExist = IterationLines.Select(kvp => kvp.Key).Max();

                    // Extract log segments for each iteration and write to separate files
                    foreach (var kvp in IterationLines)
                    {
                        // Skip updating logs for iterations that won't be updated anymore
                        if (kvp.Key < MaxLogsExist - 2)
                            continue;

                        // Determine the start and end line for this iteration
                        int Start = kvp.Value;
                        int End = IterationLines.ContainsKey(kvp.Key + 1) ? IterationLines[kvp.Key + 1] : LogLines.Length;

                        // Write the log segment to a file for this iteration
                        JobTools.WriteLogFile(string.Join('\n', LogLines.Slice(Start, End - Start).ToArray()),
                                              LogFilePath(kvp.Key));
                    }
                }
            }
            else
            {
                // Log size hasn't changed, maintain previous value
                MaxLogsExist = Math.Max(MaxLogsExist, LogsAvailableIteration);
            }
        }

        #endregion

        bool ReportUpdate = logFileChanged || MaxLogsExist > LogsAvailableIteration;

        // Return an action to update the iteration count if there are new iterations
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
    /// Tracks the progress of results by checking for output files and generating visualizations.
    /// This method monitors the output directories for new result files from each iteration and
    /// generates various visualizations (orthoslices, FSC plots, etc.) when new results are detected.
    /// </summary>
    /// <returns>
    /// An action to be executed if result updates are detected, or null if no updates are needed
    /// </returns>
    /// <remarks>
    /// This method is called frequently by the QueueRepository to detect when new result files 
    /// are available and to generate corresponding visualizations. It serves as a core component
    /// of the job's progress tracking system, handling several critical responsibilities:
    /// 
    /// 1. Checks for the existence of output map files for each iteration
    /// 2. Generates multiple types of visualizations in parallel using the Bakery library:
    ///    - Orthogonal slice views through 3D volumes
    ///    - Angular distribution plots showing particle orientations
    ///    - Fourier sampling coverage plots
    ///    - FSC (Fourier Shell Correlation) plots for resolution assessment
    /// 3. Extracts and stores class statistics (distribution, resolution, accuracy) as JSON
    /// 4. Creates summary visualizations for the job card display
    /// 
    /// This approach enables incremental UI updates as the job progresses, giving users
    /// immediate feedback on classification results without waiting for job completion.
    /// 
    /// The method is directly used in test environments via JobDev program for verification
    /// of visualization generation and is a key component of the application's responsiveness.
    /// </remarks>
    public override Action TrackProgressResults()
    {
        var baseResult = base.TrackProgressResults();

        Directory.CreateDirectory(RelayResultsDirectoryPath);

        int MaxResultsExist = -1;
        bool HasFinished = File.Exists(PathSuccess);

        // Check each iteration to see if results exist and need visualization
        for (int ires = ContinuingFromIteration; ires < LogsAvailableIteration + (HasFinished ? 1 : 0); ires++)
        {
            // Skip if the result file for the first class doesn't exist
            if (!File.Exists(ResMapFile(ires, 1)))
                break;

            // Check if visualizations need to be generated for this iteration
            if (!File.Exists(VisFilteredSlices(ires, 1)))
            {
                List<Task> VisTasks = new();

                // Task 1: Generate orthoslice visualizations for each class
                VisTasks.Add(Task.Run(() =>
                {
                    for (int c = 1; c <= NClasses; c++)
                        if (File.Exists(ResMapFile(ires, c)))
                            BakeryWrapper.MapOrthosliceAtlas(ResMapFile(ires, c),
                                                             1,
                                                             VisFilteredSlices(ires, c));
                }));

                // Task 2: Generate orientation and Fourier sampling plots
                VisTasks.Add(Task.Run(() =>
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

                // Task 3: Generate FSC plots
                VisTasks.Add(Task.Run(() =>
                {
                    if (File.Exists(ResModelStarFile(ires)))
                        BakeryWrapper.Class3DPerClassFscPlots(ResModelStarFile(ires),
                                                              Path.Combine(RelayResultsDirectoryPath,
                                                                           $"fsc_it{ires:D4}.png"));
                }));

                // Task 4: Extract and save class statistics as JSON
                VisTasks.Add(Task.Run(() =>
                {
                    try
                    {
                        if (File.Exists(ResModelStarFile(ires)))
                        {
                            Star TableIn = new(ResModelStarFile(ires), "model_classes");

                            Class3DModel[] Models = new Class3DModel[NClasses];

                            for (int c = 0; c < NClasses; c++)
                            {
                                // Extract class distribution (% of particles in this class)
                                float? Distribution = TableIn.HasColumn("rlnClassDistribution") ? TableIn.GetRowValueFloat(c, "rlnClassDistribution") : null;

                                if (Distribution.HasValue && !float.IsFinite(Distribution.Value))
                                    Distribution = 0;

                                // Extract estimated resolution for this class
                                float? Resolution = TableIn.HasColumn("rlnEstimatedResolution") ? TableIn.GetRowValueFloat(c, "rlnEstimatedResolution") : null;

                                if (Resolution.HasValue && !float.IsFinite(Resolution.Value))
                                    Resolution = 999;

                                // Extract angular accuracy
                                float? AccuracyRotations = TableIn.HasColumn("rlnAccuracyRotations") ? TableIn.GetRowValueFloat(c, "rlnAccuracyRotations") : null;

                                if (AccuracyRotations.HasValue && !float.IsFinite(AccuracyRotations.Value))
                                    AccuracyRotations = 999;

                                // Extract translational accuracy
                                float? AccuracyTranslations = TableIn.HasColumn("rlnAccuracyTranslationsAngst") ?
                                                                  TableIn.GetRowValueFloat(c, "rlnAccuracyTranslationsAngst") :
                                                                  null;

                                if (AccuracyTranslations.HasValue && !float.IsFinite(AccuracyTranslations.Value))
                                    AccuracyTranslations = 999;

                                // Create model object with all extracted metrics
                                Models[c] = new Class3DModel
                                {
                                    Id = c + 1,
                                    Distribution = Distribution,
                                    Resolution = Resolution,
                                    AccuracyRotations = AccuracyRotations,
                                    AccuracyTranslations = AccuracyTranslations
                                };
                            }

                            // Save the models as JSON
                            File.WriteAllText(VisClassStats(ires),
                                              JsonSerializer.Serialize(Models, new JsonSerializerOptions { WriteIndented = true }));
                        }
                    }
                    catch (Exception e)
                    {
                        Log.ForContext<Class3D>().Error(e, "Error processing class statistics for 3D classification iteration {Iteration}", ires);
                    }
                }));

                // Task 5: Generate a job card showing all classes
                VisTasks.Add(Task.Run(() => BakeryWrapper.Class3DJobCard(GetClassesResource(ires)
                                                                                       .Maps.Take(20)
                                                                                       .Select(m => m.AverageVolumePath)
                                                                                       .ToArray(),
                                                                                       Enumerable
                                                                                           .Range(start: 1, count: NClasses)
                                                                                           .Take(20)
                                                                                           .ToArray(),
                                                                                       VisCard(ires))));

                // Wait for all visualization tasks to complete
                Task.WaitAll(VisTasks.ToArray());

                MaxResultsExist = ires;

                // Only process one iteration at a time to avoid overloading the system
                break;
            }
        }

        bool ReportUpdate = MaxResultsExist > VisAvailableIteration;

        // Return an action to update the iteration count if there are new results
        if (ReportUpdate)
            return () =>
            {
                baseResult?.Invoke();
                VisAvailableIteration = MaxResultsExist;
            };
        else
            return baseResult;
    }
}

/// <summary>
/// Defines the alignment type options for 3D classification.
/// Global alignment searches the entire angular space, while
/// Local alignment only searches within a limited range around
/// the previous orientation.
/// </summary>
public enum Class3DAlignmentType
{
    /// <summary>
    /// Searches the entire angular space (adjusted for symmetry)
    /// </summary>
    Global = 0,

    /// <summary>
    /// Searches only a small angular range around the previous iteration's best orientation
    /// </summary>
    Local = 1
}

/// <summary>
/// Structure containing model statistics for a single 3D class.
/// Used to store and display key metrics about each class in the 3D classification.
/// </summary>
/// <remarks>
/// This model structure is serialized to JSON and used by both the Class3D and Class3DSelect jobs
/// for tracking and displaying quality metrics for each 3D class. It captures essential information
/// extracted from RELION's model.star files, particularly from the model_classes table.
/// 
/// The Class3DModel is used extensively in the expanded view components to display class statistics,
/// and is shared between various job types through the selection process. When users select classes
/// in the Class3DExpandedView, the associated models are passed to the Class3DSelect job to maintain
/// consistent metadata.
/// </remarks>
public struct Class3DModel
{
    /// <summary>
    /// The class number (1-based RELION class numbering)
    /// </summary>
    /// <remarks>
    /// This property corresponds to the class index in RELION's 1-based numbering system
    /// and is used as a key for identifying and matching classes during selection operations.
    /// The Class3DSelect job uses this ID to filter models when selecting specific classes.
    /// </remarks>
    public int Id { get; set; }

    /// <summary>
    /// The percentage of particles assigned to this class
    /// </summary>
    /// <remarks>
    /// This property represents the fraction of total particles assigned to this structural class,
    /// typically extracted from the rlnClassDistribution column in RELION's model.star file.
    /// It's a critical metric for evaluating class significance and is displayed prominently
    /// in the UI to help users identify populated classes worth further refinement.
    /// </remarks>
    public float? Distribution { get; set; } = null;

    /// <summary>
    /// The estimated resolution of this class in Angstroms
    /// </summary>
    /// <remarks>
    /// The resolution estimate for this 3D class, extracted from the rlnEstimatedResolution column
    /// in RELION's model.star file. Lower values indicate better resolution and higher quality
    /// reconstructions. This is a key metric for assessing class quality and is prominently
    /// displayed in the expanded view to guide users in selecting classes for further refinement.
    /// </remarks>
    public float? Resolution { get; set; } = null;

    /// <summary>
    /// The angular accuracy of particle alignments in this class, in degrees
    /// </summary>
    /// <remarks>
    /// This value represents the estimated accuracy of particle orientation assignments
    /// within this class, extracted from the rlnAccuracyRotations column. Lower values
    /// indicate more precise alignments and generally correlate with better map quality.
    /// </remarks>
    public float? AccuracyRotations { get; set; } = null;

    /// <summary>
    /// The translational accuracy of particle alignments in this class, in Angstroms
    /// </summary>
    /// <remarks>
    /// This value represents the estimated accuracy of particle shift assignments
    /// within this class, extracted from the rlnAccuracyTranslationsAngst column.
    /// Lower values indicate more precise alignments and generally correlate with better
    /// map quality.
    /// </remarks>
    public float? AccuracyTranslations { get; set; } = null;

    /// <summary>
    /// Default constructor required for JSON serialization/deserialization
    /// </summary>
    /// <remarks>
    /// This constructor is used by the JSON serializer when deserializing the model
    /// from the stats.json file. It's used both in the original Class3D job and in
    /// the Class3DSelect job when reading class statistics.
    /// </remarks>
    public Class3DModel() { }
}