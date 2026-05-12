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

namespace Refund.Jobs.Ts.Reconstruction.ReconstructMap;

/// <summary>
/// Job that creates tilt series stacks and runs Etomo patch tracking to obtain tilt series alignments.
/// This is based on the WarpTools EtomoPatchTrackTiltseries command.
/// </summary>
[GenerateReadOnly]
public class ReconstructMap : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "b09a6298-d84b-46f4-b1aa-4547b71bb715";

    public override string TypeCategory => "Tilt-series.Reconstruction.Map";

    public override string TypeName => "Reconstruct map";

    public override string TypeNameShort => "Reconstruct map";

    public override string TypeDescription => "Reconstructs a 3D map given particle positions and angles in a set of tilt series.";

    protected override int DefaultMemoryPerWorker => 16;

    public override Type ExpandedViewType => typeof(ReconstructMapExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(3, 1);

    public override int CoreCount => (NGpus * PerDevice) * 4;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInDataSetTs = "TiltSeries";
    public const string PortInParticlePositions = "ParticlePositions";
    public const string PortOutReconstruction = "Reconstruction";
    
    private const string MapName = "reconstruction";
    public string ResReconstructionAverage => Path.Combine(DirectoryPath, $"{MapName}.mrc");
    public string ResReconstructionHalf1 => Path.Combine(DirectoryPath, $"{MapName}_half1.mrc");
    public string ResReconstructionHalf2 => Path.Combine(DirectoryPath, $"{MapName}_half2.mrc");
    
    public string VisMap3d => DoHalfmaps ? ResReconstructionHalf1 : ResReconstructionAverage;
    
    #region Parameters

    /// <summary>
    /// Rescale tilt images to this pixel size; normally 10–15 for cryo data
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Reconstruction", 0)]
    [UiDecimal("rec_angpix", "Pixel size", min: 1, max: 100000, stepSize: 0.0001, unit: "Å",
               "Rescale data to this pixel size")]
    public decimal AngPix { get; set; } = 2;

    [RelayProperty]
    [UiInt("boxsize", "Box size", min: 10, max: Int32.MaxValue, stepSize: 2,
           unit: "px",
           "Size of the reconstruction box in pixels")]
    public int BoxSize { get; set; } = 128;

    [RelayProperty]
    [UiSymmetry("symmetry", "Symmetry",
               "Point-group symmetry to apply during reconstruction")]
    public string Symmetry { get; set; } = "C1";

    [RelayProperty]
    [UiBool(null, "Reconstruct half-maps",
                  "Reconstruct half-maps for resolution assessment. Otherwise, all particles will go into a single map." +
                  "If rlnRandomSubset is unavailable in the metadata, particles will be randomly split into two halves.")]
    public bool DoHalfmaps { get; set; } = true;
    
    #endregion

    public ReconstructMap()
    {
        var portInDataSetTs = new PortIn(this, typeof(TiltSeriesSet), PortInDataSetTs, "Aligned tilt-series", 1, 1);
        var portInParticlePositions = new PortIn(this, typeof(ParticleSet), PortInParticlePositions, "Particle positions", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInDataSetTs] = portInDataSetTs,
            [PortInParticlePositions] = portInParticlePositions
        });

        var portOutReconstruction = new PortOut(this, typeof(MapList), PortOutReconstruction, "Reconstruction", GetTiltSeriesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutReconstruction] = portOutReconstruction
        });
    }

    private MapList GetTiltSeriesResource(int iter)
    {
        Map result = new Map(half1VolumePath: DoHalfmaps ? ResReconstructionHalf1 : null,
                             half2VolumePath: DoHalfmaps ? ResReconstructionHalf2 : null,
                             averageVolumePath: !DoHalfmaps ? ResReconstructionAverage : null);

        return new MapList([result]);
    }

    /// <summary>
    /// Gets the name of the Warp command used for tilt series import.
    /// </summary>
    public override string CommandName => "WarpTools ts_reconstruct_average";

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

        if (DoHalfmaps)
            result["force_split"] = "";
        else
            result["ignore_split"] = "";
        
        result["output"] = MapName;

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
    public override Action TrackProgressResults()
    {
        var result = base.TrackProgressResults();
        bool changed = false;

        if (LogsAvailableIteration >= 0 && 
            !File.Exists(VisCard(0)))
        {
            if (DoHalfmaps)
            {
                if (File.Exists(ResReconstructionHalf1))
                {
                    BakeryWrapper.MapOrthosliceAtlas(ResReconstructionHalf1, 1, VisCard(0));
                    changed = true;
                }
            }
            else
            {
                if (File.Exists(ResReconstructionAverage))
                {
                    BakeryWrapper.MapOrthosliceAtlas(ResReconstructionAverage, 1, VisCard(0));
                    changed = true;
                }
            }
        }
        
        if (changed)
            return () =>
            {
                result?.Invoke();
                VisAvailableIteration = 0;
            };
        
        return result;
    }
}