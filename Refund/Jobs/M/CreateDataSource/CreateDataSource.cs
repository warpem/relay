using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp.Sociology;
using Warp.Tools;

namespace Refund.Jobs.M.CreateDataSource;

[GenerateReadOnly]
public class CreateDataSource : WarpJob, IClusterJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import map job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string TypeGuid => "77a9970f-aaa4-4470-b893-6d0b2be2bde4";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "M.CreateDataSource";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Create data source";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Create data source";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Converts a set of tilt series to a data source and adds it to a population";

    /// <summary>
    /// Gets the queue type this job should be submitted to.
    /// Import jobs run locally as they typically involve only file I/O operations.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.CPU;

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
    
    public override string CommandName => $"MTools create_source";

    public override string[] SupportedModules => base.SupportedModules.Concat(["cpu"]).ToArray();

    public override string[] RequiredModules => base.RequiredModules.Concat(["cpu"]).ToArray();

    public override int CoreCount => 4;

    public override int MemoryGb => 8;

    public override int GpuCount => 0;

    public override int ProcessCount => 1;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInPopulation = "Population";
    public const string PortInTiltSeries = "TiltSeries";
    public const string PortOutPopulation = "Population";

    #region Parameters

    /// <summary>
    /// Gets or sets the path to the map file to be imported.
    /// Must point to a valid MRC/MAP file on the filesystem.
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiString("name", "Name",
            helpText: "Name of the data source to be created.")]
    [RelayProperty]
    public string DataSourceName { get; set; } = "";

    #endregion
    
    public string ResSettingsPath => Path.Combine(DirectoryPath, "processing.settings");

    /// <summary>
    /// Gets the path where the imported map will be stored within the job directory.
    /// </summary>
    public string ResDataSourcePath => GetDataSource(0).CanonicalPath;

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public CreateDataSource()
    {
        var portInPopulation = new PortIn(this, typeof(MPopulation), PortInPopulation, "Population", 1, 1);
        var portInTiltSeries = new PortIn(this, typeof(TiltSeriesSet), PortInTiltSeries, "Tilt series", 1, 1);
        
        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInPopulation.Name] = portInPopulation,
            [portInTiltSeries.Name] = portInTiltSeries
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
        
        if (string.IsNullOrWhiteSpace(DataSourceName))
            errors[nameof(DataSourceName)] = "Data source name cannot be empty.";

        return errors;
    }

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result.Remove("strict");
        
        var population = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        var seriesSet = PortsIn[PortInTiltSeries].GetSingleResource<TiltSeriesSet>();
        
        result["population"] = GetPopulation(0).CanonicalPath;
        result["processing_settings"] = ResSettingsPath;
        
        result["output"] = ResDataSourcePath;
        result["dont_version"] = "";

        return result;
    }

    public override void Stage()
    {
        base.Stage();
        
        var oldPopulation = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        var newPopulation = GetPopulation(0);
        
        var seriesSet = PortsIn[PortInTiltSeries].GetSingleResource<TiltSeriesSet>();
        
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
        
        var sourceDirectory = Path.GetDirectoryName(GetDataSource(0).CanonicalPath);
        Directory.CreateDirectory(sourceDirectory);
        
        foreach (var file in Directory.EnumerateFiles(seriesSet.LatestMetadataDirectory, "*.xml"))
            File.Copy(file, Path.Combine(sourceDirectory, Path.GetFileName(file)), true);
        
        var processingOptions = seriesSet.DataSet.ToOptionsWarp();
        processingOptions.Import.ProcessingFolder = sourceDirectory;
        processingOptions.Save(ResSettingsPath);
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
        
        population.DataSources.Add(GetDataSource(iter));
        
        population.MoveTo(DirectoryPath);

        return population;
    }
    
    private MDataSource GetDataSource(int iter)
    {
        return new MDataSource
        {
            Name = DataSourceName,
            PopulationDirectoryPath = DirectoryPath
        };
    }
}