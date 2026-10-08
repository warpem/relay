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

namespace Refund.Jobs.Ts.Picking.TemplateMatch;

/// <summary>
/// Job that proposes template matches in a coarse volume and refines them against tilt images.
/// This is based on the WarpTools TemplateMatchTiltseries command.
/// </summary>
[GenerateReadOnly]
public class TemplateMatch : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "b44eae53-19e2-4c67-bd59-b9c7b3f52f2e";
    
    public override string TypeCategory => "Tilt-series.Picking.Template matching";

    public override string TypeName => "Template matching";

    public override string TypeNameShort => "Template Match";

    public override string TypeDescription => "Match a 3D template against tilt images using coarse matched-filter proposals and multistart pose refinement";

    protected override int DefaultMemoryPerWorker => 48;

    public override Type ExpandedViewType => typeof(TemplateMatchExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => IsPooled ? base.CoreCount : (NGpus * PerDevice) * 4;

    public override bool CanBeFinalized => true;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTomogramSet = "Tomograms";
    public const string PortInMapList = "Template";
    public const string PortOutTomogramSet = "Tomograms";
    public const string PortOutParticleSet = "Positions";

    [RelayProperty]
    [Clearable]
    public string VisTemplateName { get; set; }
    
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
    
    // Retained for deserializing existing jobs; refinement is now always enabled.
    [RelayProperty]
    public bool OptimizePoses { get; set; } = false;

    [RelayProperty]
    public int OptimizePosesSteps { get; set; } = 1;

    [RelayProperty]
    [UiDecimalNullable("optimize_poses_angpix", "Pixel size for pose refinement",
                       min: 0.1, max: 99999.0, stepSize: 0.1, unit: "Å",
                       helpText: "Minimum pixel size for pose refinement, no larger than the coarse-search pixel size. Leave empty to use the coarse-search pixel size.")]
    public decimal? OptimizePosesAngpix { get; set; } = null;

    [RelayProperty]
    [UiInt("match_topk", "Orientations per voxel", min: 1, max: 128, isAdvanced: true,
           helpText: "Retain this many orientation scores per voxel. Leaderboard GPU memory is 8 × K bytes per padded voxel.")]
    public int MatchTopK { get; set; } = 8;

    [RelayProperty]
    [UiInt("refine_starts", "Refinement starts", min: 1, max: 1024,
           helpText: "Maximum GPU pose hypotheses per peak, pooled from that voxel and its six neighbors")]
    public int RefineStarts { get; set; } = 32;

    [RelayProperty]
    [UiBool("refine_fit_bfactor", "Fit amplitude and B-factor", isAdvanced: true,
            helpText: "Jointly fit amplitude and B-factor at each final pose and write envelope diagnostics without changing particle scores or selection")]
    public bool RefineFitBfactor { get; set; } = false;

    [RelayProperty]
    [UiDecimal("refine_fit_highpass", "Envelope fitting high-pass", min: 0, max: 10000, stepSize: 1, unit: "Å", isAdvanced: true,
               helpText: "Use frequencies above 1/value for amplitude/B-factor fitting; 0 uses the full refinement band. Must be coarser than the final refinement resolution.",
               ConditionalOnField = nameof(RefineFitBfactor), ConditionalOnValue = true)]
    public decimal RefineFitHighpass { get; set; } = 30;

    [RelayProperty]
    [UiBool("refine_export_tilt_spectra", "Export per-tilt spectra", isAdvanced: true,
            helpText: "Export per-tilt sufficient statistics for experimental shared tilt-scale calibration",
            ConditionalOnField = nameof(RefineFitBfactor), ConditionalOnValue = true)]
    public bool RefineExportTiltSpectra { get; set; } = false;

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
    /// Minimum distance in Angstrom between peaks; leave empty to use half the template diameter
    /// </summary>
    [RelayProperty]
    [UiIntNullable("peak_distance", "Peak distance", min: 10, max: 1000, stepSize: 10, unit: "Å", 
                   helpText: "Minimum distance in Angstrom between peaks; leave empty to use half the template diameter")]
    public int? PeakDistance { get; set; } = null;

    /// <summary>
    /// Maximum number of coarse candidate positions to refine
    /// </summary>
    [RelayProperty]
    [UiInt("npeaks", "Maximum coarse candidates", min: 1, max: 100000, stepSize: 100,
           helpText: "Maximum coarse candidate positions; all are refined before final spatial suppression")]
    public int PeakNumber { get; set; } = 8000;
    
    [RelayProperty]
    [UiIntNullable("tophat", "Tophat peak filter", min: 1, max: 3, stepSize: 1,
           helpText: "Filter peaks by sharpness using a tophat transform with a kernel of this connectivity order; " +
                     "higher values = less aggressive filtering; leave empty to disable. " +
                     "For a description of the method, see Chaillet et al. 2025, J Struct Biol")]
    public int? Tophat { get; set; } = null;

    // Legacy parameters are kept in saved state but no longer emitted to WarpTools.
    [RelayProperty]
    public bool DontNormalizeScores { get; set; } = false;

    [RelayProperty]
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

    [RelayProperty]
    public int SubVolumeSize { get; set; } = 192;

    [RelayProperty]
    [UiFieldGroup("Advanced Options", 4)]
    [UiIntNullable("max_missing_tilts", "Maximum missing tilts", min: 0, max: 100, stepSize: 1,
           helpText: "Optional coarse coverage restriction; clear to leave visibility handling to per-candidate refinement")]
    public int? MaxMissingTilts { get; set; } = null;

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

    public override int PerDevice { get => 1; set { } }

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
        
        tomogramSet.TomogramCorrVolumeDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.MatchingDirName);
        tomogramSet.ToTomogramCorrVolumePath = path => GetEffectiveTemplateFlip() is bool flip
            ? WarpHelper.PathCombine(DirectoryPath, TiltSeries.MatchingDirName,
                                    $"{TiltSeries.ToTomogramWithPixelSize(path, tomogramSet.PixelSize)}_{GetTemplateName(flip)}_corr.mrc")
            : null;

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

        // A handedness trial is not a result selection. Both sets of trial files
        // exist, so use Warp's explicit decision rather than guessing from filenames.
        bool? templateFlip = GetEffectiveTemplateFlip();
        if (!templateFlip.HasValue)
            return null;
        string templateName = GetTemplateName(templateFlip.Value);

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
        if (templateFlip.Value || TemplateEmdb is > 0)
        {
            templateMap = new Map(averageVolumePath: Path.Combine(DirectoryPath, "template", $"{templateName}.mrc"));
        }
        else if (PortsIn[PortInMapList].IsConnected)
        {
            var mapList = PortsIn[PortInMapList].GetSingleResource<MapList>();
            if (mapList != null && mapList.Maps.Any())
                templateMap = mapList.Maps.First();
        }
        
        if (templateMap != null)
            result.CorrespondingMaps = new MapList([templateMap]);

        return result;
    }

    /// <summary>
    /// Resolves the selected hand without changing the submitted parameters.
    /// </summary>
    private bool? GetEffectiveTemplateFlip()
    {
        if (CheckHandN is not > 0)
            return TemplateFlip;
        if (IsBlueprint)
            return null;

        // These decision lines are emitted by WarpTools after comparing both hands,
        // before the full run. Reading them also supports already-completed jobs.
        if (!File.Exists(PathStdOut))
            return null;
        bool? selected = null;
        foreach (string line in File.ReadLines(PathStdOut))
        {
            switch (line.Trim())
            {
                case "Flipped template has higher peak values, using it for further processing":
                    selected = true;
                    break;
                case "Original template has higher peak values, using it for further processing":
                    selected = false;
                    break;
            }
        }
        return selected;
    }

    private string GetTemplateName(bool templateFlip)
    {
        if (PortsIn[PortInMapList].IsConnected)
        {
            var mapList = PortsIn[PortInMapList].GetSingleResource<MapList>();
            if (mapList != null && mapList.Maps.Any())
            {
                string templatePath = mapList.Maps.First().AverageVolumePath;
                string baseName = Path.GetFileNameWithoutExtension(templatePath);
                
                // If flipping is enabled, append _flipx
                if (templateFlip)
                    return $"{baseName}_flipx";
                
                return baseName;
            }
        }
        else if (TemplateEmdb is > 0)
        {
            string emdbId = TemplateEmdb.Value.ToString("D4");
            
            // If flipping is enabled, append _flipx
            if (templateFlip)
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
        // WarpTools expects an integer diameter, even when saved decimal state has a scale.
        result["template_diameter"] = ((int)TemplateDiameter).ToString(CultureInfo.InvariantCulture);

        if (PortsIn[PortInMapList].GetSingleResource<MapList>() != null && TemplateEmdb == null)
        {
            var map = PortsIn[PortInMapList].GetSingleResource<MapList>().Maps.First();
            result["template_path"] = map.AverageVolumePath;
        }

        // A missing nullable UI value would otherwise leave WarpTools' default implicit.
        result["max_missing_tilts"] = (MaxMissingTilts ?? -1).ToString(CultureInfo.InvariantCulture);

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
    
    public override Action TrackProgressResults() => TrackProgressResults(BakeryWrapper.TsTemplateMatchJobCard);

    internal Action TrackProgressResults(Action<string, string, string, float, string> renderCard)
    {
        var baseUpdate = base.TrackProgressResults();

        ParticleSet particleSet = GetParticleSetResource(0);
        string templatePath = particleSet?.CorrespondingMaps?.Maps.FirstOrDefault()?.GetAverageOrSimilar();
        if (templatePath == null)
            return baseUpdate;
        string templateName = Path.GetFileNameWithoutExtension(templatePath);

        if (VisAvailableIteration >= 0 && VisTemplateName == templateName && File.Exists(VisCard(0)))
            return baseUpdate;
        if (!File.Exists(ResProcessedItemsJson))
            return baseUpdate;

        var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(File.ReadAllText(ResProcessedItemsJson));
        if (processedItems is not { Count: > 0 })
            return baseUpdate;

        TomogramSet tomogramSet = GetTomogramSetResource(0);
        string tomogramPath = tomogramSet.ToTomogramPath(processedItems[0].Path);
        string particlePath = particleSet.ToMultiStarPath(processedItems[0].Path);
        if (!File.Exists(tomogramPath) || !File.Exists(particlePath) || !File.Exists(templatePath))
            return baseUpdate;

        Directory.CreateDirectory(RelayResultsDirectoryPath);
        renderCard(tomogramPath, particlePath, templatePath, (float)TemplateDiameter, VisCard(0));
        return () =>
        {
            baseUpdate?.Invoke();
            VisAvailableIteration = 0;
            VisTemplateName = templateName;
        };
    }
}
