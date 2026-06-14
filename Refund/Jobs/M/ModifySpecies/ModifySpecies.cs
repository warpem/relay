using System.Globalization;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Sociology;
using Warp.Tools;

namespace Refund.Jobs.M.ModifySpecies;

[GenerateReadOnly]
public class ModifySpecies : LocalJob, ILocalJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import map job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string TypeGuid => "e6b2de07-99a9-47e3-b02e-9818fe345373";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "M.Modify species";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Modify species";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Modify species";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Modifies parameters of an existing M species";

    /// <summary>
    /// Gets the queue type this job should be submitted to.
    /// Import jobs run locally as they typically involve only file I/O operations.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;

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
    public const string PortInMask = "Mask";
    public const string PortOutPopulation = "Population";

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
    
    [UiSymmetry("", "Symmetry",
                helpText: "Modify the species' point-group symmetry.")]
    [RelayProperty]
    public string Symmetry { get; set; } = null;

    [UiDecimalNullable("", "Pixel size",
                       min: 0.01, max: Int32.MaxValue, stepSize: 0.00001,
                       unit: "Å",
                       helpText: "Modify the species' pixel size in Å.")]
    [RelayProperty]
    public decimal? PixelSize { get; set; } = null;
    
    [UiIntNullable("", "Diameter",
                   min: 10, max: Int32.MaxValue, stepSize: 2,
                   unit: "Å",
                   helpText: "Modify the species' particle diameter in Å.")]
    [RelayProperty]
    public int? ParticleDiameter { get; set; } = null;

    [UiEnum("", "Use denoiser", typeof(NullableBool),
            helpText: "Modify whether to use noise2noise-based denoising for regularization. Default: True")]
    [RelayProperty]
    public NullableBool UseDenoiser { get; set; } = NullableBool.Unchanged;
    
    [UiEnum("", "Ewald curvature sign", typeof(EwaldSign),
            helpText: "Modify the species' Ewald curvature correction sign. Default: Negative")]
    [RelayProperty]
    public EwaldSign EwaldCurvatureSign { get; set; } = EwaldSign.Unchanged;
    
    [UiEnum("", "Use Ewald in refinement", typeof(NullableBool),
            helpText: "Modify whether to use Ewald curvature correction during refinement. Default: False")]
    [RelayProperty]
    public NullableBool UseEwaldInRefinement { get; set; } = NullableBool.Unchanged;

    public object GetSpeciesData(ReadOnlyJob job)
    {
        if (!job.PortsIn[PortInPopulation].IsConnected)
            return null;
        
        var population = job.PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        return population?.Species.ToList();
    }

    #endregion

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public ModifySpecies()
    {
        var portInPopulation = new PortIn(this, typeof(MPopulation), PortInPopulation, "Population", 1, 1);
        var portInMask = new PortIn(this, typeof(Mask), PortInMask, "New mask", 0, 1);
        
        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInPopulation.Name] = portInPopulation,
            [portInMask.Name] = portInMask
        });

        var portOutPopulation = new PortOut(this, typeof(MPopulation), PortOutPopulation, "Population", GetPopulation);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutPopulation.Name] = portOutPopulation
        });
    }

    private MPopulation GetPopulation(int iter)
    {
        if (SpeciesId == null)
            return null;
        
        var population = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        
        population.MoveTo(DirectoryPath);

        return population;
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

                var population = GetPopulation(0);

                if (SpeciesId == null || 
                    SpeciesId >= population.Species.Count)
                    throw new ArgumentOutOfRangeException(nameof(SpeciesId),
                                                          $"Species ID {SpeciesId} is out of range for the population with {population.DataSources.Count} species.");

                var species = population.Species[SpeciesId.Value];

                Species s = new Species(null, null, null);
                s.Load(species.CanonicalPath);
                
                logger.Write("Modifying species parameters... ");
                
                if (Symmetry != null)
                    s.Symmetry = Symmetry;

                if (PixelSize != null)
                    s.PixelSize = PixelSize.Value;
                
                if (ParticleDiameter != null)
                    s.DiameterAngstrom = ParticleDiameter.Value;
                
                if (UseDenoiser != NullableBool.Unchanged)
                    s.ApplyDenoising = UseDenoiser == NullableBool.True;

                if (EwaldCurvatureSign != EwaldSign.Unchanged)
                    s.EwaldReverse = EwaldCurvatureSign == EwaldSign.Positive;

                if (UseEwaldInRefinement != NullableBool.Unchanged)
                    s.DoEwald = UseEwaldInRefinement == NullableBool.True;
                
                s.Save();

                logger.WriteLine("Done");
                
                if (PortsIn[PortInMask].IsConnected)
                {
                    var mask = PortsIn[PortInMask].GetSingleResource<Mask>();
                    if (mask != null)
                    {
                        logger.Write("Updating species mask... ");
                        File.Copy(mask.MaskVolumePath, species.MaskPath, true);
                        logger.WriteLine("Done");
                    }
                }
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

public enum EwaldSign
{
    Unchanged,
    Negative,
    Positive
}

public enum NullableBool
{
    Unchanged,
    True,
    False
}