using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Jobs._2D.Class2D;
using Refund.Utils;
using Warp;
using Warp.Headers;
using Warp.Tools;

namespace Refund.Jobs._2D.Class2DSelect;

/// <summary>
/// Implements functionality for selecting specific classes from a 2D classification job
/// </summary>
/// <remarks>
/// This job is used to split particles based on their assigned 2D classes. It allows users
/// to choose which classes contain good particles, and separates the dataset into selected
/// and unselected particles and templates. This is a critical step in the data processing
/// workflow, as it helps remove bad particles showing contamination or ice, and improves
/// the dataset quality for subsequent 3D processing.
/// </remarks>
[GenerateReadOnly]
[HideFromMenu]
public class Class2DSelect : Job, ILocalJob
{
    public override string TypeGuid => "9baf2a2d-921c-4d51-9bea-55ebe04dd27b";

    /// <summary>
    /// Defines the aspect ratio of the job card in the workspace view
    /// </summary>
    public override int2 CardSquareCount { get; set; } = new int2(2, 1);
    
    /// <summary>
    /// The category path for this job type in the job creation menu
    /// </summary>
    public override string TypeCategory => "2D.Class2DSelect";
    
    /// <summary>
    /// The full descriptive name of this job type
    /// </summary>
    public override string TypeName => "2D class selection";
    
    /// <summary>
    /// The abbreviated name of this job type
    /// </summary>
    public override string TypeNameShort => "Class2DSelect";
    
    /// <summary>
    /// A brief description of what this job does
    /// </summary>
    public override string TypeDescription => "Store class selection from a 2D classification job";
    
    /// <summary>
    /// The queue type requirement for this job - always runs locally since it's a simple file operation
    /// </summary>
    public override JobQueueType QueueType => JobQueueType.Local;
    
    /// <summary>
    /// Specifies the component type to use for the expanded view of this job
    /// </summary>
    public override Type ExpandedViewType => typeof(Class2DSelectExpandedView);
    
    /// <summary>
    /// The list of class indices that were selected as containing good particles
    /// </summary>
    /// <remarks>
    /// Classes are 1-indexed, matching the RELION convention. These selected classes 
    /// will be extracted into separate output files and can be used for further processing.
    /// </remarks>
    [RelayProperty]
    public int[] SelectedClasses { get; set; } = [];

    /// <summary>
    /// The list of class indices that were explicitly marked as containing bad particles
    /// </summary>
    /// <remarks>
    /// Classes are 1-indexed, matching the RELION convention. These unselected classes
    /// will be separated into different output files, which can be useful for diagnostic
    /// purposes or for checking what was excluded.
    /// </remarks>
    [RelayProperty]
    public int[] UnselectedClasses { get; set; } = [];
    
    #region Results paths
    
    /// <summary>
    /// Filename for the selected class averages MRC stack
    /// </summary>
    private const string ResSelectedClasses = "selected_classes.mrcs";
    
    /// <summary>
    /// Full path to the selected class averages MRC stack
    /// </summary>
    public string ResSelectedClassesFile => Path.Combine(DirectoryPath, ResSelectedClasses);
    
    /// <summary>
    /// Filename for the selected class model STAR file
    /// </summary>
    private const string ResSelectedModelStar = "selected_model.star";
    
    /// <summary>
    /// Full path to the selected class model STAR file
    /// </summary>
    public string ResSelectedModelStarFile => Path.Combine(DirectoryPath, ResSelectedModelStar);
    
    /// <summary>
    /// Filename for the selected particles data STAR file
    /// </summary>
    private const string ResSelectedDataStar = "selected_data.star";
    
    /// <summary>
    /// Full path to the selected particles data STAR file
    /// </summary>
    public string ResSelectedDataStarFile => Path.Combine(DirectoryPath, ResSelectedDataStar);
    
    
    /// <summary>
    /// Filename for the unselected class averages MRC stack
    /// </summary>
    private const string ResUnselectedClasses = "unselected_classes.mrcs";
    
    /// <summary>
    /// Full path to the unselected class averages MRC stack
    /// </summary>
    public string ResUnselectedClassesFile => Path.Combine(DirectoryPath, ResUnselectedClasses);
    
