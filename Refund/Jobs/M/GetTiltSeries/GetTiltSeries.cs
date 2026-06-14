using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.M.CreatePopulation;
using Refund.UIFields;
using Warp.Sociology;
using Warp.Tools;

namespace Refund.Jobs.M.GetTiltSeries;

[GenerateReadOnly]
public class GetTiltSeries : LocalJob, ILocalJob
{
    /// <summary>
    /// Gets or sets the dimensions of the job card in the workflow editor.
    /// Import map job cards are shown in a 3x1 grid layout.
    /// </summary>
    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override string TypeGuid => "a27cacf0-8845-41bc-85cb-436e03226237";

    /// <summary>
    /// Gets the category of this job type for organization in the UI and type registration.
    /// </summary>
    public override string TypeCategory => "M.Get tilt series";

    /// <summary>
    /// Gets the full name of this job type for display in menus and the UI.
    /// </summary>
    public override string TypeName => "Get tilt series";

    /// <summary>
    /// Gets the abbreviated name of this job type for display in space-constrained areas.
    /// </summary>
    public override string TypeNameShort => "Get tilt series";

    /// <summary>
    /// Gets a brief description of this job type's purpose.
    /// </summary>
    public override string TypeDescription => "Get tilt series metadata with improved alignments from a population, to be used for Warp jobs";

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
    public const string PortInTiltSeries = "TiltSeries";
    public const string PortOutTiltSeries = "TiltSeries";

    #region Parameters

    /// <summary>
    /// Gets or sets the path to the map file to be imported.
    /// Must point to a valid MRC/MAP file on the filesystem.
    /// </summary>
    [UiFieldGroup("Parameters", 0)]
    [UiMDataSource("Data source", nameof(GetDataSourceData),
                   helpText: "Select the data source from which to get the tilt series metadata.")]
    [RelayProperty]
    public int? DataSourceId { get; set; } = null;

    public object GetDataSourceData(ReadOnlyJob job)
    {
        if (!job.PortsIn[PortInPopulation].IsConnected)
            return null;
        
        var population = job.PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
        return population?.DataSources.ToList();
    }

    #endregion

    /// <summary>
    /// Initializes a new instance of the ImportMap job.
    /// Configures the output port that will provide the imported map to downstream jobs.
    /// </summary>
    public GetTiltSeries()
    {
        var portInPopulation = new PortIn(this, typeof(MPopulation), PortInPopulation, "Population", 1, 1);
        var portInTiltSeries = new PortIn(this, typeof(TiltSeriesSet), PortInTiltSeries, "Tilt series", 1, 1);
        
        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInPopulation.Name] = portInPopulation,
            [portInTiltSeries.Name] = portInTiltSeries
        });

        var portOutTiltSeries = new PortOut(this, typeof(TiltSeriesSet), PortOutTiltSeries, "Tilt series", GetTs);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutTiltSeries.Name] = portOutTiltSeries
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
        
        if (DataSourceId == null)
            errors[nameof(DataSourceId)] = "Data source must be selected.";

        return errors;
    }

    private TiltSeriesSet GetTs(int iter)
    {
        if (!PortsIn[PortInPopulation].IsConnected)
            throw new InvalidOperationException("Population input port is not connected.");

        if (!PortsIn[PortInTiltSeries].IsConnected)
            throw new InvalidOperationException("Tilt series input port is not connected.");
        
        var tiltSeriesSet = PortsIn[PortInTiltSeries].GetSingleResource<TiltSeriesSet>();

        tiltSeriesSet.LatestMetadataDirectory = DirectoryPath;
        tiltSeriesSet.DataSet.DataDirectory = DirectoryPath;

        return tiltSeriesSet;
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
            
            var population = PortsIn[PortInPopulation].GetSingleResource<MPopulation>();
            var tiltSeriesSet = PortsIn[PortInTiltSeries].GetSingleResource<TiltSeriesSet>();
            
            if (DataSourceId >= population.DataSources.Count)
                throw new ArgumentOutOfRangeException(nameof(DataSourceId), 
                    $"Data source ID {DataSourceId} is out of range for the population with {population.DataSources.Count} data sources.");
            var dataSource = population.DataSources[DataSourceId.Value];
            
            var dataSourcePath = Path.GetDirectoryName(dataSource.CanonicalPath);
            
            logger.WriteLine($"Looking for refined metadata in {dataSourcePath}...");
            var refinedMetadata = Directory.EnumerateFiles(dataSourcePath, "*.xml")
                                           .Select(p => Path.GetFileNameWithoutExtension(p))
                                           .Where(n => n[0] != '.')
                                           .ToList();
            logger.WriteLine($"Found {refinedMetadata.Count} refined metadata files.");
            
            logger.WriteLine($"Looking for original metadata in {tiltSeriesSet.LatestMetadataDirectory}...");
            var originalMetadata = Directory.EnumerateFiles(tiltSeriesSet.LatestMetadataDirectory, "*.xml")
                                            .Select(p => Path.GetFileNameWithoutExtension(p))
                                            .Where(n => n[0] != '.')
                                            .ToList();
            logger.WriteLine($"Found {originalMetadata.Count} original metadata files.");
            
            var intersection = refinedMetadata.Intersect(originalMetadata).ToList();
            if (intersection.Count == 0)
                throw new InvalidOperationException("No matching metadata files found between refined and original metadata.");
            
            logger.WriteLine($"Found {intersection.Count} matching metadata files.");
            
            logger.WriteLine($"Looking for tomostar files in {tiltSeriesSet.DataSet.DataDirectory}...");
            var tomostar = Directory.EnumerateFiles(tiltSeriesSet.DataSet.DataDirectory, "*.tomostar")
                                    .Select(p => Path.GetFileNameWithoutExtension(p))
                                    .Where(n => n[0] != '.')
                                    .ToList();
            logger.WriteLine($"Found {tomostar.Count} tomostar files.");
            
            var tomostarIntersection = intersection.Intersect(tomostar).ToList();
            if (tomostarIntersection.Count == 0)
                throw new InvalidOperationException("No matching tomostar files found for the metadata files.");
            if (tomostarIntersection.Count != intersection.Count)
                logger.WriteLine($"Warning: number of metadata and tomostar files is not the same!");
            
            logger.WriteLine("Copying files to output directory...");
            
            foreach (var rootName in tomostarIntersection)
            {
                var sourceMetadataPath = Path.Combine(dataSourcePath, $"{rootName}.xml");
                var destMetadataPath = Path.Combine(DirectoryPath, $"{rootName}.xml");
                
                File.Copy(sourceMetadataPath, destMetadataPath, true);
                
                var sourceTomostarPath = Path.Combine(tiltSeriesSet.DataSet.DataDirectory, $"{rootName}.tomostar");
                var destTomostarPath = Path.Combine(DirectoryPath, $"{rootName}.tomostar");
                
                File.Copy(sourceTomostarPath, destTomostarPath, true);
            }
            
            logger.WriteLine("Done");
        }
    }

    public override Action TrackProgressResults()
    {
        var result = base.TrackProgressResults();

        // if (VisAvailableIteration < 0)
        // {
        //     return () =>
        //     {
        //         result?.Invoke();
        //         VisAvailableIteration = 0;
        //     };
        // }
        
        return result;
    }
}