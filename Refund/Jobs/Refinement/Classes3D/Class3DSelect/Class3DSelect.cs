using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.InitialModel.InitialReference3D;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Refinement.Classes3D.Class3DSelect;

/// <summary>
/// Class3DSelect enables the selection and extraction of specific 3D classes from a
/// Class3D or InitialReference job. It automatically filters particle sets based on
/// class membership and provides both the selected maps and corresponding particles 
/// for further processing.
/// </summary>
/// <remarks>
/// This job is typically created programmatically from the Class3DExpandedView when a user
/// selects classes of interest. It is hidden from the standard job menu as it is intended
/// to be created only through the UI selection interface, converting UI-based 0-indexed 
/// classes to RELION's 1-indexed class numbering system.
/// 
/// The job is critical in the 3D classification workflow, allowing users to select 
/// promising structural classes for further refinement or analysis. It creates a new
/// data.star file containing only particles belonging to the selected classes.
/// </remarks>
[GenerateReadOnly]
[HideFromMenu]
public class Class3DSelect : Job, ILocalJob
{
    public override string TypeGuid => "b777d711-621f-41c5-a7f6-ca013a2961d4";

    /// <summary>
    /// Unique type category identifier used in job creation, cloning, and type registration.
    /// </summary>
    public override string TypeCategory => "Refinement.3D classes.Select 3D classes";
    
    /// <summary>
    /// User-friendly name shown in the application interface.
    /// </summary>
    public override string TypeName => "3D class selection";
    
    /// <summary>
    /// Short identifier used in the data model and object serialization.
    /// </summary>
    public override string TypeNameShort => "Class3DSelect";
    
    /// <summary>
    /// Description shown in job type selection interface.
    /// </summary>
    public override string TypeDescription => "Store class selection from a 3D classification job";
    
    /// <summary>
    /// Specifies that this job runs locally without requiring cluster resources.
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;

    /// <summary>Runs locally on the CPU; requests no GPUs.</summary>
    public override int GpuCount => 0;
    
    /// <summary>
    /// Defines the component type to use for the expanded job view.
    /// </summary>
    public override Type ExpandedViewType => typeof(Class3DSelectExpandedView);
    
    /// <summary>
    /// Dynamically calculates the visualization grid layout based on the number of selected classes.
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
    
    /// <summary>
    /// The 1-based RELION class numbers that were selected for extraction.
    /// These are explicitly converted from 0-based UI indices when the job is created
    /// from Class3DExpandedView.
    /// </summary>
    [RelayProperty]
    public int[] SelectedClasses { get; set; } = [];

    /// <summary>
    /// The specific iteration from the parent job to extract classes from.
    /// Set programmatically from the ExpandedViewService.CurrentVisIteration when created.
    /// </summary>
    [RelayProperty]
    public int SelectedIteration { get; set; } = -1;
    
    /// <summary>
    /// The number of selected classes.
    /// </summary>
    public int NClasses => SelectedClasses.Length;
    
    #region Results paths
    
    /// <summary>
    /// Generates the filename for a selected class volume.
    /// </summary>
    /// <param name="c">The class number (1-based RELION numbering)</param>
    /// <returns>Filename for the class volume</returns>
    private string ResSelectedClass(int c) => $"class{c:D3}.mrc";
    
    /// <summary>
    /// Gets the full path to a selected class volume file.
    /// </summary>
    /// <param name="c">The class number (1-based RELION numbering)</param>
    /// <returns>Full path to the class volume file</returns>
    public string ResSelectedClassFile(int c) => Path.Combine(DirectoryPath, ResSelectedClass(c));
    
    /// <summary>
    /// Filename for the model.star file containing class metadata.
    /// </summary>
    private const string ResSelectedModelStar = "model.star";
    
    /// <summary>
    /// Full path to the model.star file containing class metadata.
    /// </summary>
    public string ResSelectedModelStarFile => Path.Combine(DirectoryPath, ResSelectedModelStar);
    
    /// <summary>
    /// Filename for the data.star file containing selected particle data.
    /// </summary>
    private const string ResSelectedDataStar = "data.star";
    
    /// <summary>
    /// Full path to the data.star file containing selected particle data.
    /// This is the primary output of the job that other jobs will use to access the filtered particles.
    /// </summary>
    public string ResSelectedDataStarFile => Path.Combine(DirectoryPath, ResSelectedDataStar);
    
    /// <summary>
    /// Filename for the optimisation_set.star file containing references to particles and tomograms (tomo only).
    /// </summary>
    private const string ResSelectedOptimisationSetStar = "optimisation_set.star";
    
