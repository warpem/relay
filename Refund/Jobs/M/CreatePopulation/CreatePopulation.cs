using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Warp.Sociology;
using Warp.Tools;

namespace Refund.Jobs.M.CreatePopulation;

[GenerateReadOnly]
public class CreatePopulation : LocalJob, ILocalJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import map job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string TypeGuid => "5ab7353e-b3e6-46aa-b859-9e05c9744243";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "M.Create population";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Create population";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Create population";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Creates an M population to hold data sources and species for further processing";

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

    public override Type CardViewType => typeof(CreatePopulationCardContent);

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortOutPopulaiton = "Population";

    #region Parameters

    /// <summary>
    /// Gets or sets the path to the map file to be imported.
    /// Must point to a valid MRC/MAP file on the filesystem.
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiString("", "Name",
            helpText: "Name of the population to be created.")]
    [RelayProperty]
    public string PopulationName { get; set; } = "";

    [UiMLogo("Hue",
             "Give your M population a special hue.")]
    [RelayProperty]
    public int HueShift { get; set; } = 0;

    #endregion

    /// <summary>
    /// Gets the path where the imported map will be stored within the job directory.
    /// </summary>
    public string ResPopulationPath => Path.Combine(DirectoryPath, $"{PopulationName}.population");

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public CreatePopulation()
    {
        PortsIn = new(new Dictionary<string, PortIn>());

        var portOutPopulation = new PortOut(this, typeof(MPopulation), PortOutPopulaiton, "Population", GetPopulation);

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
        
        if (string.IsNullOrWhiteSpace(PopulationName))
            errors[nameof(PopulationName)] = "Population name cannot be empty.";

        //TODO: Implement validation for the rest of the parameters
        return errors;
    }

    /// <summary>
    /// Creates and returns a Map resource from the imported map file.
    /// This method is called by the output port to provide data to downstream jobs.
    /// </summary>
    /// <param name="iter">The iteration number (not used as this job is non-iterative)</param>
    /// <returns>A Map resource pointing to the imported map file</returns>
    private MPopulation GetPopulation(int iter)
    {
        return new MPopulation(null, null)
        {
            Name = PopulationName,
            DirectoryPath = DirectoryPath
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
            ((StreamWriter)logger).AutoFlush = true;
            
            logger.WriteLine($"Creating {PopulationName} population");

            Population population = new Population(ResPopulationPath)
            {
                Name = PopulationName
            };
            
            population.Save();
            
            logger.WriteLine("Done");
        }
    }

    public override Action TrackProgressResults()
    {
        var result = base.TrackProgressResults();

        if (VisAvailableIteration < 0)
        {
            return () =>
            {
                result?.Invoke();
                VisAvailableIteration = 0;
            };
        }
        
        return result;
    }
}