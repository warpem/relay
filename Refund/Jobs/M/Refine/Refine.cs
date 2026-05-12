using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Sociology;
using Warp.Tools;

namespace Refund.Jobs.M.Refine;

[GenerateReadOnly]
public class Refine : WarpJobGpu, IClusterJob
{
    public override int2 CardSquareCount
    {
        get
        {
            //if (!PortsIn[PortInPopulation].IsConnected)
            //    return new int2(2, 1);

            var nClasses = Math.Max(1, SpeciesCount);// PortsIn[PortInPopulation].GetSingleResource<MPopulation>().Species.Count;

            if (nClasses <= 5)
                return new int2(Math.Max(2, nClasses), 1);
            else
                return new int2(Math.Min(5, (nClasses + 3) / 4), 1);
        }
        set { }
    }

    public override string TypeGuid => "3c9eb4f3-19da-4a4b-aa96-aee9fd26a918";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "M.MRefinement";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "M refinement";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "M refinement";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Runs a single iteration of M's refinement algorithm on a population";

    /// <summary>
    /// Gets whether this job produces iterative results.
    /// </summary>
    public override bool IsIterative => false;

    /// <summary>
    /// Gets the type of the expanded view component for this job.
    /// Import jobs do not have a specialized expanded view.
    /// </summary>
    public override Type ExpandedViewType => typeof(RefineExpandedView);

    public override string CommandName => $"MCore";

    protected override int DefaultMemoryPerWorker => 8;

    public override int CoreCount => 2 * NGpus * PerDevice;

    public override int MemoryGb => MemoryPerWorker * NGpus * PerDevice;

    public override int ProcessCount => 1;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInPopulation = "Population";

    public const string PortOutPopulation = "Population";

    #region Parameters

    #region General

    [UiFieldGroup("General", 0)]
    [UiInt("iter", "Number of sub-iterations",
           min: 0,
           helpText: "Number of refinement sub-iterations. If set to 0, only reconstruction " +
                     "will be performed without refinement.")]
    [RelayProperty]
    public int Iterations { get; set; } = 3;

    [UiDecimal("first_iteration_fraction", "Initial resolution fraction",
               min: 0.1,
               max: 1.0,
               helpText: "Use this fraction of available resolution for alignment in first " +
                         "sub-iteration, increase linearly to 1.0 towards last sub-iterations")]
    [RelayProperty]
    public decimal FirstIterationFraction { get; set; } = 1M;

    [UiInt("min_particles", "Minimum particles",
           min: 1,
           helpText: "Only use series with at least N particles in the field of view")]
    [RelayProperty]
    public int NParticles { get; set; } = 1;

    [UiDecimal("weight_threshold", "Weight threshold",
               min: 0,
               max: 1.0,
               helpText: "Refine each tilt/frame up to the resolution at which the exposure " +
                         "weighting function (B-factor) reaches this value")]
    [RelayProperty]
    public decimal WeightThreshold { get; set; } = 0.05M;

    #endregion

    #region Geometry

    [UiFieldGroup("Particle poses", 1)]
    [UiBool("", "Refine image warp",
            helpText: "Refine image warp with a grid of XxY dimensions.")]
    [RelayProperty]
    public bool RefineImageWarp { get; set; } = false;

    [UiInt2("", "Image warp grid",
            min: 1,
            helpText: "Dimensions of the image warp grid to refine.",
            ConditionalOnField = nameof(RefineImageWarp),
            ConditionalOnValue = true)]
    [RelayProperty]
    public int2 RefineImageWarpGrid { get; set; } = new int2(1, 1);

    [UiBool("refine_particles", "Refine particle poses",
            helpText: "Refine particle poses")]
    [RelayProperty]
    public bool RefinePoses { get; set; } = false;

    [UiBool("refine_mag", "Refine anisotropic magnification",
            helpText: "Refine anisotropic magnification")]
    [RelayProperty]
    public bool RefineMag { get; set; } = false;

    [UiBool("refine_doming", "Refine doming",
            helpText: "Refine doming (frame series only)")]
    public bool RefineDoming { get; set; } = false;

