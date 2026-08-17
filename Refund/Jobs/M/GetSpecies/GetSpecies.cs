using System.Globalization;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.M.GetSpecies;

[GenerateReadOnly]
public class GetSpecies : LocalJob, ILocalJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import map job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string TypeGuid => "6bc4c720-2850-46c8-b638-e3f82823a871";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "M.Get species";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Get species";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Get species";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Get maps and particle coordinates, to be used for Warp and RELION jobs";

    /// <summary>
    /// Gets the queue type this job should be submitted to.
    /// Import jobs run locally as they typically involve only file I/O operations.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;

    /// <summary>Runs locally on the CPU; requests no GPUs.</summary>
    public override int GpuCount => 0;

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

    public override Type CardViewType => null;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInPopulation = "Population";
    public const string PortOutMap = "Map";
    public const string PortOutParticles = "Particles";

    #region Parameters

    /// <summary>
    /// Gets or sets the path to the map file to be imported.
    /// Must point to a valid MRC/MAP file on the filesystem.
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiMSpecies("Species", nameof(GetSpeciesData),
                helpText: "Select the species for which to get the map and particle positions.")]
    [RelayProperty]
    public int? SpeciesId { get; set; } = null;

    public object GetSpeciesData(ReadOnlyJob job)
    {
        if (!job.PortsIn[PortInPopulation].IsConnected)
            return null;
        
        var population = job.PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        return population?.Species.ToList();
    }

    #endregion
    
    public string ResAverageFilePath => Path.Combine(DirectoryPath, "map.mrc");
    
    public string ResHalf1FilePath => Path.Combine(DirectoryPath, "map_half1.mrc");
    
    public string ResHalf2FilePath => Path.Combine(DirectoryPath, "map_half2.mrc");
    
    public string ResParticlesFilePath => Path.Combine(DirectoryPath, "particles.star");

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public GetSpecies()
    {
        var portInPopulation = new PortIn(this, typeof(MPopulation), PortInPopulation, "Population", 1, 1);
        
        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInPopulation.Name] = portInPopulation
        });

        var portOutMap = new PortOut(this, typeof(MapList), PortOutMap, "Map", GetMap);
        var portOutParticles = new PortOut(this, typeof(ParticleSet), PortOutParticles, "Particle positions", GetParticles);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutMap.Name] = portOutMap,
            [portOutParticles.Name] = portOutParticles
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
        
        if (SpeciesId == null)
            errors[nameof(SpeciesId)] = "Species must be selected.";

        return errors;
    }

    private MapList GetMap(int iter)
    {
        return new MapList([new Map(ResHalf1FilePath, ResHalf2FilePath, ResAverageFilePath)]);
    }
    
    private ParticleSet GetParticles(int iter)
    {
        return new ParticleSet
        {
            ParticlesSingleStarPath = ResParticlesFilePath,
            HasPositions = true,
            HasAngles = true,
            Has3dCoords = true,
            CoordPixelSize = 1
        };
    }

    /// <summary>
    /// Executes the map import operation locally.
    /// Copies the source file to the job directory and logs file metadata.
    /// </summary>
    /// <param name="token">Cancellation token for aborting the operation</param>
    public void RunLocal(CancellationToken token)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            try
            {
                ((StreamWriter)logger).AutoFlush = true;

                var population = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();

                if (SpeciesId >= population.Species.Count)
                    throw new ArgumentOutOfRangeException(nameof(SpeciesId),
                                                          $"Species ID {SpeciesId} is out of range for the population with {population.DataSources.Count} species.");

                var species = population.Species[SpeciesId.Value];

                logger.Write("Copying average and half-map files... ");

                {
                    File.Copy(species.FilteredMapPath, ResAverageFilePath, true);
                    token.ThrowIfCancellationRequested();

                    File.Copy(species.HalfMap1Path, ResHalf1FilePath, true);
                    token.ThrowIfCancellationRequested();

                    File.Copy(species.HalfMap2Path, ResHalf2FilePath, true);
                    token.ThrowIfCancellationRequested();
                }

                logger.WriteLine("Done");

                logger.Write("Converting STAR file... ");

                {
                    var tableIn = new Star(species.ParticlesStarPath);

                    float[] coordsX = tableIn.GetFloat("wrpCoordinateX1");
                    float[] coordsY = tableIn.GetFloat("wrpCoordinateY1");
                    float[] coordsZ = tableIn.GetFloat("wrpCoordinateZ1");

                    float[] angleRot = tableIn.GetFloat("wrpAngleRot1");
                    float[] angleTilt = tableIn.GetFloat("wrpAngleTilt1");
                    float[] anglePsi = tableIn.GetFloat("wrpAnglePsi1");

                    int[] subset = tableIn.GetColumn("wrpRandomSubset")
                                          .Select(s => int.TryParse(s, out var val) ? val : 0)
                                          .ToArray();

                    string[] sourceName = tableIn.GetColumn("wrpSourceName");

                    var tableOutParticles = new Star();
                    tableOutParticles.AddColumn("rlnCoordinateX", coordsX.Select(c => c.ToString("F5", CultureInfo.InvariantCulture)).ToArray());
                    tableOutParticles.AddColumn("rlnCoordinateY", coordsY.Select(c => c.ToString("F5", CultureInfo.InvariantCulture)).ToArray());
                    tableOutParticles.AddColumn("rlnCoordinateZ", coordsZ.Select(c => c.ToString("F5", CultureInfo.InvariantCulture)).ToArray());

                    tableOutParticles.AddColumn("rlnAngleRot", angleRot.Select(a => a.ToString("F5", CultureInfo.InvariantCulture)).ToArray());
                    tableOutParticles.AddColumn("rlnAngleTilt", angleTilt.Select(a => a.ToString("F5", CultureInfo.InvariantCulture)).ToArray());
                    tableOutParticles.AddColumn("rlnAnglePsi", anglePsi.Select(a => a.ToString("F5", CultureInfo.InvariantCulture)).ToArray());

                    tableOutParticles.AddColumn("rlnRandomSubset", subset.Select(s => s.ToString()).ToArray());

                    tableOutParticles.AddColumn("rlnTomoName", sourceName);
                    
                    tableOutParticles.AddColumn("rlnOpticsGroup", Enumerable.Repeat("1", coordsX.Length).ToArray());
                    
                    var tableOutOptics = new Star();
                    tableOutOptics.AddColumn("rlnOpticsGroup", ["1"]);
                    tableOutOptics.AddColumn("rlnImagePixelSize", ["1.0"]);

                    Star.SaveMultitable(ResParticlesFilePath, new()
                    {
                        {"optics", tableOutOptics}, 
                        {"particles", tableOutParticles}
                    });
                }

                logger.WriteLine("Done");
            }
            catch (Exception ex)
            {
                logger.WriteLine($"An error occurred: {ex.Message}");
                throw;
            }
        }
    }
    
    public override Action TrackProgressResults()
    {
        var result = base.TrackProgressResults();
        
        var population = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        if (SpeciesId == null || SpeciesId.Value >= population.Species.Count)
            return result;
        
        if (VisAvailableIteration < 0 &&
            !File.Exists(VisCard(0)))
        {
            BakeryWrapper.MGetSpeciesJobCard(population.SpeciesDirectoryPath, 
                                             population.Species[SpeciesId.Value].Name, 
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