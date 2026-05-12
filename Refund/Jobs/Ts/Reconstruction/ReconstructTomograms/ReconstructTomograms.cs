using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.Reconstruction.ReconstructTomograms;

/// <summary>
/// Job that reconstructs tomograms from tilt series.
/// This is based on the WarpTools ReconstructTiltseries command.
/// </summary>
[GenerateReadOnly]
public class ReconstructTomograms : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "24b1cc22-2a52-477a-b4c7-19fac71f669f";
    
    public override string TypeCategory => "Tilt-series.Reconstruction.Reconstruct";

    public override string TypeName => "Reconstruct tomograms";

    public override string TypeNameShort => "Reconstruct";

    public override string TypeDescription => "Reconstructs tomograms from tilt series with various options for deconvolution and half-map generation";

    public override Type ExpandedViewType => typeof(ReconstructTomogramsExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    public override int CoreCount => (NGpus * PerDevice) * 4;

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTiltSeriesSet = "TiltSeries";
    public const string PortOutTomogramSet = "Tomograms";
    
    #region Parameters

    /// <summary>
    /// Pixel size of the reconstructed tomograms in Angstrom
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Reconstruction Parameters", 0)]
    [UiDecimal("angpix", "Pixel size", min: 1, max: 100, stepSize: 0.1, unit: "Å",
               helpText: "Pixel size of the reconstructed tomograms in Angstrom")]
    public decimal AngPix { get; set; } = 10;

    /// <summary>
    /// Also produce a deconvolved version; all half-tomograms, if requested, will also be deconvolved
    /// </summary>
    [RelayProperty]
    [UiBool("deconv", "Apply deconvolution",
            "Also produce a deconvolved version; all half-tomograms, if requested, will also be deconvolved")]
    public bool DoDeconv { get; set; } = false;

    /// <summary>
    /// Strength of the deconvolution filter, if requested
    /// </summary>
    [RelayProperty]
    [UiDecimal("deconv_strength", "Deconvolution strength", min: 0, max: 10, stepSize: 0.1,
               helpText: "Strength of the deconvolution filter, if requested")]
    public decimal DeconvStrength { get; set; } = 1.0M;

    /// <summary>
    /// Fall-off of the deconvolution filter, if requested
    /// </summary>
    [RelayProperty]
    [UiDecimal("deconv_falloff", "Deconvolution falloff", min: 0, max: 10, stepSize: 0.1,
               helpText: "Fall-off of the deconvolution filter, if requested")]
    public decimal DeconvFalloff { get; set; } = 1.0M;

    /// <summary>
    /// High-pass value (in Angstrom) of the deconvolution filter, if requested
    /// </summary>
    [RelayProperty]
    [UiDecimal("deconv_highpass", "Deconvolution high-pass", min: 10, max: 1000, stepSize: 10, unit: "Å",
               helpText: "High-pass value (in Angstrom) of the deconvolution filter, if requested")]
    public decimal DeconvHighpass { get; set; } = 300.0M;

    /// <summary>
    /// Mask out voxels that aren't contained in some of the tilt images (due to excessive sample shifts); 
    /// don't use if you intend to run template matching
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Post-processing", 1)]
    [UiBool("keep_full_voxels", "Only keep fully sampled voxels",
            "Mask out voxels that aren't contained in some of the tilt images (due to excessive sample shifts); don't use if you intend to run template matching")]
    public bool KeepFullVoxels { get; set; } = false;

    /// <summary>
    /// Don't invert the contrast; contrast inversion is needed for template matching on cryo data,
    /// i.e. when the density is dark in original images
    /// </summary>
    [RelayProperty]
    [UiBool("dont_invert", "Don't invert contrast",
            "Don't invert the contrast; contrast inversion is needed for template matching on cryo data, i.e., when the density is dark in original images")]
    public bool NoInvert { get; set; } = false;

    /// <summary>
    /// Don't normalize the tilt images
    /// </summary>
    [RelayProperty]
    [UiBool("dont_normalize", "Don't normalize",
            "Don't normalize the tilt images")]
    public bool NoNormalize { get; set; } = false;

    /// <summary>
    /// Don't apply a mask to each tilt image if available; otherwise, masked areas will be filled with Gaussian noise
    /// </summary>
    [RelayProperty]
    [UiBool("dont_mask", "Don't mask",
            "Don't apply a mask to each tilt image if available; otherwise, masked areas will be filled with Gaussian noise")]
    public bool NoMask { get; set; } = false;

    /// <summary>
    /// Don't overwrite existing tomograms in output directory
    /// </summary>
    [RelayProperty]
    [UiBool("dont_overwrite", "Don't overwrite existing",
            "Don't overwrite existing tomograms in output directory")]
    public bool NoOverwrite { get; set; } = false;

    /// <summary>
    /// Also produce two half-tomograms, each reconstructed from half of the frames 
    /// (requires running align_frameseries with --average_halves previously)
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Half-Map Generation", 2)]
    [UiBool("halfmap_frames", "Generate half-maps from frames",
            "Also produce two half-tomograms, each reconstructed from half of the frames (requires running align_frameseries with --average_halves previously)")]
    public bool DoHalfmapFrames { get; set; } = false;

    /// <summary>
    /// Also produce two half-tomograms, each reconstructed from half of the tilts
    /// (doesn't work quite as well as --halfmap_frames)
    /// </summary>
    [RelayProperty]
    [UiBool("halfmap_tilts", "Generate half-maps from tilts",
            "Also produce two half-tomograms, each reconstructed from half of the tilts (doesn't work quite as well as generating from frames)")]
    public bool DoHalfmapTilts { get; set; } = false;

    /// <summary>
    /// Reconstruction is performed locally using sub-volumes of this size in pixels
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Advanced Options", 3)]
    [UiInt("subvolume_size", "Subvolume size", min: 16, max: 512, stepSize: 16,
           helpText: "Reconstruction is performed locally using sub-volumes of this size in pixels")]
    public int SubVolumeSize { get; set; } = 64;

    /// <summary>
    /// Padding factor for the reconstruction sub-volumes (helps with aliasing effects at sub-volume borders)
    /// </summary>
    [RelayProperty]
    [UiInt("subvolume_padding", "Subvolume padding", min: 1, max: 10, stepSize: 1,
           helpText: "Padding factor for the reconstruction sub-volumes (helps with aliasing effects at sub-volume borders)")]
    public int SubVolumePadding { get; set; } = 3;
    
    #endregion

    public ReconstructTomograms()
    {
        var portInTiltSeriesSet = new PortIn(this, typeof(TiltSeriesSet), PortInTiltSeriesSet, "Tilt-series", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTiltSeriesSet] = portInTiltSeriesSet
        });

        var portOutTomogramSet = new PortOut(this, typeof(TomogramSet), PortOutTomogramSet, "Reconstructed tomograms", GetTomogramResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutTomogramSet] = portOutTomogramSet
        });
    }

    private TomogramSet GetTomogramResource(int iter)
    {
        if (!PortsIn[PortInTiltSeriesSet].IsConnected)
            return null;

        var tiltSeriesSet = PortsIn[PortInTiltSeriesSet].GetSingleResource<TiltSeriesSet>();

        if (tiltSeriesSet == null)
            throw new InvalidOperationException("Tilt-series input not found.");

        var tomogramSet = new TomogramSet
        {
            // Link to the source tilt series
            TiltSeriesSet = tiltSeriesSet,
            
            // Metadata
            HasMetadata = true,
            LatestMetadataDirectory = DirectoryPath,
            
            // Processing results
            ProcessedItemsJson = ResProcessedItemsJson,
            FailedItemsJson = ResFailedItemsJson,
            
            // Tomogram properties
            PixelSize = AngPix,
            HasDeconvolution = DoDeconv,
            HasHalfMaps = DoHalfmapFrames || DoHalfmapTilts,
            HalfMapType = DoHalfmapFrames ? HalfMapType.Frames : HalfMapType.Tilts,
            OnlyFullVoxelsKept = KeepFullVoxels
        };

        // Set up standard tomogram paths
        tomogramSet.TomogramDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionDirName);
        tomogramSet.ToTomogramPath = (name) => WarpHelper.PathCombine(DirectoryPath,
                                                                      TiltSeries.ToReconstructionTomogramPath(name, AngPix));
        tomogramSet.ToTomogramThumbnailPath = (name) => WarpHelper.PathCombine(DirectoryPath,
                                                                               TiltSeries.ToReconstructionThumbnailPath(name, AngPix));
        
        // Handle deconvolved tomograms if enabled
        if (DoDeconv)
        {
            tomogramSet.TomogramDeconvDirectory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionDeconvDirName);
            tomogramSet.ToTomogramDeconvPath = (name) => WarpHelper.PathCombine(DirectoryPath,
                                                                                TiltSeries.ToReconstructionDeconvPath(name, AngPix));
        }
        
        // Handle half-maps if enabled
        if (DoHalfmapFrames || DoHalfmapTilts)
        {
            tomogramSet.TomogramHalfMap1Directory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionOddDirName);
            tomogramSet.ToTomogramHalfMap1Path = (name) => WarpHelper.PathCombine(DirectoryPath,
                                                                                  TiltSeries.ToReconstructionOddPath(name, AngPix));
            
            tomogramSet.TomogramHalfMap2Directory = WarpHelper.PathCombine(DirectoryPath, TiltSeries.ReconstructionEvenDirName);
            tomogramSet.ToTomogramHalfMap2Path = (name) => WarpHelper.PathCombine(DirectoryPath,
                                                                                  TiltSeries.ToReconstructionEvenPath(name, AngPix));
        }

        return tomogramSet;
    }

    /// <summary>
    /// Gets the name of the Warp command used for tomogram reconstruction.
    /// </summary>
    public override string CommandName => "WarpTools ts_reconstruct";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));

        return result;
    }

    public override void Stage()
    {
        base.Stage();

        var tiltSeriesSet = PortsIn[PortInTiltSeriesSet].GetSingleResource<TiltSeriesSet>();

        if (tiltSeriesSet == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        if (!tiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt-series input must have metadata.");

        Directory.CreateDirectory(DirectoryPath);
        
        // Copy metadata files from the input tilt series
        foreach (var file in Directory.EnumerateFiles(tiltSeriesSet.LatestMetadataDirectory, "*.xml"))
            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), true);

        // Create and save WarpTools settings file
        var optionsWarp = tiltSeriesSet.DataSet.ToOptionsWarp();
        optionsWarp.Import.ProcessingFolder = DirectoryPath;
        
        optionsWarp.Save(Path.Combine(DirectoryPath, "processing.settings"));
    }
    
    public override Action TrackProgressResults()
    {
        var baseUpdate = base.TrackProgressResults();
        
        if (VisAvailableIteration < 0 && !File.Exists(VisCard(0)) && NItemsProcessed > 1)
        {
            var processedItems = JsonSerializer.Deserialize<List<WarpTools.MiniJsonTsItem>>(File.ReadAllText(ResProcessedItemsJson));

            if (processedItems.Count == 0)
                return null;

            TomogramSet tomogramSet = GetTomogramResource(0);

            BakeryWrapper.TsReconstructJobCard(tomogramSet.ToTomogramThumbnailPath(processedItems[0].Path), 
                                               tomogramSet.ToTomogramThumbnailPath(processedItems[1].Path), 
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