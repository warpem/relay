using System.Globalization;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Fs.Extraction.ExtractParticles2D;

/// <summary>
/// Extracts particles from the movies of a micrograph set with WarpTools fs_export_particles: each particle is
/// averaged from its motion-corrected frames, with the CTF from the movie's Warp metadata. The particles are either
/// positions (e.g. picks) or refined particles, which are re-centered on their refined shifts.
/// </summary>
[GenerateReadOnly]
public class ExtractParticles2D : WarpToolsJobGpu, IClusterJob
{
    public override int2 CardSquareCount { get; set; } = new int2(3, 1);
    public override string TypeGuid => "21f9daa1-3e5a-4f04-94b4-63204b1c74b5";
    public override string TypeCategory => "Frame-series.Extraction.Extract particles";
    public override string TypeName => "Extract particles";
    public override string TypeNameShort => "Extract particles";
    public override string TypeDescription => "Extract and export particles from 2D micrographs";
    public override bool IsIterative => false;
    public override Type ExpandedViewType => null;

    // Extraction is as fast as the movies' frames can be decoded on the CPU; the GPU work hides behind it.
    protected override int CoresPerWorker => 4;

    public const string PortInMicrographs = "Micrographs";
    public const string PortInPositions = "Positions";
    public const string PortInParticles = "Particles";
    public const string PortOutParticles = "Particles";

    #region Parameters

    [UiFieldGroup("Extraction Settings", 0)]
    [UiDecimalNullable("angpix_out", "Output pixel size",
                       helpText: "Pixel size the extracted particles will be scaled to; leave empty to use the micrographs' pixel size",
                       min: 0,
                       stepSize: 0.001,
                       unit: "Å")]
    [RelayProperty]
    public decimal? AngpixOut { get; set; } = null;

    [UiInt("box", "Box size",
           helpText: "Particle box size in pixels",
           min: 4,
           max: 4096,
           stepSize: 2,
           unit: "px")]
    [RelayProperty]
    public int BoxSize { get; set; } = 128;

    [UiInt("diameter", "Particle diameter",
           helpText: "Particle diameter in Angstrom; the background outside it is normalized",
           min: 10,
           max: 10000,
           stepSize: 10,
           unit: "Å")]
    [RelayProperty]
    public int Diameter { get; set; } = 100;

    [UiFieldGroup("Advanced Options", 1)]
    [UiBool("dont_invert", "Don't invert contrast",
            "Don't invert contrast, e.g. for negative stain data")]
    [RelayProperty]
    public bool DontInvert { get; set; } = false;

    [UiBool("dont_normalize", "Don't normalize",
            "Don't normalize the background around each particle to zero mean and unit variance, which refinement programs expect")]
    [RelayProperty]
    public bool DontNormalize { get; set; } = false;

    [UiBool("dont_center", "Don't recenter",
            "Don't re-center particles based on their refined shifts, and keep the shifts",
            ConditionalOnField = nameof(HasRefinedShifts), ConditionalOnValue = true)]
    [RelayProperty]
    public bool DontCenter { get; set; } = false;

    [UiBool("flip_phases", "Pre-flip phases",
            "Pre-flip phases in bigger box to avoid signal loss due to delocalization")]
    [RelayProperty]
    public bool PreflipPhases { get; set; } = false;

    [UiBool("keep_ctf", "Keep input CTF",
            "Keep the particles' CTF parameters (e.g. refined per particle) instead of taking them from the movies' Warp metadata")]
    [RelayProperty]
    public bool KeepCtf { get; set; } = false;

    [UiInt("skip_first_frames", "Skip first frames",
           helpText: "Skip first N frames",
           min: 0)]
    [RelayProperty]
    public int SkipFirst { get; set; } = 0;

    [UiInt("skip_last_frames", "Skip last frames",
           helpText: "Skip last N frames",
           min: 0)]
    [RelayProperty]
    public int SkipLast { get; set; } = 0;

    #endregion

    /// <summary>The connected particles have refined shifts, which extraction re-centers them on unless told not to.</summary>
    public bool HasRefinedShifts => ParticleInputs.Sources(PortsIn[PortInParticles])
        .Concat(ParticleInputs.Sources(PortsIn[PortInPositions]))
        .Any(s => s.Resource?.HasShifts == true);

    public string ResParticlesStar => Path.Combine(DirectoryPath, "particles.star");
    public string ResSettings => Path.Combine(DirectoryPath, "processing.settings");

    public ExtractParticles2D()
    {
        var portInMicrographs = new PortIn(this, typeof(MicrographSet), PortInMicrographs, "Micrographs", 1, 1);
        var portInPositions = new PortIn(this, typeof(ParticleSet), PortInPositions, "Positions", 0, 1);
        var portInParticles = new PortIn(this, typeof(ParticleSet), PortInParticles, "Particles", 0, 1);

        PortsIn = new(new Dictionary<string, PortIn>
        {
            [portInMicrographs.Name] = portInMicrographs,
            [portInPositions.Name] = portInPositions,
            [portInParticles.Name] = portInParticles,
        });

        var portOutParticles = new PortOut(this, typeof(ParticleSet), PortOutParticles, "Particles", GetParticlesResource);

        PortsOut = new(new Dictionary<string, PortOut>
        {
            [portOutParticles.Name] = portOutParticles,
        });
    }

    /// <summary>The particles to extract: refined particles if connected, otherwise positions.</summary>
    private ParticleSet InputParticles() =>
        PortsIn[PortInParticles].GetSingleResource<ParticleSet>() ?? PortsIn[PortInPositions].GetSingleResource<ParticleSet>();