    [UiBool("refine_stageangles", "Refine stage angles",
            helpText: "Refine stage angles (tilt series only)")]
    [RelayProperty]
    public bool RefineStageAngles { get; set; } = false;

    [UiBool("", "Refine volume warp",
            helpText: "Refine volume warp with a grid of XxYxZxT dimensions (tilt series only)")]
    public bool RefineVolumeWarp { get; set; } = false;

    [UiInt4("", "Volume warp grid",
            min: 1,
            helpText: "Dimensions of the volume warp grid to refine",
            ConditionalOnField = nameof(RefineVolumeWarp),
            ConditionalOnValue = true)]
    [RelayProperty]
    public int4 RefineVolumeWarpGrid { get; set; } = new int4(1, 1, 1, 1);

    [UiBool("refine_tiltmovies", "Refine tilt movies",
            helpText: "Refine tilt movie alignments (tilt series only)")]
    public bool RefineTiltMovies { get; set; } = false;

    #endregion

    #region CTF

    [UiFieldGroup("CTF", 2)]
    [UiDecimal("ctf_minresolution", "Minimum resolution",
               min: 0.1,
               max: 100.0,
               stepSize: 0.1,
               unit: "Å",
               helpText: "Use only species with at least this resolution (in Angstrom) for CTF refinement")]
    [RelayProperty]
    public decimal CtfMinResolution { get; set; } = 8M;

    [UiBool("ctf_defocus", "Refine defocus",
            helpText: "Refine defocus using a local search")]
    [RelayProperty]
    public bool CtfDefocus { get; set; } = false;

    [UiBool("ctf_defocusexhaustive", "Exhaustive defocus search",
            helpText: "Refine defocus using a more exhaustive grid search in the first sub-iteration; " +
                      "only works in combination with ctf_defocus",
            ConditionalOnField = nameof(CtfDefocus),
            ConditionalOnValue = true)]
    [RelayProperty]
    public bool CtfDefocusExhaustive { get; set; } = false;

    [UiBool("ctf_phase", "Refine phase shift",
            helpText: "Refine phase shift (phase plate data only)")]
    [RelayProperty]
    public bool CtfPhase { get; set; } = false;

    [UiBool("ctf_cs", "Refine spherical aberration",
            helpText: "Refine spherical aberration, which is also a proxy for pixel size")]
    [RelayProperty]
    public bool CtfCs { get; set; } = false;

    [UiBool("ctf_zernike3", "3rd order CTF aberrations",
            helpText: "Refine Zernike polynomials of 3rd order (beam tilt, trefoil – fast)")]
    [RelayProperty]
    public bool CtfZernike3 { get; set; } = false;

    [UiBool("ctf_zernike5", "5th order CTF aberrations",
            helpText: "Refine Zernike polynomials of 5th order (fast)")]
    [RelayProperty]
    public bool CtfZernike5 { get; set; } = false;

    [UiBool("ctf_zernike2", "2nd order CTF aberrations",
            helpText: "Refine Zernike polynomials of 2nd order (slow)")]
    public bool CtfZernike2 { get; set; } = false;

    [UiBool("ctf_zernike4", "4th order CTF aberrations",
            helpText: "Refine Zernike polynomials of 4th order (slow)")]
    public bool CtfZernike4 { get; set; } = false;

    #endregion

    #region Computation

    [UiFieldGroup("Computation", 3)]
    [UiInt("", "Number of GPUs",
           min: 1,
           helpText: "Number of GPUs to request for refinement.")]
    [RelayProperty]
    public override int NGpus { get; set; } = 1;

    [UiInt("perdevice_refine", "Workers per GPU",
           min: 1,
           helpText: "Number of worker processes per GPU used for refinement; " +
                     "set to >1 to improve utilization if your GPUs have enough memory")]
    [RelayProperty]
    public override int PerDevice { get; set; } = 1;

    [UiFieldGroup("Resources", 999)]
    [UiInt("", "Memory",
           min: 1,
           unit: "GB",
           helpText: "Amount of memory to request.")]
    [RelayProperty]
    public override int MemoryPerWorker { get; set; }

