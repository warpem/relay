using System.Globalization;
using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;
using WarpHelper = Warp.Tools.Helper;

namespace Refund.Jobs.Ts.ExtractParticles;

/// <summary>
/// Job that extracts particles from tilt series as 2D image stacks or 3D volumes.
/// This is based on the WarpTools ExportParticlesTiltseries command.
/// </summary>
[GenerateReadOnly]
public class ExtractParticles : WarpJobGpu, IClusterJob
{
    public override string TypeGuid => "a3cc57ee-0308-4181-bc9c-2675874e354d";
    
    public override string TypeCategory => "Tilt-series.Extraction.Extract particles";

    public override string TypeName => "Extract particles";

    public override string TypeNameShort => "Extract";

    public override string TypeDescription => "Extracts particles from tilt series as 2D image stacks or 3D volumes";

    public override int CoreCount => (NGpus * PerDevice) * 3;

    public override Type ExpandedViewType => typeof(ExtractParticlesExpandedView);

    public override int2 CardSquareCount { set; get; } = new int2(2, 1);

    /// <summary>
    /// Port name constants
    /// </summary>
    public const string PortInTiltSeries = "TiltSeries";
    public const string PortInParticleSet = "Positions";
    public const string PortOutParticleSet = "Particles";
    
    #region Parameters

    /// <summary>
    /// Output particles as 2D image series centered on the particle
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Output Type", 0)]
    [UiEnum(null, "Particle type", typeof(ExportType), 
            "Output particles as 2D image series or sub-tomograms")]
    public ExportType OutputType { get; set; } = ExportType.Tiltseries;

    /// <summary>
    /// Pixel size at which to export particles
    /// </summary>
    [RelayProperty]
    [UiFieldGroup("Output Parameters", 1)]
    [UiDecimal("output_angpix", "Output pixel size", min: 0.1, max: 999999, stepSize: 0.01, unit: "Å",
               helpText: "Pixel size at which to export particles")]
    public decimal OutputPixelSize { get; set; } = 2.0M;
    
    /// <summary>
    /// Output box size in pixels/voxels
    /// </summary>
    [RelayProperty]
    [UiInt("box", "Box size", min: 2, max: 4096, stepSize: 2, unit: "px",
           helpText: "Output box size in pixels/voxels")]
    public int OutputBoxSize { get; set; } = 128;
    
    /// <summary>
    /// Particle diameter in angstroms
    /// </summary>
    [RelayProperty]
    [UiInt("diameter", "Particle diameter", min: 10, max: 1000, stepSize: 10, unit: "Å",
           helpText: "Particle diameter in angstroms")]
    public int ParticleDiameter { get; set; } = 100;
    
    /// <summary>
    /// Number of tilt images to include in the output
    /// </summary>
    [RelayProperty]
    [UiIntNullable("n_tilts", "Maximum tilts", min: 1, max: 100, stepSize: 1,
                  helpText: "Number of tilt images to include in the output; tilts with the lowest overall exposure will be included first")]
    public int? OutputNTilts { get; set; } = null;
    
    #endregion

