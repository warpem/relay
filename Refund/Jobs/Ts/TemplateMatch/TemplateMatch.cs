using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.TemplateMatch;

/// <summary>
/// Job that performs template matching on reconstructed tomograms.
/// This is based on the WarpTools TemplateMatchTiltseries command.
/// </summary>
[GenerateReadOnly]
public class TemplateMatch : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "b44eae53-19e2-4c67-bd59-b9c7b3f52f2e";
    
    public override string TypeCategory => "Tilt-series.TemplateMatch";

    public override string TypeName => "Template matching";

    public override string TypeNameShort => "Template Match";

    public override string TypeDescription => "Match previously reconstructed tomograms against a 3D template, producing a list of the highest-scoring matches";

    protected override int DefaultMemoryPerWorker => 48;

    public override Type ExpandedViewType => typeof(TemplateMatchExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => (NGpus * PerDevice) * 4;

    public override bool CanBeFinalized => true;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTomogramSet = "Tomograms";
    public const string PortInMapList = "Template";
    public const string PortOutTomogramSet = "Tomograms";
    public const string PortOutParticleSet = "Positions";
    
    #region Parameters

    /// <summary>
    /// EMDB entry number
    /// </summary>
    [RelayProperty]
    [UiEmdbEntry("template_emdb", "EMDB entry",
                 helpText: "Download the EMDB entry with this ID and use its main map")]
    public int? TemplateEmdb { get; set; } = null;

    /// <summary>
    /// Template diameter in Angstrom
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Template Parameters", 2)]
    [UiDecimal("template_diameter", "Template diameter", min: 10, max: 1000, stepSize: 10, unit: "Å",
               helpText: "Diameter of the template in Angstrom")]
    public decimal TemplateDiameter { get; set; } = 100.0M;

    /// <summary>
    /// Pixel size of the template in Angstrom; leave empty to use value from map header
    /// </summary>
    [RelayProperty]
    [UiDecimalNullable("template_angpix", "Template pixel size", min: 0.1, max: 10, stepSize: 0.1, unit: "Å", isAdvanced: true,
                       helpText: "Pixel size of the template in Angstrom; leave empty to use value from map header")]
    public decimal? TemplateAngPix { get; set; } = null;

    /// <summary>
    /// Mirror the template along the X axis to flip the handedness
    /// </summary>
    [RelayProperty]
    [UiBool("template_flip", "Flip template", 
            "Mirror the template along the X axis to flip the handedness")]
    public bool TemplateFlip { get; set; } = false;

    /// <summary>
    /// Symmetry of the template, e.g. C1, D7, O
    /// </summary>
    [RelayProperty]
    [UiSymmetry("symmetry", "Template symmetry", 
                "Symmetry of the template, e.g. C1, D7, O")]
    public string TemplateSymmetry { get; set; } = "C1";

    /// <summary>
    /// Number of subdivisions defining the angular search step
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Matching Parameters", 3)]
    [UiHealpix("subdivisions", "Angular sampling order", 
               helpText: "Number of subdivisions defining the angular search step: 2 = 15° step, 3 = 7.5°, 4 = 3.75° and so on")]
    public int HealpixOrder { get; set; } = 3;
    
    [RelayProperty]
    [UiBool("optimize_poses", "Refine poses", 
               helpText: "Refine poses with sub-pixel precision after initial cross-correlation with the volume")]
    public bool OptimizePoses { get; set; } = false;

    [RelayProperty]
    [UiDecimalNullable("optimize_poses_angpix", "Pixel size for pose refinement",
                       min: 1.0, max: 99999.0, stepSize: 0.1, unit: "Å",
                       helpText: "Minimum pixel size to use at the last iteration of pose optimization. " +
                                 "Leave empty to use the tomogram pixel size.",
                       ConditionalOnField = nameof(OptimizePoses),
                       ConditionalOnValue = true)]
    public decimal? OptimizePosesAngpix { get; set; } = null;

    [RelayProperty]
    [UiInt("optimize_poses_steps", "Number of annealing steps",
                       min: 1, max: 99999, stepSize: 1,
                       helpText: "To avoid getting stuck in local minima, anneal the pixel size used for pose refinement " +
                                 "from the tomogram pixel size to the pose refinement pixel size over this many iterations.",
                       ConditionalOnField = nameof(OptimizePoses),
                       ConditionalOnValue = true)]
    public int OptimizePosesSteps { get; set; } = 1;

    /// <summary>
    /// Limit the range of angles between the reference's Z axis and the tomogram's XY plane to plus/minus this value, in degrees
    /// </summary>
    [RelayProperty]
    [UiDecimalNullable("tilt_range", "Tilt range limit", min: 0, max: 90, stepSize: 5, unit: "°",
                      helpText: "Limit the range of angles between the reference's Z axis and the tomogram's XY plane to plus/minus this value, in °; " +
                                "useful for matching filaments lying mostly flat in the XY plane")]
    public decimal? TiltRange { get; set; } = null;

    /// <summary>
    /// How many orientations to evaluate at once
    /// </summary>
    [RelayProperty]
    [UiInt("batch_angles", "Batch size", min: 1, max: 64, stepSize: 1, isAdvanced: true,
           helpText: "How many orientations to evaluate at once; memory consumption scales linearly with this; higher than 32 probably won't lead to speed-ups")]
    public int BatchAngles { get; set; } = 4;

    /// <summary>
    /// Minimum distance in Angstrom between peaks; leave empty to use template diameter
    /// </summary>
    [RelayProperty]
    [UiIntNullable("peak_distance", "Peak distance", min: 10, max: 1000, stepSize: 10, unit: "Å", 
                   helpText: "Minimum distance in Angstrom between peaks; leave empty to use template diameter")]
    public int? PeakDistance { get; set; } = null;

    /// <summary>
    /// Maximum number of peak positions to save
    /// </summary>
    [RelayProperty]
    [UiInt("npeaks", "Maximum peaks", min: 10, max: 10000, stepSize: 100,
           helpText: "Maximum number of peak positions to save")]
    public int PeakNumber { get; set; } = 2000;
    
    [RelayProperty]
    [UiIntNullable("tophat", "Tophat peak filter", min: 1, max: 3, stepSize: 1,
           helpText: "Filter peaks by sharpness using a tophat transform with a kernel of this connectivity order; " +
                     "higher values = less aggressive filtering; leave empty to disable. " +
                     "For a description of the method, see Chaillet et al. 2025, J Struct Biol")]
    public int? Tophat { get; set; } = null;

    /// <summary>
    /// Don't set score distribution to median = 0, stddev = 1
    /// </summary>
    [RelayProperty]
    [UiBool("dont_normalize", "Don't normalize scores", 
            "Don't set score distribution to median = 0, stddev = 1")]
    public bool DontNormalizeScores { get; set; } = false;

    /// <summary>
    /// Perform spectral whitening to give higher-resolution information more weight
    /// </summary>
    [RelayProperty]
    [UiBool("whiten", "Whiten spectra", 
            "Perform spectral whitening to give higher-resolution information more weight; this can help when the alignments are already good and you need more selective matching")]
    public bool Whiten { get; set; } = false;

    /// <summary>
    /// Gaussian low-pass filter to be applied to template and tomogram, in fractions of Nyquist; 1.0 = no low-pass, <1.0 = low-pass
    /// </summary>
    [RelayProperty]
    [UiDecimal("lowpass", "Low-pass filter", min: 0.01, max: 1.0, stepSize: 0.01, 
               helpText: "Gaussian low-pass filter to be applied to template and tomogram, in fractions of Nyquist; 1.0 = no low-pass, <1.0 = low-pass")]
    public decimal Lowpass { get; set; } = 1.0M;

    /// <summary>
    /// Sigma (i.e. fall-off) of the Gaussian low-pass filter, in fractions of Nyquist; larger value = slower fall-off
    /// </summary>
    [RelayProperty]
    [UiDecimal("lowpass_sigma", "Low-pass sigma", min: 0.01, max: 0.5, stepSize: 0.01,
               helpText: "Sigma (i.e. fall-off) of the Gaussian low-pass filter, in fractions of Nyquist; larger value = slower fall-off")]
    public decimal LowpassSigma { get; set; } = 0.1M;

    /// <summary>
    /// Matching is performed locally using sub-volumes of this size in pixel
    /// </summary>
    [RelayProperty]
    [UiInt("subvolume_size", "Subvolume size", min: 64, max: 512, stepSize: 64, unit: "px",
           helpText: "Matching is performed locally using sub-volumes of this size in pixel")]
    public int SubVolumeSize { get; set; } = 192;

    /// <summary>
    /// Dismiss positions not covered by at least this many tilts; set to -1 to disable position culling
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Advanced Options", 4)]
    [UiIntNullable("max_missing_tilts", "Maximum missing tilts", min: 1, max: 10, stepSize: 1,
           helpText: "Dismiss positions not covered by at least this many tilts; clear to disable position culling")]
    public int? MaxMissingTilts { get; set; } = 2;

    // /// <summary>
    // /// Reuse correlation volumes from a previous run if available, only extract peak positions
    // /// </summary>
    // [RelayProperty]
    // [UiBool("reuse_results", "Reuse previous results", 
    //         "Reuse correlation volumes from a previous run if available, only extract peak positions")]
    // public bool ReuseResults { get; set; } = false;

    /// <summary>
    /// Number of tomograms to use for handedness checking
    /// </summary>
    [RelayProperty]
    [UiIntNullable("check_hand", "Number of tomograms to check", min: 1, max: 20, stepSize: 1,
           helpText: "Number of tomograms to use for handedness checking; clear to disable")]
    public int? CheckHandN { get; set; } = null;

    public override int PerDevice { get; set; } = 1;

    #endregion

    /// <summary>
    /// Constructor
    /// </summary>
    public TemplateMatch()
    {
        var portInTomogramSet = new PortIn(this, typeof(TomogramSet), PortInTomogramSet, "Tomograms", 1, 1);
        var portInMapList = new PortIn(this, typeof(MapList), PortInMapList, "Template", 0, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTomogramSet] = portInTomogramSet,
            [PortInMapList] = portInMapList
        });

        var portOutTomogramSet = new PortOut(this, typeof(TomogramSet), PortOutTomogramSet, "Tomograms", GetTomogramSetResource);
        var portOutPositions = new PortOut(this, typeof(ParticleSet), PortOutParticleSet, "Particle positions", GetParticleSetResource);
        

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTomogramSet] = portOutTomogramSet,
            [PortOutParticleSet] = portOutPositions
        });
    }
    
    private TomogramSet GetTomogramSetResource(int iter)
    {
        if (!PortsIn[PortInTomogramSet].IsConnected)
            return null;

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();

        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");

        // Ensure the TiltSeries has metadata
        if (!tomogramSet.TiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt series must have metadata.");
        
        tomogramSet.TomogramCorrVolumeDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionDirName);
        tomogramSet.ToTomogramCorrVolumePath = path => WarpHelper.PathCombine(DirectoryPath,
                                                                              TiltSeries.ReconstructionDirName,
                                                                              $"{WarpHelper.PathToName(path)}_corr.mrc");

        return tomogramSet;
    }

    /// <summary>
    /// Resource generator for the output PositionSet
    /// </summary>
    private ParticleSet GetParticleSetResource(int iter)
    {
        if (!PortsIn[PortInTomogramSet].IsConnected)
            return null;

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();

        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");

        // Get template name for pattern matching
        string templateName = GetTemplateName();

        // Format for matching output STAR files
        var result = new ParticleSet
        {
            ParticlesMultiStarDirectory = Path.Combine(DirectoryPath, TiltSeries.MatchingDirName),
            ToMultiStarPath = path => Path.Combine(DirectoryPath,
                                                   TiltSeries.MatchingDirName,
                                                   $"{WarpHelper.PathToName(tomogramSet.ToTomogramPath(path))}_{templateName}.star"),
            Has3dCoords = true,
            HasAngles = true,
            HasPositions = true,
            HasNormalizedCoords = true,
            PickedInTomograms = tomogramSet,
            Diameter = (int)TemplateDiameter
        };

        // Hook up template map if available
        Map templateMap = null;
        if (PortsIn[PortInMapList].IsConnected)
        {
            var mapList = PortsIn[PortInMapList].GetSingleResource<MapList>();
            if (mapList != null && mapList.Maps.Any())
                templateMap = mapList.Maps.First();
        }
        else if (TemplateEmdb is > 0)
        {
            templateMap = new Map(half1VolumePath: null, 
                                  half2VolumePath: null, 
                                  averageVolumePath:Path.Combine(DirectoryPath, 
                                                                 "template", 
                                                                 $"{GetTemplateName()}.mrc"));
        }
        
        if (templateMap != null)
            result.CorrespondingMaps = new MapList([templateMap]);

        return result;
    }

    /// <summary>
    /// Gets the name of the template based on input settings
    /// </summary>
    private string GetTemplateName()
    {
        if (PortsIn[PortInMapList].IsConnected)
        {
            var mapList = PortsIn[PortInMapList].GetSingleResource<MapList>();
            if (mapList != null && mapList.Maps.Any())
            {
                string templatePath = mapList.Maps.First().AverageVolumePath;
                string baseName = Path.GetFileNameWithoutExtension(templatePath);
                
                // If flipping is enabled, append _flipx
                if (TemplateFlip)
                    return $"{baseName}_flipx";
                
                return baseName;
            }
        }
        else if (TemplateEmdb is > 0)
        {
            string emdbId = TemplateEmdb.Value.ToString("D4");
            
            // If flipping is enabled, append _flipx
            if (TemplateFlip)
                return $"emd_{emdbId}_flipx";
                
            return $"emd_{emdbId}";
        }

        return "template";
    }

    /// <summary>
    /// Gets the name of the Warp command used for template matching in tomograms.
    /// </summary>
    public override string CommandName => "WarpTools ts_template_match";

    /// <summary>
    /// Composes command line arguments for the template matching command.
    /// </summary>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();
        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");
        
        var result = base.ComposeCommandArguments();

        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));
        result["tomo_angpix"] = tomogramSet.PixelSize.ToString(CultureInfo.InvariantCulture);

        if (PortsIn[PortInMapList].GetSingleResource<MapList>() != null && TemplateEmdb == null)
        {
            var map = PortsIn[PortInMapList].GetSingleResource<MapList>().Maps.First();
            result["template_path"] = map.AverageVolumePath;
        }

        return result;
    }

    /// <summary>
    /// Prepares the job for execution.
    /// </summary>
    public override void Stage()
    {
        base.Stage();

        var tomogramSet = PortsIn[PortInTomogramSet].GetSingleResource<TomogramSet>();

        if (tomogramSet == null)
            throw new InvalidOperationException("Tomogram set input not found.");
        
        // Ensure underlying tilt series has metadata
        if (!tomogramSet.TiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt series must have metadata.");
        
        if (!PortsIn[PortInMapList].IsConnected && TemplateEmdb == null)
            throw new InvalidOperationException("Either a template map or an EMDB entry ID must be provided.");

        Directory.CreateDirectory(DirectoryPath);
        
        // Copy XML metadata files
        foreach (var file in Directory.EnumerateFiles(tomogramSet.TiltSeriesSet.LatestMetadataDirectory, "*.xml"))
            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), true);
        
        // Create symbolic links to all tomograms
        string sourceTomogramDir = tomogramSet.TomogramDirectory;
        string targetTomogramDir = Path.Combine(DirectoryPath, TiltSeries.ReconstructionDirName);
        
        try
        {
            Directory.CreateDirectory(targetTomogramDir);
            foreach (var sourceFile in Directory.EnumerateFiles(sourceTomogramDir, "*.*"))
            {
                string targetFile = Path.Combine(targetTomogramDir, Path.GetFileName(sourceFile));
                if (!File.Exists(targetFile))
                    File.CreateSymbolicLink(targetFile, sourceFile);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create symbolic links to tomograms: {ex.Message}");
        }
        
        // Create and save WarpTools options
        var optionsWarp = tomogramSet.TiltSeriesSet.DataSet.ToOptionsWarp();
        optionsWarp.Import.ProcessingFolder = DirectoryPath;
        
        optionsWarp.Save(Path.Combine(DirectoryPath, "processing.settings"));
    }
    
    public override Action TrackProgressResults()
    {
        var baseUpdate = base.TrackProgressResults();
        
        if (VisAvailableIteration < 0 && !File.Exists(VisCard(0)) && NItemsProcessed > 0)
        {
            var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(File.ReadAllText(ResProcessedItemsJson));

            if (processedItems.Count == 0)
                return null;

            ParticleSet particleSet = GetParticleSetResource(0);
            TomogramSet tomogramSet = GetTomogramSetResource(0);

            string templatePath = null;
            if (PortsIn[PortInMapList].IsConnected)
            {
                MapList mapList = PortsIn[PortInMapList].GetSingleResource<MapList>();
                templatePath = mapList.Maps.First().AverageVolumePath;
            }
            else if (TemplateEmdb is > 0)
            {
                templatePath = Path.Combine(DirectoryPath, "template", $"{GetTemplateName()}.mrc");
            }

            BakeryWrapper.TsTemplateMatchJobCard(tomogramSet.ToTomogramPath(processedItems[0].Path),
                                                 particleSet.ToMultiStarPath(processedItems[0].Path),
                                                 templatePath,
                                                 (float)TemplateDiameter,
                                                 VisCard(0));
            
            return () =>
            {
                baseUpdate?.Invoke();
                VisAvailableIteration = 0;
            };
        }

        return baseUpdate;
    }
}