    /// <summary>
    /// Filename for the unselected class model STAR file
    /// </summary>
    private const string ResUnselectedModelStar = "unselected_model.star";
    
    /// <summary>
    /// Full path to the unselected class model STAR file
    /// </summary>
    public string ResUnselectedModelStarFile => Path.Combine(DirectoryPath, ResUnselectedModelStar);
    
    /// <summary>
    /// Filename for the unselected particles data STAR file
    /// </summary>
    private const string ResUnselectedDataStar = "unselected_data.star";
    
    /// <summary>
    /// Full path to the unselected particles data STAR file
    /// </summary>
    public string ResUnselectedDataStarFile => Path.Combine(DirectoryPath, ResUnselectedDataStar);
    
    #endregion
    
    #region Visualization paths
    
    /// <summary>
    /// Path to the visualization atlas of selected class averages
    /// </summary>
    public string VisSelectedClassAtlas => Path.Combine(RelayResultsDirectoryPath, "selected_classes.png");
    
    /// <summary>
    /// Path to the visualization atlas of unselected class averages
    /// </summary>
    public string VisUnselectedClassAtlas => Path.Combine(RelayResultsDirectoryPath, "unselected_classes.png");
    
    /// <summary>
    /// Path to the JSON file containing statistics for selected classes
    /// </summary>
    public string VisSelectedClassStats => Path.Combine(RelayResultsDirectoryPath, "selected_stats.json");
    
    /// <summary>
    /// Path to the JSON file containing statistics for unselected classes
    /// </summary>
    public string VisUnselectedClassStats => Path.Combine(RelayResultsDirectoryPath, "unselected_stats.json");
    
    #endregion
    