    /// <summary>
    /// Full path to the optimisation_set.star file containing references to particles and tomograms (tomo only).
    /// </summary>
    public string ResSelectedOptimisationSetStarFile => Path.Combine(DirectoryPath, ResSelectedOptimisationSetStar);
    
    #endregion
    
    #region Visualization paths

    /// <summary>
    /// Gets the path to the orthogonal slices visualization for a specific class.
    /// </summary>
    /// <param name="c">The class number (1-based RELION numbering)</param>
    /// <returns>Path to the orthogonal slices visualization file</returns>
    public string VisFilteredSlices(int c) => Path.Combine(RelayResultsDirectoryPath,
                                                           $"filtered_slices_class{c:D3}.png");

    /// <summary>
    /// Gets the path to the FSC (Fourier Shell Correlation) curve visualization for a specific class.
    /// </summary>
    /// <param name="c">The class number (1-based RELION numbering)</param>
    /// <returns>Path to the FSC visualization file</returns>
    public string VisFsc(int c) => Path.Combine(RelayResultsDirectoryPath,
                                                $"fsc_class{c:D3}.png");

    /// <summary>
    /// Gets the path to the angular distribution visualization for a specific class.
    /// </summary>
    /// <param name="c">The class number (1-based RELION numbering)</param>
    /// <returns>Path to the angular distribution visualization file</returns>
    public string VisAngularDistribution(int c) => Path.Combine(RelayResultsDirectoryPath,
                                                                $"angular_distribution_class{c:D3}.png");

    /// <summary>
    /// Gets the path to the Fourier sampling visualization for a specific class.
    /// </summary>
    /// <param name="c">The class number (1-based RELION numbering)</param>
    /// <returns>Path to the Fourier sampling visualization file</returns>
    public string VisFourierSampling(int c) => Path.Combine(RelayResultsDirectoryPath,
                                                            $"fourier_sampling_class{c:D3}.png");

    /// <summary>
    /// Path to the JSON file containing class statistics.
    /// </summary>
    public string VisClassStats => Path.Combine(RelayResultsDirectoryPath,
                                                "stats.json");

    public string VisMap3d(int c) => ResSelectedClass(c);
    
    #endregion

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInParticles = "Particles";
    public const string PortInMaps = "Maps";
    public const string PortOutParticles = "Particles";
    public const string PortOutMaps = "Maps";
    