    [UiInt("ctf_batch", "Particle batch size",
           min: 1,
           helpText: "Batch size for CTF refinements. Lower = less memory, higher = faster")]
    [RelayProperty]
    public int CTFBatch { get; set; } = 32;

    [UiBool("cpu_memory", "Use CPU memory",
            helpText: "Use CPU memory to store particle images during refinement (GPU by default)")]
    [RelayProperty]
    public bool UseHostMemory { get; set; } = false;

    #endregion

    #endregion

    [Clearable]
    [RelayProperty]
    public int SpeciesCount = 1;

    public string ResSpeciesFolderPath(MSpecies species) => Path.Combine(DirectoryPath, "species", species.Name);
    public string ResSpeciesFilePath(MSpecies species) => Path.Combine(ResSpeciesFolderPath(species), $"{species.Name}.species");
    public string ResDataSourceFolderPath(MDataSource source) => Path.Combine(DirectoryPath, "data", source.Name);
    public string ResDataSourceFilePath(MDataSource source) => Path.Combine(ResDataSourceFolderPath(source), $"{source.Name}.source");

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public Refine()
    {
        var portInPopulation = new PortIn(this, typeof(MPopulation), PortInPopulation, "Population", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInPopulation.Name] = portInPopulation
        });

        var portOutPopulation = new PortOut(this, typeof(MPopulation), PortOutPopulation, "Population", GetPopulation);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutPopulation.Name] = portOutPopulation
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

        return errors;
    }

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();
        
        result["population"] = GetPopulation(0).CanonicalPath;
        
        if (RefineImageWarp)
            result["refine_imagewarp"] = $"{RefineImageWarpGrid.X}x{RefineImageWarpGrid.Y}";
        
        if (RefineVolumeWarp)
            result["refine_volumewarp"] = $"{RefineVolumeWarpGrid.X}x{RefineVolumeWarpGrid.Y}x{RefineVolumeWarpGrid.Z}x{RefineVolumeWarpGrid.W}";

        result["port"] = "0"; // Use port 0 to disable the built-in web server

        result.Remove("strict");

        return result;
    }

    /// <summary>
    /// Creates and returns a Map resource from the imported map file.
    /// This method is called by the output port to provide data to downstream jobs.
    /// </summary>
    /// <param name="iter">The iteration number (not used as this job is non-iterative)</param>
    /// <returns>A Map resource pointing to the imported map file</returns>
    public MPopulation GetPopulation(int iter)
    {
        var population = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        population.MoveTo(DirectoryPath);
        
        return population;
    }

    public override void Stage()
    {
        base.Stage();

        var oldPopulation = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        //oldPopulation.MoveTo(PortsIn[PortInPopulation].Edges.First().Source.Job.DirectoryPath);
        var newPopulation = GetPopulation(0);

        SpeciesCount = newPopulation.Species.Count;

        FileUtils.CopyDirectoryContents(oldPopulation.DirectoryPath, DirectoryPath,
                                        [NameSuccess, NameStdOut, NameStdErr, "submit.sh"],
                                        [".relay"]);

        Population.MoveSources(oldPopulation.DataSources.ToDictionary(s => s.Name,
                                                                  s => MDataSource.ToCanonicalPath(s.Name,
                                                                                                   DirectoryPath)),
                           newPopulation.CanonicalPath);

        Population.MoveSpecies(oldPopulation.Species.ToDictionary(s => s.Name,
                                                              s => MSpecies.ToCanonicalPath(s.Name,
                                                                                            DirectoryPath)),
                           newPopulation.CanonicalPath);
    }

    public override Action TrackProgressResults()
    {
        var result = base.TrackProgressResults();

        bool hasFinished = File.Exists(PathSuccess);

        if (hasFinished && !File.Exists(VisCard(0)))
        {
            var species = GetPopulation(0).Species.FirstOrDefault();

            if (species == null)
                return null;
            
            var allSpeciesPath = Path.GetDirectoryName(Path.GetDirectoryName(species.CanonicalPath));

            BakeryWrapper.MRefineJobCard(allSpeciesPath,
                                         VisCard(0));
            
            return () =>
            {
                result?.Invoke();
                VisAvailableIteration = 0;
            };
        }

        return result;
    }
}