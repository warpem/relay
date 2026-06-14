using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Sociology;
using Warp.Tools;

namespace Refund.Jobs.M.EstimateWeights;

[GenerateReadOnly]
public class EstimateWeights : WarpJobGpu, IClusterJob
{
    public override int2 CardSquareCount { get; set; } = new int2(2, 1);

    public override string TypeGuid => "a4aa779c-47ed-4b43-86d3-9c889d5a3f26";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "M.Estimate weights";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Estimate weights";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "EstimateWeights";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Estimates better weights for the data based on results from a previous M refinement step";

    /// <summary>
    /// Gets whether this job produces iterative results.
    /// </summary>
    public override bool IsIterative => false;

    /// <summary>
    /// Gets the type of the expanded view component for this job.
    /// Import jobs do not have a specialized expanded view.
    /// </summary>
    public override Type ExpandedViewType => null;

    public override string CommandName => $"EstimateWeights";

    protected override int DefaultMemoryPerWorker => 48;

    public override int CoreCount => CoresPerWorker;

    public override int MemoryGb => MemoryPerWorker;

    public override int GpuCount => 1;

    public override int ProcessCount => 1;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInPopulation = "Population";

    public const string PortOutPopulation = "Population";

    #region Parameters

    [UiFieldGroup("Fitting mode", 0)]
    [UiEnum("", "Estimation mode", typeof(WeightEstimationMode),
            helpText: "Choose whether to estimate weights resolved per-tilt and tilt-series, spatially, or to reset all existing weights.")]
    [RelayProperty]
    public WeightEstimationMode EstimationMode { get; set; } = WeightEstimationMode.Regular;

    #region Regular
    
    [UiBool("resolve_frames", "Resolve tilts",
            helpText: "Estimate weights per tilt, resulting in Ntilts weights. Can be combined with resolving per tilt-series, resulting in Ntilts * Ntiltseries weights.",
            ConditionalOnField = nameof(EstimationMode), ConditionalOnValue = WeightEstimationMode.Regular)]
    [RelayProperty]
    public bool ResolveTilts { get; set; } = true;

    [UiBool("resolve_items", "Resolve tilt-series",
            helpText:
            "Estimate weights per tilt-series, resulting in Ntiltseries weights. Can be combined with resolving per tilt, resulting in Ntilts * Ntiltseries weights.",
            ConditionalOnField = nameof(EstimationMode), ConditionalOnValue = WeightEstimationMode.Regular)]
    [RelayProperty]
    public bool ResolveItems { get; set; } = false;

    [UiBool("fit_anisotropy", "Fit anisotropy",
            helpText: "Fit anisotropic B-factors. Only makes sense when fitting per-tilt-series.",
            ConditionalOnField = nameof(EstimationMode), ConditionalOnValue = WeightEstimationMode.Regular)]
    [RelayProperty]
    public bool FitAnisotropy { get; set; } = false;

    #endregion
    
    #region Spatial
    
    [UiInt2("", "Resolution over field of view", min: 1,
            helpText: "Width and height of the parameter grid when resolving weights spatially over the field of view.",
            ConditionalOnField = nameof(EstimationMode), ConditionalOnValue = WeightEstimationMode.Spatial)]
    [RelayProperty]
    public int2 SpatialResolution { get; set; } = new int2(3, 3);
    
    #endregion
    
    [UiFieldGroup("Compute", 0)]
    [UiInt("", "Number of threads",
           min: 1,
           helpText: "Number of CPU cores to request.")]
    [RelayProperty]
    public int CoresPerWorker { get; set; } = 16;

    public override int NGpus { get; set; } = 1;
    public override int PerDevice { get; set; } = 1;

    #endregion

    public string ResSpeciesFolderPath(MSpecies species) => Path.Combine(DirectoryPath, "species", species.Name);
    public string ResSpeciesFilePath(MSpecies species) => Path.Combine(ResSpeciesFolderPath(species), $"{species.Name}.species");
    public string ResDataSourceFolderPath(MDataSource source) => Path.Combine(DirectoryPath, "data", source.Name);
    public string ResDataSourceFilePath(MDataSource source) => Path.Combine(ResDataSourceFolderPath(source), $"{source.Name}.source");

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public EstimateWeights()
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

        result["source"] = GetPopulation(0).DataSources.First().Name;

        if (EstimationMode == WeightEstimationMode.Spatial)
        {
            result["resolve_location"] = "";
            result["grid_width"] = SpatialResolution.X.ToString();
            result["grid_height"] = SpatialResolution.Y.ToString();
        }
        else if (EstimationMode == WeightEstimationMode.Reset)
        {
            result["reset"] = "";
        }

        result.Remove("strict");

        return result;
    }

    /// <summary>
    /// Creates and returns a Map resource from the imported map file.
    /// This method is called by the output port to provide data to downstream jobs.
    /// </summary>
    /// <param name="iter">The iteration number (not used as this job is non-iterative)</param>
    /// <returns>A Map resource pointing to the imported map file</returns>
    private MPopulation GetPopulation(int iter)
    {
        var population = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        population.MoveTo(DirectoryPath);
        
        return population;
    }

    public override void Stage()
    {
        base.Stage();

        var oldPopulation = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        var newPopulation = GetPopulation(0);

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
}

public enum WeightEstimationMode
{
    Regular,
    Spatial,
    Reset
}