    /// <summary>
    /// Initializes a new instance of the Class3DSelect job with the necessary input and output ports.
    /// </summary>
    /// <remarks>
    /// This job connects to both the particle data and 3D maps from the parent job through its
    /// input ports, and provides filtered output through two output ports: one for the selected
    /// particle subset and one for the selected map volumes.
    /// </remarks>
    public Class3DSelect()
    {
        var portInParticles = new PortIn(this, typeof(ParticleSet), PortInParticles, "Particles", 1, 1);
        var portInMaps = new PortIn(this, typeof(MapList), PortInMaps, "3D classes", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInParticles.Name] = portInParticles,
            [portInMaps.Name] = portInMaps
        });

        var portOutSelectedParticles = new PortOut(this, typeof(ParticleSet), 
                                                   PortOutParticles, "Selected particles", 
                                                   GetSelectedParticlesResource);
        var portOutSelectedMaps = new PortOut(this, typeof(MapList),
                                              PortOutMaps, "Selected 3D classes",
                                              GetSelectedClassesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutSelectedParticles.Name] = portOutSelectedParticles,
            [portOutSelectedMaps.Name] = portOutSelectedMaps
        });
    }
    
    #region Resource locators

    /// <summary>
    /// Creates a ParticleSet resource containing only particles belonging to the selected classes.
    /// </summary>
    /// <param name="iter">The iteration number (unused since this job has only one iteration)</param>
    /// <returns>A ParticleSet resource pointing to the filtered particle data.star file</returns>
    /// <remarks>
    /// This method is used by the Particles output port to provide the filtered particle set
    /// to downstream jobs in the processing pipeline. It reuses the input ParticleSet's
    /// configuration but updates the path to point to the filtered data.star file.
    /// </remarks>
    private Resource GetSelectedParticlesResource(int iter)
    {
        ParticleSet result = PortsIn["Particles"].GetSingleResource<ParticleSet>(SelectedIteration);

        result.ParticlesSingleStarPath = ResSelectedDataStarFile;
        result.OptimisationSetStarPath = ResSelectedOptimisationSetStarFile;

        return result;
    }

    /// <summary>
    /// Creates a MapList resource containing the selected 3D class maps and their visualizations.
    /// </summary>
    /// <param name="iter">The iteration number (unused since this job has only one iteration)</param>
    /// <returns>A MapList resource containing the selected 3D class maps</returns>
    /// <remarks>
    /// This method is used by the Maps output port to provide the selected 3D class maps
    /// to downstream jobs. It includes all visualizations (orthogonal slices, FSC curves,
    /// angular distributions, etc.) for each selected class.
    /// </remarks>
    private Resource GetSelectedClassesResource(int iter) 
    {
        List<Map> maps = new();

        foreach (int c in SelectedClasses)
            maps.Add(new Map(averageVolumePath: ResSelectedClassFile(c),
                             visualizationPaths: new()
                             {
                                 { Map.VisTypes.OrthoSlices, VisFilteredSlices(c) },
                                 { Map.VisTypes.Fsc, VisFsc(c) },
                                 { Map.VisTypes.AngularDistribution, VisAngularDistribution(c) },
                                 { Map.VisTypes.FourierSampling, VisFourierSampling(c) },
                                 { Map.VisTypes.Statistics, VisClassStats }
                             },
                             isAbsoluteScale: true));

        return new MapList(maps);
    }

    #endregion
    
    /// <summary>
    /// Executes the Class3DSelect job locally to filter and extract selected classes.
    /// </summary>
    /// <param name="token">Cancellation token for interrupting the job</param>
    /// <remarks>
    /// This method performs three main operations:
    /// 1. Copies selected 3D map volumes and their visualizations from the parent job
    /// 2. Extracts relevant model.star information for the selected classes
    /// 3. Creates a filtered data.star file containing only particles belonging to the selected classes
    /// 
    /// The operation is lightweight as it primarily involves file copying and particle selection
    /// rather than heavy computational work.
    /// </remarks>
    public void RunLocal(CancellationToken token)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            try
            {
                Job parentJob = GetParents().First();
                if (parentJob is not (Class3D.Class3D or InitialReference))
                    throw new Exception("Parent job is not a 3D classification-like job");
                
                ParticleSet resourceParticles = PortsIn[PortInParticles].GetSingleResource<ParticleSet>(SelectedIteration);
                MapList resourceClasses = PortsIn[PortInMaps].GetSingleResource<MapList>(SelectedIteration);
                
                Map[] selectedMaps = SelectedClasses.Select(c => resourceClasses.Maps[c - 1]).ToArray();
                
                logger.WriteLine($"Performing 3D class selection from job {parentJob.AliasOrId}");

                #region Maps and visualizations
                
                logger.WriteLine("Copying maps... ");
                {
                    #region Copy maps

                    for (int i = 0; i < NClasses; i++)
                        File.Copy(selectedMaps[i].AverageVolumePath, 
                                  ResSelectedClassFile(SelectedClasses[i]));
                    
                    #endregion
                    
                    #region Copy visualizations
                    
                    for (int i = 0; i < NClasses; i++)
                    {
                        int c = SelectedClasses[i];

                        List<(string source, string target)> filesToCopy = new();
                        
                        if (selectedMaps[i].VisualizationPaths.ContainsKey(Map.VisTypes.OrthoSlices))
                            filesToCopy.Add((selectedMaps[i].VisualizationPaths[Map.VisTypes.OrthoSlices],
                                             VisFilteredSlices(c)));
                        
                        if (selectedMaps[i].VisualizationPaths.ContainsKey(Map.VisTypes.Fsc))
                            filesToCopy.Add((selectedMaps[i].VisualizationPaths[Map.VisTypes.Fsc],
                                             VisFsc(c)));
                        
                        if (selectedMaps[i].VisualizationPaths.ContainsKey(Map.VisTypes.AngularDistribution))
                            filesToCopy.Add((selectedMaps[i].VisualizationPaths[Map.VisTypes.AngularDistribution],
                                             VisAngularDistribution(c)));
                        
                        if (selectedMaps[i].VisualizationPaths.ContainsKey(Map.VisTypes.FourierSampling))
                            filesToCopy.Add((selectedMaps[i].VisualizationPaths[Map.VisTypes.FourierSampling],
                                             VisFourierSampling(c)));
                        
                        foreach (var (source, target) in filesToCopy)
                            if (File.Exists(source))
                                File.Copy(source, target);
                    }

                    // Generate a job card visualization showing the selected classes
                    BakeryWrapper.Class3DJobCard(
                        volumeFiles: selectedMaps.Take(20).Select(m => m.AverageVolumePath).ToArray(),
                        classNumbers: SelectedClasses.Take(20).ToArray(),
                        outputImageFile: VisCard(0)
                    );

                    // Copy and filter class statistics
                    if (selectedMaps[0].VisualizationPaths.ContainsKey(Map.VisTypes.Statistics) &&
                        File.Exists(selectedMaps[0].VisualizationPaths[Map.VisTypes.Statistics]))
                    {
                        Class3DModel[] models = JsonSerializer.Deserialize<Class3DModel[]>(File.ReadAllText(selectedMaps[0].VisualizationPaths[Map.VisTypes.Statistics]));
                        Class3DModel[] selectedModels = SelectedClasses.Select(c => models.FirstOrDefault(m => m.Id == c)).ToArray();

                        File.WriteAllText(VisClassStats, JsonSerializer.Serialize(selectedModels, new JsonSerializerOptions { WriteIndented = true }));
                    }
                    
                    #endregion
                }
                logger.WriteLine("Done.");
                
                #endregion
                
                #region model.star
                {
                    logger.WriteLine("Copying model tables... ");
                    {
                        // Extract and save only the model information for selected classes
                        Dictionary<string, Star> models = new();

                        foreach (var c in SelectedClasses)
                            models.Add($"model_class_{c}", new Star(resourceClasses.Model, $"model_class_{c}"));
                        
                        Star.SaveMultitable(ResSelectedModelStarFile, models);
                    }
                    logger.WriteLine("Done.");
                }
                
                #endregion
                
                #region data.star

                {
                    Star tableInGeneral = null;
                    Star tableInOptics;
                    Star tableInParticles;
                    int[] columnClasses;

                    Star tableOutSelected;
                    
                    logger.WriteLine("Reading particle table... ");
                    {
                        // Read the input particle data
                        if (Star.ContainsTable(resourceParticles.ParticlesSingleStarPath, "general"))
                            tableInGeneral = new StarParameters(resourceParticles.ParticlesSingleStarPath, "general");
                        tableInOptics = new Star(resourceParticles.ParticlesSingleStarPath, "optics");
                        tableInParticles = new Star(resourceParticles.ParticlesSingleStarPath, "particles");
                        
                        // Extract class assignments for all particles
                        columnClasses = tableInParticles.GetColumn("rlnClassNumber").Select(int.Parse).ToArray();
                    }
                    logger.WriteLine("Done.");
                    
                    logger.WriteLine("Selecting particles... ");
                    {
                        // Create a subset of particles that belong to the selected classes
                        int[] rowsSelected = columnClasses.Select((c, i) => (c, i))
                                                          .Where(t => SelectedClasses.Contains(t.Item1))
                                                          .Select(t => t.Item2)
                                                          .ToArray();
                        tableOutSelected = tableInParticles.CreateSubset(rowsSelected);
                        logger.WriteLine($"Selected {rowsSelected.Length} particles belonging to classes {string.Join(", ", SelectedClasses)}.");
                    }
                    logger.WriteLine("Done.");
                        
                    logger.WriteLine("Saving new particle table... ");
                    {
                        // Write the filtered particle data to a new STAR file
                        if (tableInGeneral == null)
                            Star.SaveMultitable(ResSelectedDataStarFile, new()
                            {
                                { "optics", tableInOptics },
                                { "particles", tableOutSelected }
                            });
                        else
                            Star.SaveMultitable(ResSelectedDataStarFile, new()
                            {
                                { "general", tableInGeneral },
                                { "optics", tableInOptics },
                                { "particles", tableOutSelected }
                            });
                    }
                    logger.WriteLine("Done.");
                }
                #endregion
                
                #region Tomo optimization set
                {
                    if (resourceParticles.IsTomo)
                    {
                        logger.WriteLine("Adapting optimisation_set.star...");

                        var setTable = new StarParameters(resourceParticles.OptimisationSetStarPath);
                        if (!setTable.HasColumn("rlnTomoParticlesFile") ||
                            !setTable.HasColumn("rlnTomoTomogramsFile"))
                            throw new Exception("Optimization set file must have rlnTomoParticlesFile and rlnTomoTomogramsFile columns.");
                        
                        setTable.SetRowValue(0, "rlnTomoParticlesFile", Space.GetRelativePath(ResSelectedDataStarFile));
                        setTable.Save(ResSelectedOptimisationSetStarFile);
                        
                        logger.WriteLine("Done.");
                    }
                }
                #endregion
                
                logger.WriteLine("Classes selected successfully.");
            }
            catch (Exception exc)
            {
                logger.WriteLine($"An error occurred: {exc}");
                throw;
            }

            VisAvailableIteration = 0;
        }
    }

    /// <summary>
    /// Tracks the progress of the job by monitoring log files.
    /// </summary>
    /// <returns>An action to update the job progress, or null if no update is needed</returns>
    /// <remarks>
    /// Since this is a fast, local job that completes quickly, this method simply marks
    /// the job as complete once it finishes. It's used by QueueRepository to track job progress
    /// and update the UI accordingly.
    /// </remarks>
    public override Action TrackProgressLogs()
    {
        if (LogsAvailableIteration < 0)
            return () =>
            {
                LogsAvailableIteration = 0;
            };
        
        return null;
    }
}