    /// <summary>
    /// Initializes a new instance of the Class2DSelect job
    /// </summary>
    /// <remarks>
    /// Sets up input and output ports for the job. This job takes particle and template (class) inputs 
    /// from a 2D classification job and produces four outputs: selected particles, selected templates,
    /// unselected particles, and unselected templates. This allows subsequent jobs to either continue
    /// with the good particles or analyze the discarded particles.
    /// </remarks>
    public Class2DSelect()
    {
        // Create input ports - require exactly one particle set and one template set
        var portInParticles = new PortIn(this, typeof(ParticleSet), "Particles", "Particles", 1, 1);
        var portInTemplates = new PortIn(this, typeof(TemplateSet), "Templates", "2D classes", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInParticles.Name] = portInParticles,
            [portInTemplates.Name] = portInTemplates
        });

        // Create output ports for selected particles and templates
        var portOutSelectedParticles = new PortOut(this, typeof(ParticleSet), 
                                                   "Selected particles", "Selected particles", 
                                                   GetSelectedParticlesResource);
        var portOutSelectedTemplates = new PortOut(this, typeof(TemplateSet), 
                                                   "Selected templates", "Selected 2D classes", 
                                                   GetSelectedTemplatesResource);

        // Create output ports for unselected particles and templates
        var portOutUnselectedParticles = new PortOut(this, typeof(ParticleSet), 
                                                     "Unselected particles", "Unselected particles", 
                                                     GetUnselectedParticlesResource);
        var portOutUnselectedTemplates = new PortOut(this, typeof(TemplateSet), 
                                                     "Unselected templates", "Unselected 2D classes", 
                                                     GetUnselectedTemplatesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutSelectedParticles.Name] = portOutSelectedParticles,
            [portOutSelectedTemplates.Name] = portOutSelectedTemplates,
            
            [portOutUnselectedParticles.Name] = portOutUnselectedParticles,
            [portOutUnselectedTemplates.Name] = portOutUnselectedTemplates
        });
    }
    
    #region Resource locators

    /// <summary>
    /// Creates a ParticleSet resource for the selected particles output port
    /// </summary>
    /// <param name="iter">The iteration number (not used)</param>
    /// <returns>A ParticleSet resource containing particles from the selected classes</returns>
    /// <remarks>
    /// This resource points to the STAR file containing only particles from the classes
    /// that were marked as selected.
    /// </remarks>
    private Resource GetSelectedParticlesResource(int iter)
    {
        ParticleSet result = PortsIn["Particles"].Edges[0].Source.GetResource() as ParticleSet;

        result.ParticlesSingleStarPath = ResSelectedDataStarFile;

        return result;
    }

    /// <summary>
    /// Creates a TemplateSet resource for the selected templates output port
    /// </summary>
    /// <param name="iter">The iteration number (not used)</param>
    /// <returns>A TemplateSet resource containing class averages from the selected classes</returns>
    /// <remarks>
    /// This resource points to the files containing only the class averages and their metadata
    /// from the classes that were marked as selected.
    /// </remarks>
    private Resource GetSelectedTemplatesResource(int iter) => new TemplateSet(ResSelectedModelStarFile, 
                                                                               ResSelectedClassesFile,
                                                                               VisSelectedClassStats);

    /// <summary>
    /// Creates a ParticleSet resource for the unselected particles output port
    /// </summary>
    /// <param name="iter">The iteration number (not used)</param>
    /// <returns>A ParticleSet resource containing particles from the unselected classes</returns>
    /// <remarks>
    /// This resource points to the STAR file containing only particles from the classes
    /// that were marked as unselected.
    /// </remarks>
    private Resource GetUnselectedParticlesResource(int iter)
    {
        ParticleSet result = PortsIn["Particles"].Edges[0].Source.GetResource() as ParticleSet;

        result.ParticlesSingleStarPath = ResUnselectedDataStarFile;

        return result;
    }

    /// <summary>
    /// Creates a TemplateSet resource for the unselected templates output port
    /// </summary>
    /// <param name="iter">The iteration number (not used)</param>
    /// <returns>A TemplateSet resource containing class averages from the unselected classes</returns>
    /// <remarks>
    /// This resource points to the files containing only the class averages and their metadata
    /// from the classes that were marked as unselected.
    /// </remarks>
    private Resource GetUnselectedTemplatesResource(int iter) => new TemplateSet(ResUnselectedModelStarFile, 
                                                                                 ResUnselectedClassesFile,
                                                                                 VisUnselectedClassStats);

    #endregion
    
    /// <summary>
    /// Executes the class selection job locally, splitting selected and unselected particles
    /// </summary>
    /// <param name="token">Cancellation token to abort the operation</param>
    /// <remarks>
    /// This method performs the actual operation of selecting classes and separating the particles.
    /// It performs the following steps:
    /// 1. Creates subsets of class average images for selected and unselected classes
    /// 2. Creates visualizations of these class average subsets
    /// 3. Copies and updates the model STAR files for both subsets
    /// 4. Extracts statistics about selected and unselected classes
    /// 5. Separates particles into selected and unselected groups based on their class assignments
    /// </remarks>
    public void RunLocal(CancellationToken token)
    {
        Directory.CreateDirectory(RelayResultsDirectoryPath);

        using (TextWriter logger = File.CreateText(LogFilePath(0)))
        {
            try
            {
                // Verify parent job is a 2D classification
                Job ParentJob = GetParents().First();
                if (ParentJob is not Class2D.Class2D)
                    throw new Exception("Parent job is not a 2D classification job");
                
                // Get input resources
                ParticleSet ResourceParticles = (ParticleSet)PortsIn["Particles"].Edges[0].Source.GetResource();
                TemplateSet ResourceTemplates = (TemplateSet)PortsIn["Templates"].Edges[0].Source.GetResource();
                
                logger.WriteLine($"Performing 2D class selection from job {ParentJob.AliasOrId}");

                #region classes.mrcs - Extract class average images
                
                logger.WriteLine("Copying class templates... ");
                {
                    #region Read in class images and save selected and unselected subsets
                    
                    // Locate the class average MRC stack
                    string TemplatesPath = ResourceTemplates.TemplateMrcPath;
                    if (!File.Exists(TemplatesPath))
                        throw new Exception("Class images not found");
                    
                    // Read the MRC header to determine stack size
                    MapHeader Header = MapHeader.ReadFromFile(TemplatesPath);
                    
                    // Verify class indices are valid
                    if (SelectedClasses.Max() > Header.Dimensions.Z)
                        throw new Exception("Selected class index exceeds class image stack size");

                    if(UnselectedClasses.Any() && UnselectedClasses.Max() > Header.Dimensions.Z)
                        throw new Exception("Unselected class index exceeds class image stack size");

                    // Extract selected class averages and write to new file
                    using(var SelectedTemplates = Image.FromFile(TemplatesPath, 
                                                                 new int2(2), 0, typeof(float), 
                                                                 SelectedClasses.Select(i => i - 1).ToArray()))
                        SelectedTemplates.WriteMRC16b(ResSelectedClassesFile);

                    // Extract unselected class averages if any
                    if(UnselectedClasses.Any())
                    {
                    using(var UnselectedTemplates = Image.FromFile(TemplatesPath, 
                                                                   new int2(2), 0, typeof(float), 
                                                                   UnselectedClasses.Select(i => i - 1).ToArray()))
                        UnselectedTemplates.WriteMRC16b(ResUnselectedClassesFile);
                    }
                    #endregion
                    
                    #region Visualize - Generate image atlases for classes
                    
                    // Create tasks for visualization generation to run in parallel
                    Task TaskSelected = Task.Run(() => BakeryWrapper.Class2DImageAtlas(ResSelectedClassesFile,
                                                                                      VisSelectedClassAtlas));
                    Task TaskUnselected = Task.Run(() => BakeryWrapper.Class2DImageAtlas(ResUnselectedClassesFile,
                                                                                        VisUnselectedClassAtlas));

                    // Create job card visualization showing selected classes
                    Task TaskCard = Task.Run(() => BakeryWrapper.Class2DJobCard(
                        classImagesMrcsFile: ResSelectedClassesFile,
                        imageIndices: Enumerable.Range(0, SelectedClasses.Length).ToArray(),
                        imageLabels: SelectedClasses.Select(i => i.ToString()).ToArray(),
                        outputImageFile: VisCard(0))
                    );
                    
                    // Wait for all visualization tasks to complete
                    Task.WaitAll(TaskSelected, TaskUnselected, TaskCard);
                    
                    #endregion
                }
                logger.WriteLine("Done.");
                
                #endregion
                
                #region model.star - Update class models

                logger.WriteLine("Copying class model data... ");
                {
                    // Locate the model STAR file
                    string ModelPath = ResourceTemplates.ModelStarPath;
                    if (!File.Exists(ModelPath))
                        throw new Exception("Class model data not found");

                    // Read the model STAR file
                    Star TableIn = new Star(ModelPath, "model_classes");
                    
                    // Remove VDAM-specific gradient moments (not needed for model)
                    if (TableIn.HasColumn("rlnGradMoment1"))
                        TableIn.RemoveColumn("rlnGradMoment1");
                    if (TableIn.HasColumn("rlnGradMoment2"))
                        TableIn.RemoveColumn("rlnGradMoment2");
                    
                    // Create subset for selected classes
                    Star TableOutSelected = TableIn.CreateSubset(SelectedClasses.Select(i => i - 1).ToArray());
                    {
                        // Update reference image paths to point to the new class average file
                        string[] ColumnImages = TableOutSelected.GetColumn("rlnReferenceImage");

                        for (int r = 0; r < ColumnImages.Length; r++)
                        {
                            string[] Parts = ColumnImages[r].Split('@');
                            ColumnImages[r] = (r + 1).ToString("D6") + "@" + 
                                              Path.Combine(DirectoryPathInSpace, ResSelectedClasses);
                        }
                        
                        TableOutSelected.SetColumn("rlnReferenceImage", ColumnImages);
                    }
                    TableOutSelected.Save(ResSelectedModelStarFile);

                    // Create subset for unselected classes
                    Star TableOutUnselected = TableIn.CreateSubset(UnselectedClasses.Select(i => i - 1).ToArray());
                    {
                        // Update reference image paths to point to the new class average file
                        string[] ColumnImages = TableOutUnselected.GetColumn("rlnReferenceImage");

                        for (int r = 0; r < ColumnImages.Length; r++)
                        {
                            string[] Parts = ColumnImages[r].Split('@');
                            ColumnImages[r] = (r + 1).ToString("D6") + "@" + 
                                              Path.Combine(DirectoryPathInSpace, ResUnselectedClasses);
                        }
                        
                        TableOutUnselected.SetColumn("rlnReferenceImage", ColumnImages);
                    }
                    TableOutUnselected.Save(ResUnselectedModelStarFile);
                }

                // Process and save class statistics if available
                if (File.Exists(ResourceTemplates.VisClassStats))
                {
                    // Read all class statistics
                    Class2DModel[] AllModels = JsonSerializer.Deserialize<Class2DModel[]>(File.ReadAllText(ResourceTemplates.VisClassStats));
                    
                    // Extract statistics for selected and unselected classes
                    Class2DModel[] SelectedModels = SelectedClasses.Select(c => AllModels.FirstOrDefault(m => m.Id == c)).ToArray();
                    Class2DModel[] UnselectedModels = UnselectedClasses.Select(c => AllModels.FirstOrDefault(m => m.Id == c)).ToArray();

                    // Save statistics JSON files
                    File.WriteAllText(VisSelectedClassStats, JsonSerializer.Serialize(SelectedModels, new JsonSerializerOptions { WriteIndented = true }));
                    File.WriteAllText(VisUnselectedClassStats, JsonSerializer.Serialize(UnselectedModels, new JsonSerializerOptions { WriteIndented = true }));
                }
                
                logger.WriteLine("Done.");
                
                #endregion
                
                #region data.star - Split particles by class
                {
                    Star TableInOptics;
                    Star TableInParticles;
                    int[] ColumnClasses;

                    Star TableOutSelected;
                    Star TableOutUnselected;
                    
                    logger.WriteLine("Reading particle table... ");
                    {
                        // Read optics and particle tables from input STAR file
                        TableInOptics = new Star(ResourceParticles.ParticlesSingleStarPath, "optics");
                        TableInParticles = new Star(ResourceParticles.ParticlesSingleStarPath, "particles");
                        
                        // Extract class assignments for each particle
                        ColumnClasses = TableInParticles.GetColumn("rlnClassNumber").Select(int.Parse).ToArray();
                    }
                    logger.WriteLine("Done.");
                    
                    logger.WriteLine("Selecting particles... ");
                    {
                        // Find particles assigned to selected classes
                        int[] RowsSelected = ColumnClasses.Select((c, i) => (c, i))
                                                          .Where(t => SelectedClasses.Contains(t.Item1))
                                                          .Select(t => t.Item2)
                                                          .ToArray();
                        TableOutSelected = TableInParticles.CreateSubset(RowsSelected);
                        
                        // Find particles assigned to unselected classes
                        int[] RowsUnselected = ColumnClasses.Select((c, i) => (c, i))
                                                            .Where(t => UnselectedClasses.Contains(t.Item1))
                                                            .Select(t => t.Item2)
                                                            .ToArray();
                        TableOutUnselected = TableInParticles.CreateSubset(RowsUnselected);
                    }
                    logger.WriteLine("Done.");
                        
                    logger.WriteLine("Saving new particle table... ");
                    {
                        // Save selected particles STAR file
                        Star.SaveMultitable(ResSelectedDataStarFile, new()
                        {
                            { "optics", TableInOptics },
                            { "particles", TableOutSelected }
                        });
                        
                        // Save unselected particles STAR file
                        Star.SaveMultitable(ResUnselectedDataStarFile, new()
                        {
                            { "optics", TableInOptics },
                            { "particles", TableOutUnselected }
                        });
                    }
                    logger.WriteLine("Done.");
                }
                #endregion

                logger.WriteLine("Classes selected successfully.");
            }
            catch (Exception exc)
            {
                logger.WriteLine($"An error occurred: {exc.Message}");
                throw;
            }

            // Set the job as having one complete iteration
            VisAvailableIteration = 0;
        }
    }

    /// <summary>
    /// Tracks the log progress for this job
    /// </summary>
    /// <returns>An action to update the job state, or null if no update is needed</returns>
    /// <remarks>
    /// Since this is a single-step job that completes instantly, this method simply
    /// sets the log availability to iteration 0 to indicate the job is done.
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