using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.Components.FileBrowser;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Alignment.PeakAlign;

/// <summary>
/// Job that creates tilt series stacks and runs Etomo patch tracking to obtain tilt series alignments.
/// This is based on the WarpTools EtomoPatchTrackTiltseries command.
/// </summary>
[GenerateReadOnly]
public class PeakAlign : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "08b596fb-3b9f-4888-a9d7-4faba5cb045c";

    public override string TypeCategory => "Tilt-series.Alignment.Peak alignment";

    public override string TypeName => "Peak alignment";

    public override string TypeNameShort => "Peak alignment";

    public override string TypeDescription => "Correlates particles in each tilt with template and " +
                                              "calculates shift corrections based on average correlation " +
                                              "peak position.";

    protected override int DefaultMemoryPerWorker => 16;

    public override Type ExpandedViewType => null;

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => (NGpus * PerDevice) * 4;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInDataSetTs = "TiltSeries";
    public const string PortInParticlePositions = "ParticlePositions";
    public const string PortInTemplate = "Template";
    public const string PortOutDataSetTs = "TiltSeries";
    
    #region Parameters

    /// <summary>
    /// Rescale tilt images to this pixel size; normally 10–15 for cryo data
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Pre-processing", 0)]
    [UiDecimal("corr_angpix", "Pixel size", min: 1, max: 100000, stepSize: 0.1, unit: "Å",
               "Rescale tilt images to this pixel size")]
    public decimal AngPix { get; set; } = 10;

    [RelayProperty]
    [UiInt("template_diameter", "Particle diameter", min: 10, max: Int32.MaxValue, stepSize: 10,
           unit: "Å",
           "Size of the patches the images will be divided into for processing")]
    public int TemplateDiameter { get; set; } = 100;
    
    [UiFieldGroup("Alignment", 1)]
    [UiBool("optimize_poses", "Optimize particle poses",
           helpText: "Refine particle orientations and shifts after initial alignment")]
    public bool OptimizePoses { get; set; } = true;
    
    #endregion

    public PeakAlign()
    {
        var portInDataSetTs = new PortIn(this, typeof(TiltSeriesSet), PortInDataSetTs, "Aligned tilt-series", 1, 1);
        var portInParticlePositions = new PortIn(this, typeof(ParticleSet), PortInParticlePositions, "Particle positions", 1, 1);
        var portInTemplate = new PortIn(this, typeof(MapList), PortInTemplate, "Template volume", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInDataSetTs] = portInDataSetTs,
            [PortInParticlePositions] = portInParticlePositions,
            [PortInTemplate] = portInTemplate
        });

        var portOutDataSetTs = new PortOut(this, typeof(TiltSeriesSet), PortOutDataSetTs, "Aligned tilt-series", GetTiltSeriesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutDataSetTs] = portOutDataSetTs
        });
    }

    private TiltSeriesSet GetTiltSeriesResource(int iter)
    {
        
        if (!PortsIn[PortInDataSetTs].IsConnected)
            return null;

        var resource = PortsIn[PortInDataSetTs].GetSingleResource<TiltSeriesSet>();

        if (resource == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        resource.DataSet.SettingsPath = Path.Combine(DirectoryPath, "processing.settings");

        resource.HasMetadata = true;
        resource.LatestMetadataDirectory = DirectoryPath;

        resource.ProcessedItemsJson = ResProcessedItemsJson;
        resource.FailedItemsJson = ResFailedItemsJson;

        return resource;
    }

    /// <summary>
    /// Gets the name of the template based on input settings
    /// </summary>
    private string GetTemplateName()
    {
        if (PortsIn[PortInTemplate].IsConnected)
        {
            var mapList = PortsIn[PortInTemplate].GetSingleResource<MapList>();
            if (mapList != null && mapList.Maps.Any())
            {
                string templatePath = mapList.Maps.First().AverageVolumePath;
                string baseName = Path.GetFileNameWithoutExtension(templatePath);
                
                return baseName;
            }
        }
        else
            throw new Exception("Template input not connected.");

        return "template";
    }

    /// <summary>
    /// Gets the name of the Warp command used for tilt series import.
    /// </summary>
    public override string CommandName => "WarpTools ts_peak_align";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));
        
        var particleSet = PortsIn[PortInParticlePositions].GetSingleResource<ParticleSet>();
        
        if (!string.IsNullOrEmpty(particleSet.ParticlesMultiStarDirectory))
        {
            result["input_directory"] = Space.GetRelativePath(particleSet.ParticlesMultiStarDirectory);
            result["input_pattern"] = "*.star";
        }
        else
        {
            result["input_star"] = particleSet.ParticlesSingleStarPath;
        }

        if (particleSet.HasNormalizedCoords)
            result["normalized_coords"] = "";
        else
            result["coords_angpix"] = particleSet.CoordPixelSize.ToString("F4", CultureInfo.InvariantCulture);

        if (PortsIn[PortInTemplate].GetSingleResource<MapList>() != null)
        {
            var map = PortsIn[PortInTemplate].GetSingleResource<MapList>().Maps.First();
            result["template_path"] = map.AverageVolumePath;
        }
        else
            throw new Exception("Template input not connected.");

        return result;
    }

    public override void Stage()
    {
        base.Stage();

        var tiltSeriesSet = PortsIn[PortInDataSetTs].GetSingleResource<TiltSeriesSet>();

        if (tiltSeriesSet == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        if (!tiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt-series input must have metadata.");

        Directory.CreateDirectory(DirectoryPath);
        
        foreach (var file in Directory.EnumerateFiles(tiltSeriesSet.LatestMetadataDirectory, "*.xml"))
            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), true);

        var optionsWarp = tiltSeriesSet.DataSet.ToOptionsWarp();
        optionsWarp.Import.ProcessingFolder = DirectoryPath;

        optionsWarp.Save(Path.Combine(DirectoryPath, "processing.settings"));
    }
}