    private ParticleSet GetParticlesResource(int iter)
    {
        var result = InputParticles()?.Copy();
        if (result == null)
            return null;

        result.HasData = true;
        result.ParticlesSingleStarPath = ResParticlesStar;
        result.ParticlesMultiStarDirectory = "";
        result.ToMultiStarPath = null;
        result.TomogramsStarPath = null;
        result.OptimisationSetStarPath = null;
        result.ItemCount = null;
        result.DataDimensionality = ParticleType.Image;
        result.HasPositions = true;
        result.HasCtf = true;
        // Re-centering moves each position by its refined shift, which is then zero; coordinates keep their units.
        result.HasShifts = DontCenter && result.HasShifts;
        result.Diameter = Diameter;
        result.PickedInMicrographs = PortsIn[PortInMicrographs].GetSingleResource<MicrographSet>() ?? result.PickedInMicrographs;

        return result;
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = base.ValidateInputs();

        bool hasPositions = PortsIn[PortInPositions].Edges.Any(), hasParticles = PortsIn[PortInParticles].Edges.Any();
        if (hasPositions == hasParticles)
            errors[PortInParticles] = "Connect either positions or particles to extract.";

        if (BoxSize % 2 != 0)
            errors[nameof(BoxSize)] = "The box size must be even.";

        if (PortsIn[PortInMicrographs].GetSingleResource<MicrographSet>() is { } micrographs)
        {
            if (!micrographs.HasMovies || micrographs.DataSetFs == null)
                errors[PortInMicrographs] = "Particles are extracted from the movies, which these micrographs don't have.";
            else if (!micrographs.HasMotion || !micrographs.HasMetadataDirectory)
                errors[PortInMicrographs] = "The micrographs need Warp motion correction metadata.";
            else if (!micrographs.HasCtf && !KeepCtf)
                errors[PortInMicrographs] = "The micrographs need Warp CTF metadata, or keep the particles' input CTF.";
            else if (AngpixOut is { } angpix && angpix < micrographs.DataSetFs.PixelSize * micrographs.DataSetFs.BinFactor)
                errors[nameof(AngpixOut)] = "The output pixel size can't be smaller than the micrographs' pixel size.";
        }

        if (InputParticles() is { } particles && particles.HasNormalizedCoords)
            errors[hasParticles ? PortInParticles : PortInPositions] = "Normalized coordinates aren't supported; the coordinates need a pixel size.";

        return errors;
    }

    public override string CommandName => "WarpTools fs_export_particles";

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var result = base.ComposeCommandArguments();

        var micrographs = PortsIn[PortInMicrographs].GetSingleResource<MicrographSet>()
            ?? throw new InvalidOperationException("No micrographs connected.");
        var particles = InputParticles() ?? throw new InvalidOperationException("No particles connected.");

        result["settings"] = Space.GetRelativePath(ResSettings);
        // A selection of its folder's movies (e.g. a Warp export) lists them; WarpTools would otherwise take them all.
        if (!string.IsNullOrEmpty(micrographs.InputListPath))
            result["input_data"] = Space.GetRelativePath(micrographs.InputListPath);
        // Motion and CTF come from the micrographs' metadata; this job writes its own copy beside the particles.
        result["input_processing"] = Space.GetRelativePath(micrographs.MetadataDirectory);
        result["output_processing"] = Space.GetRelativePath(DirectoryPath);

        if (particles.HasSingleStar)
            result["input"] = Space.GetRelativePath(particles.ParticlesSingleStarPath);
        else if (particles.HasMultiStar)
            result["input"] = Space.GetRelativePath(particles.ParticlesMultiStarDirectory);
        else
            throw new InvalidOperationException("The particles have neither a STAR file nor a folder of them.");

        result["angpix_coords"] = particles.CoordPixelSize.ToString(CultureInfo.InvariantCulture);
        result["output"] = Space.GetRelativePath(ResParticlesStar);
        result["averages"] = "";

        return result;
    }

    public override void Stage()
    {
        base.Stage();

        var micrographs = PortsIn[PortInMicrographs].GetSingleResource<MicrographSet>()
            ?? throw new InvalidOperationException("No micrographs connected.");
        if (micrographs.DataSetFs == null)
            throw new InvalidOperationException("The micrographs don't come with their movies' import settings.");

        Directory.CreateDirectory(DirectoryPath);

        OptionsWarp optionsWarp = micrographs.DataSetFs.ToOptionsWarp();
        optionsWarp.Import.ProcessingFolder = DirectoryPath;
        optionsWarp.Save(ResSettings);
    }

    public override Action TrackProgressResults()
    {
        var baseUpdate = base.TrackProgressResults();

        // WarpTools writes the STAR file last, once every movie is done, and the success marker after it.
        if (VisAvailableIteration < 0 && File.Exists(PathSuccess) && File.Exists(ResParticlesStar))
        {
            Directory.CreateDirectory(RelayResultsDirectoryPath);
            try
            {
                if (!File.Exists(VisCard(0)))
                    BakeryWrapper.ImportParticlesJobCard([ResParticlesStar], VisCard(0), Space.RootDirectory);
            }
            // The particles are there without their preview.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WriteToLifecycleLog($"The job card could not be made: {ex.Message}").GetAwaiter().GetResult();
            }

            return () =>
            {
                baseUpdate?.Invoke();
                VisAvailableIteration = 0;
            };
        }

        return baseUpdate;
    }
}
