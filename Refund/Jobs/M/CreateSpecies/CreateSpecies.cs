using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Sociology;
using Warp.Tools;

namespace Refund.Jobs.M.CreateSpecies;

[GenerateReadOnly]
public class CreateSpecies : WarpJobGpu, IClusterJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import map job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string TypeGuid => "2154c1c5-8b93-42cd-90ef-40c0efb71123";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "M.Create species";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Create species";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Create species";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Converts a particle class to a species and adds it to a population";

    /// <summary>
    /// Gets the queue type this job should be submitted to.
    /// Import jobs run locally as they typically involve only file I/O operations.
    /// </summary>
    /// <summary>
    /// Gets whether this job produces iterative results.
    /// Import jobs are non-iterative as they simply copy existing files.
    /// </summary>
    public override bool IsIterative => false;

    /// <summary>
    /// Gets the type of the expanded view component for this job.
    /// Import jobs do not have a specialized expanded view.
    /// </summary>
    public override Type ExpandedViewType => null;

    public override string CommandName => $"MTools create_species";

    public override int CoreCount => 4;

    public override int MemoryGb => 32;

    public override int GpuCount => 1;

    public override int ProcessCount => 1;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInPopulation = "Population";
    public const string PortInMap = "Map";
    public const string PortInMask = "Mask";
    public const string PortInParticles = "Particles";
    public const string PortOutPopulation = "Population";

    #region Parameters
    
    #region Species

    [UiFieldGroup("Species", 0)]
    [UiString("name", "Name",
            helpText: "Name of the species to be created")]
    [RelayProperty]
    public string SpeciesName { get; set; } = "";

    [UiInt("diameter", "Diameter",
           min: 2,
           max: 1000000,
           stepSize: 2,
           helpText: "Particle diameter in Angstroms")]
    [RelayProperty]
    public int DiameterAngst { get; set; } = 100;

    [UiString("sym", "Symmetry",
           helpText: "Point-group symmetry (e.g. C1, C2, D7, T, O, I).")]
    [RelayProperty]
    public string Symmetry { get; set; } = "C1";

    [UiDecimalNullable("angpix_resample", "Resample pixel size",
                       min: 0.1,
                       max: 1000,
                       stepSize: 0.01,
                       unit: "Å",
                       helpText: "Resample half-maps and masks to this pixel size (in Angstrom). Leave empty to keep original pixel size")]
    [RelayProperty]
    public decimal? AngPixResample { get; set; }
    
    [UiDecimalNullable("lowpass", "Limit resolution to",
                       min: 0.1,
                       max: 1000,
                       stepSize: 0.1,
                       unit: "Å",
                       helpText: "Optional low-pass filter (in Angstrom), applied to both half-maps")]
    [RelayProperty]
    public decimal? Lowpass { get; set; }

    [UiBool("dont_use_denoiser", "Use denoiser", reverse: true,
            helpText: "Use a denoiser for map regularization instead of a low-pass filter")]
    [RelayProperty]
    public bool UseDenoiser { get; set; } = true;
    
    #endregion
    
    #region Poses
    
    [UiFieldGroup("Particle poses", 1)]
    [UiDecimalNullable("angpix_coords", "Coordinate pixel size",
                       min: 0.01,
                       max: 1000,
                       stepSize: 0.0001,
                       unit: "Å",
                       helpText: "Override pixel size for RELION particle coordinates")]
    [RelayProperty]
    public decimal? AngPixRelionPos { get; set; }
    
    [UiDecimalNullable("angpix_shifts", "Shift pixel size",
                       min: 0.01,
                       max: 1000,
                       stepSize: 0.0001,
                       unit: "Å",
                       helpText: "Override pixel size for RELION particle shifts")]
    [RelayProperty]
    public decimal? AngPixRelionShifts { get; set; }

    [UiInt("temporal_samples", "Temporal samples",
           min: 1,
           max: 100,
           stepSize: 1,
           helpText: "Number of temporal samples to resolve in each particle's pose trajectory")]
    [RelayProperty]
    public int TemporalSamples { get; set; } = 2;

    [UiBool("ignore_unmatched", "Ignore unmatched particles",
            helpText: "Don't fail if there are particles that don't match any data sources.")]
    [RelayProperty]
    public bool IgnoreUnmatched { get; set; } = false;
    
    #endregion

    #region Helical

    [UiFieldGroup("Helical symmetry", 2)]
    [UiBool("", "Use helical symmetry",
            helpText: "Enable helical symmetry for this species")]
    [RelayProperty]
    public bool UseHelical { get; set; } = false;

    [UiInt("helical_units", "Number of helical subunits",
           min: 1,
           max: 1000000,
           stepSize: 1,
           helpText: "Number of helical subunits to average over",
           ConditionalOnField = nameof(UseHelical),
           ConditionalOnValue = true)]
    [RelayProperty]
    public int HelicalUnits { get; set; } = 1;

    [UiDecimal("helical_twist", "Twist",
               min: 0,
               max: 1000000,
               stepSize: 0.0001,
               unit: "°",
               helpText: "Helical twist in degrees, positive = right-handed",
               ConditionalOnField = nameof(UseHelical),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal HelicalTwist { get; set; } = 0M;

    [UiDecimal("helical_rise", "Rise",
               min: 0,
               max: 1000000,
               stepSize: 0.0001,
               unit: "Å",
               helpText: "Helical rise in Angstrom",
               ConditionalOnField = nameof(UseHelical),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal HelicalRise { get; set; } = 0M;

    [UiDecimal("helical_height", "Height",
               min: 0,
               max: 1000000,
               stepSize: 0.0001,
               unit: "Å",
               helpText: "Height of the helical segment along the Z axis in Angstrom",
               ConditionalOnField = nameof(UseHelical),
               ConditionalOnValue = true)]
    [RelayProperty]
    public decimal HelicalHeight { get; set; } = 100M;
    
    #endregion

    public override int PerDevice { get; set; } = 1;
    public override int MemoryPerWorker { get; set; } = 1;
    public override int NGpus { get; set; } = 1;

    #endregion

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public CreateSpecies()
    {
        var portInPopulation = new PortIn(this, typeof(MPopulation), PortInPopulation, "Population", 1, 1);
        var portInMap = new PortIn(this, typeof(MapList), PortInMap, "Half-maps", 1, 1);
        var portInMask = new PortIn(this, typeof(Mask), PortInMask, "Mask", 1, 1);
        var portInParticles = new PortIn(this, typeof(ParticleSet), PortInParticles, "Particles", 1, 1);
        
        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInPopulation.Name] = portInPopulation,
            [portInMap.Name] = portInMap,
            [portInMask.Name] = portInMask,
            [portInParticles.Name] = portInParticles
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
        
        if (string.IsNullOrWhiteSpace(SpeciesName))
            errors[nameof(SpeciesName)] = "Species source name cannot be empty.";
        
        if (PortsIn[PortInMap].GetSingleResource<MapList>() is {} mapList)
        {
            if (mapList.Maps.Count != 1)
                errors[PortInMap] = "Exactly one map must be provided.";

            var map = mapList.Maps.First();
            if (!map.HasHalf1Volume || !map.HasHalf2Volume)
                errors[PortInMap] = "Both half-maps must be provided.";
        }
        else
        {
            errors[PortInMap] = "Can't get half-maps from the input port.";
        }
        
        if (PortsIn[PortInPopulation].GetSingleResource<MPopulation>() is {} population)
            if (population.Species.Any(s => s.Name.Equals(SpeciesName, StringComparison.OrdinalIgnoreCase)))
                errors[nameof(SpeciesName)] = $"Species '{SpeciesName}' already exists in the population.";

        return errors;
    }

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["population"] = GetPopulation(0).CanonicalPath;
        
        var map = PortsIn[PortInMap].GetSingleResource<MapList>().Maps.First();
        result["half1"] = map.Half1VolumePath;
        result["half2"] = map.Half2VolumePath;
        
        var mask = PortsIn[PortInMask].GetSingleResource<Mask>();
        result["mask"] = mask.MaskVolumePath;
        
        var particles = PortsIn[PortInParticles].GetSingleResource<ParticleSet>();
        result["particles_relion"] = particles.ParticlesSingleStarPath;
        
        result["output"] = GetSpecies(0).CanonicalPath;
        result["dont_version"] = "";

        result.Remove("strict");

        return result;
    }

    private MPopulation GetPopulation(int iter)
    {
        var population = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();

        population.Species.Add(GetSpecies(iter));
        
        population.MoveTo(DirectoryPath);

        return population;
    }
    
    private MSpecies GetSpecies(int iter)
    {
        return new MSpecies()
        {
            Name = SpeciesName,
            PopulationDirectoryPath = DirectoryPath
        };
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
    public override Action TrackProgressResults()
    {
        var result = base.TrackProgressResults();

        bool hasFinished = File.Exists(PathSuccess);

        if (hasFinished && !File.Exists(VisCard(0)))
        {
            var species = GetSpecies(0);
            
            BakeryWrapper.MSpeciesJobCard(species.DenoisedMapPath,
                                          species.FscStarPath,
                                          species.CanonicalPath,
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