    /// <summary>
    /// Constructor
    /// </summary>
    public ExtractParticles()
    {
        var portInTiltSeriesSet = new PortIn(this, typeof(TiltSeriesSet), PortInTiltSeries, "Tilt-series", 1, 1);
        var portInPositionSet = new PortIn(this, typeof(ParticleSet), PortInParticleSet, "Particle positions", 1, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInTiltSeries] = portInTiltSeriesSet,
            [PortInParticleSet] = portInPositionSet
        });

        var portOutParticleSet = new PortOut(this, typeof(ParticleSet), PortOutParticleSet, "Exported particles", GetParticleSetResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutParticleSet] = portOutParticleSet
        });
    }
    
    /// <summary>
    /// Resource generator for the output ParticleSet
    /// </summary>
    private ParticleSet GetParticleSetResource(int iter)
    {
        var result = PortsIn[PortInParticleSet].GetSingleResource<ParticleSet>();
        
        string particlesStarPath = Path.Combine(DirectoryPath, "particles.star");
        string tomogramsStarPath = Path.Combine(DirectoryPath, "particles_tomograms.star");
        string optimisationSetStarPath = Path.Combine(DirectoryPath, "particles_optimisation_set.star");

        result.HasData = true;
        result.ParticlesSingleStarPath = particlesStarPath;
        result.ParticlesMultiStarDirectory = string.Empty;
        result.ToMultiStarPath = null;
        result.TomogramsStarPath = tomogramsStarPath;
        result.OptimisationSetStarPath = optimisationSetStarPath;

        result.DataDimensionality = OutputType == ExportType.Tiltseries ?
                                        ParticleType.Tiltseries :
                                        ParticleType.Tomogram;

        result.HasPositions = true;
        result.HasNormalizedCoords = false;
        result.CoordPixelSize = OutputPixelSize;
        result.HasAngles = true;
        result.HasCtf = true;
        
        return result;
    }
    
    /// <summary>
    /// Gets the name of the Warp command used for particle extraction from tilt series.
    /// </summary>
    public override string CommandName => "WarpTools ts_export_particles";
    
    /// <summary>
    /// Composes command line arguments for the particle extraction command.
    /// </summary>
    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();
        
        result["settings"] = Space.GetRelativePath(Path.Combine(Path.GetFullPath(DirectoryPath), "processing.settings"));
        
        // Set export type
        if (OutputType == ExportType.Tiltseries) 
            result["2d"] = "";
        else
            result["3d"] = "";
        
        // Output parameters
        result["output_star"] = Space.GetRelativePath(Path.Combine(DirectoryPath, "particles.star"));
        result["output_angpix"] = OutputPixelSize.ToString(CultureInfo.InvariantCulture);
        result["box"] = OutputBoxSize.ToString();
        result["diameter"] = ParticleDiameter.ToString();
        
        if (OutputNTilts.HasValue)
            result["n_tilts"] = OutputNTilts.Value.ToString();
        
        // Input options
        var particleSet = PortsIn[PortInParticleSet].GetSingleResource<ParticleSet>();
        
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
        
        return result;
    }
    
    /// <summary>
    /// Prepares the job for execution.
    /// </summary>
    public override void Stage()
    {
        base.Stage();
        
        var tiltSeriesSet = PortsIn[PortInTiltSeries].GetSingleResource<TiltSeriesSet>();
        
        if (tiltSeriesSet == null)
            throw new InvalidOperationException("Tilt-series input not found.");
        
        if (!tiltSeriesSet.HasMetadata)
            throw new InvalidOperationException("Tilt series must have metadata.");

        Directory.CreateDirectory(DirectoryPath);
        
        // Copy XML metadata files
        foreach (var file in Directory.EnumerateFiles(tiltSeriesSet.LatestMetadataDirectory, "*.xml"))
            File.Copy(file, Path.Combine(DirectoryPath, Path.GetFileName(file)), true);
            
        // Set up Warp options
        var optionsWarp = tiltSeriesSet.DataSet.ToOptionsWarp();
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

            string foundParticlePath = null;
            WarpTools.MiniJsonTsItem foundItem = null;
            foreach (var item in processedItems)
            {
                string particlePath = Path.Combine(DirectoryPath, TiltSeries.ToParticleSeriesFilePath(item.Path, OutputPixelSize, 1));
                if (File.Exists(particlePath))
                {
                    foundParticlePath = particlePath;
                    foundItem = item;
                    break;
                }
            }
            if (foundParticlePath == null)
                return null;
            
            string averagePath = Path.Combine(DirectoryPath, TiltSeries.ToParticleSeriesAveragePath(foundItem.Path, OutputPixelSize));

            BakeryWrapper.TsExportParticlesJobCard(foundParticlePath,
                                                   averagePath,
                                                   (float)OutputPixelSize,
                                                   ParticleDiameter,
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

public enum ExportType
{
    Tiltseries,
    Subtomograms
}