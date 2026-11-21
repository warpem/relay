using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.UIFields;
using Refund.Utils;
using Refund.Utils.Star;
using Warp.Tools;

namespace Refund.Jobs.Common.Tools.DeduplicateParticles;

public enum DuplicateRemoval
{
    [Display(Name = "The worse particle")] Worse,
    [Display(Name = "Both particles")] Both
}

public enum DuplicateScore
{
    [Display(Name = "Automatic")] Auto,
    [Display(Name = "STAR column")] Column,
    [Display(Name = "None (keep the first)")] None
}

/// <summary>
/// Removes particles closer together than a minimum distance within each micrograph or tilt series, with
/// <c>beam deduplicate</c>. Positions are coordinates re-centred on refined shifts, so particles that refinement moved
/// onto the same molecule are found. Several connected particle sets are merged first; a folder of per-micrograph STAR
/// files stays a folder.
/// </summary>
[GenerateReadOnly]
public class DeduplicateParticles : Job, IClusterJob
{
    public override int2 CardSquareCount { get; set; } = new(1, 1);
    public override string TypeGuid => "6c1f3b8e-2d47-4a95-b0e1-7f9d84c2a613";
    public override string TypeCategory => "Common.Tools.Deduplicate particles";
    public override string TypeName => "Deduplicate particles";
    public override string TypeNameShort => "Deduplicate";
    public override string TypeDescription => "Remove particles closer together than a minimum distance using Beam";
    public override JobQueueType QueueType => JobQueueType.CPU;
    public override string[] SupportedModules => ["beam", "cpu"];
    public override string[] RequiredModules => ["beam", "cpu"];
    public override int GpuCount => 0;
    public override int CoreCount => 1;
    public override int ProcessCount => 1;
    public override int MemoryGb => Memory;
    public override bool IsIterative => false;
    public override Type ExpandedViewType => null;
    public override string CommandName => "beam deduplicate";
    public override string CommandPrefix => "OMP_NUM_THREADS=1 MKL_NUM_THREADS=1 OPENBLAS_NUM_THREADS=1 ";
    public override string CommandSuffix => " && touch " + JobTools.QuoteArgumentValue(PathSuccess);

    public const string PortInParticles = "Particles";
    public const string PortOutParticles = "Particles";

    #region Parameters

    [UiFieldGroup("Parameters", 0)]
    [UiDecimal("", "Minimum distance", min: 0.1, max: 10000, stepSize: 1, unit: "Å",
        helpText: "Particles at most this far apart, after re-centring on their refined shifts, are duplicates.")]
    [RelayProperty]
    public decimal Distance { get; set; } = 20;

    [UiEnum("", "Remove", typeof(DuplicateRemoval),
        helpText: "Remove the worse-scoring particle of each duplicate, keeping the best of a cluster, or every particle that has a duplicate.")]
    [RelayProperty]
    public DuplicateRemoval Removal { get; set; } = DuplicateRemoval.Worse;

    [UiEnum("", "Score", typeof(DuplicateScore),
        helpText: "Automatic uses the first column present of Beam's signal scale (bmSignalScale), rlnLogLikeliContribution, " +
                  "rlnMaxValueProbDistribution, rlnAutopickFigureOfMerit and rlnLCCmax, higher being better. " +
                  "Without a score, the particle listed first is kept.",
        ConditionalOnField = nameof(Removal), ConditionalOnValue = DuplicateRemoval.Worse)]
    [RelayProperty]
    public DuplicateScore Score { get; set; } = DuplicateScore.Auto;

    public bool UsesScoreColumn => Removal == DuplicateRemoval.Worse && Score == DuplicateScore.Column;

    [UiString("", "Score column", helpText: "STAR column ranking duplicates, e.g. rlnAutopickFigureOfMerit.",
        ConditionalOnField = nameof(UsesScoreColumn), ConditionalOnValue = true)]
    [RelayProperty]
    public string ScoreColumn { get; set; } = "";

    [UiBool("", "Lower is better", helpText: "Keep the particle with the lowest value in the score column.",
        ConditionalOnField = nameof(UsesScoreColumn), ConditionalOnValue = true)]
    [RelayProperty]
    public bool LowerIsBetter { get; set; }

    [UiDecimalNullable("", "Coordinate pixel size", min: 0, stepSize: 0.001, unit: "Å", isAdvanced: true,
        helpText: "Pixel size of the particles' rlnCoordinateX/Y/Z. Leave empty to use the particle set's own.")]
    [RelayProperty]
    public decimal? CoordinatePixelSize { get; set; }

    [UiFieldGroup("Compute", 1)]
    [UiInt("", "Host memory", min: 1, max: 4096, unit: "GB")]
    [RelayProperty]
    public int Memory { get; set; } = 8;

    #endregion

    /// <summary>Particles left after deduplication, or -1 until the run has published them.</summary>
    [RelayProperty, Clearable] public int KeptParticleCount { get; set; } = -1;

    public string ResParticlesStar => Path.Combine(DirectoryPath, "particles.star");
    public string ResParticlesDirectory => Path.Combine(DirectoryPath, "particles");
    public string ResOptimisationSetStar => Path.Combine(DirectoryPath, "optimisation_set.star");

    public DeduplicateParticles()
    {
        PortsIn = new(new Dictionary<string, PortIn>
        {
            [PortInParticles] = new(this, typeof(ParticleSet), PortInParticles, "Particles", 1, int.MaxValue)
        });
        PortsOut = new(new Dictionary<string, PortOut>
        {
            [PortOutParticles] = new(this, typeof(ParticleSet), PortOutParticles, "Particles", GetParticles)
        });
    }

    /// <summary>The connected particles; several sets describe their merged single STAR file.</summary>
    private ParticleSet InputParticles() => ParticleInputs.Describe(PortsIn[PortInParticles]);

    /// <summary>A folder of per-micrograph or per-tomogram STAR files, rather than one STAR file.</summary>
    private static bool IsFolder(ParticleSet particles) => !particles.HasSingleStar && particles.HasMultiStar;

    private ParticleSet GetParticles(int iteration)
    {
        var input = InputParticles();
        var result = input?.Copy();
        if (result == null)
            return null;

        if (IsFolder(input))
        {
            string directory = ResParticlesDirectory;
            var toInputPath = input.ToMultiStarPath;
            result.ParticlesMultiStarDirectory = directory;
            result.ToMultiStarPath = toInputPath == null ? null : path => Path.Combine(directory, Path.GetFileName(toInputPath(path)));
        }
        else
        {
            result.ParticlesSingleStarPath = ResParticlesStar;
            if (!string.IsNullOrEmpty(input.OptimisationSetStarPath))
                result.OptimisationSetStarPath = ResOptimisationSetStar;
        }
        result.ItemCount = KeptParticleCount >= 0 ? KeptParticleCount : null;
        return result;
    }

    public override Dictionary<string, List<string>> ValidatePortInputs()
    {
        var errors = base.ValidatePortInputs();
        var sources = ParticleInputs.Sources(PortsIn[PortInParticles]);
        foreach (var (_, particles) in sources)
        {
            if (particles == null)
                continue;
            if (!particles.HasSingleStar && !particles.HasMultiStar)
                AddPortValidationError(errors, PortInParticles, "The particles have neither a STAR file nor a folder of them.");
            if (particles.HasNormalizedCoords)
                AddPortValidationError(errors, PortInParticles, "Normalized coordinates aren't supported; the coordinates need a pixel size.");
        }
        return errors;
    }

    public override Dictionary<string, string> ValidateInputs()
    {
        var errors = base.ValidateInputs();
        if (Distance <= 0)
            errors[nameof(Distance)] = "The minimum distance must be positive.";
        if (UsesScoreColumn && string.IsNullOrWhiteSpace(ScoreColumn))
            errors[nameof(ScoreColumn)] = "Name the STAR column to rank duplicates by.";
        else if (UsesScoreColumn && ScoreColumn.Trim().Any(char.IsWhiteSpace))
            errors[nameof(ScoreColumn)] = "A STAR column name can't contain spaces.";
        if (CoordinatePixelSize is <= 0)
            errors[nameof(CoordinatePixelSize)] = "The coordinate pixel size must be positive or empty.";
        if (Memory < 1)
            errors[nameof(Memory)] = "Host memory must be positive.";
        return errors;
    }

    /// <summary>The coordinate pixel size passed to Beam: the field if set, otherwise the particle set's.</summary>
    public decimal EffectiveCoordinatePixelSize(ParticleSet particles) => CoordinatePixelSize ?? particles.CoordPixelSize;

    public override Dictionary<string, string> ComposeCommandArguments()
    {
        var errors = ValidateInputs();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join("\n", errors.Values));
        var particles = InputParticles() ?? throw new InvalidOperationException("Connect input particles.");
        if (particles.HasNormalizedCoords)
            throw new InvalidOperationException("Normalized coordinates aren't supported; the coordinates need a pixel size.");

        bool folder = IsFolder(particles);
        string input = folder ? particles.ParticlesMultiStarDirectory : particles.ParticlesSingleStarPath;
        if (string.IsNullOrEmpty(input))
            throw new InvalidOperationException("The particles have neither a STAR file nor a folder of them.");

        var args = new Dictionary<string, string>
        {
            ["input"] = Path.GetFullPath(input, Space.RootDirectory),
            ["output"] = folder ? ResParticlesDirectory : ResParticlesStar,
            ["project-root"] = Space.RootDirectory,
            ["distance"] = Distance.ToString(CultureInfo.InvariantCulture),
            ["drop"] = Removal == DuplicateRemoval.Both ? "both" : "worse",
            ["coordinate-pixel-size"] = EffectiveCoordinatePixelSize(particles).ToString(CultureInfo.InvariantCulture)
        };
        if (Removal == DuplicateRemoval.Worse)
        {
            args["score"] = Score switch
            {
                DuplicateScore.Column => ScoreColumn.Trim(),
                DuplicateScore.None => "none",
                _ => "auto"
            };
            if (Score == DuplicateScore.Column && LowerIsBetter)
                args["lower-is-better"] = "";
        }
        return args;
    }

    public override void Stage()
    {
        base.Stage();
        // Beam only rewrites particles. A RELION optimisation set must name the deduplicated file; the tomograms are
        // unchanged and stay where they are.
        var particles = InputParticles();
        if (particles != null && !IsFolder(particles) && !string.IsNullOrEmpty(particles.OptimisationSetStarPath))
        {
            Directory.CreateDirectory(DirectoryPath);
            Refund.Jobs.Common.Tools.ExpandSymmetry.ExpandSymmetry.AdaptOptimisationSet(particles.OptimisationSetStarPath, ResOptimisationSetStar,
                Space.GetRelativePath(ResParticlesStar));
        }
    }

    public override Action TrackProgressLogs()
    {
        var stderrUpdate = base.TrackProgressLogs();
        if (!File.Exists(PathStdOut))
            return stderrUpdate;
        // Like ImportParticles: the log is small, and a cached length on a shared filesystem must not hide its end.
        var lines = JobTools.CleanProgressBarLines(File.ReadAllText(PathStdOut).Split('\n'));
        JobTools.WriteLogFile(string.Join('\n', lines), LogFilePath(0));
        return () => { stderrUpdate?.Invoke(); LogsAvailableIteration = 0; };
    }

    public override Action TrackProgressResults()
    {
        if (KeptParticleCount >= 0)
            return null;
        var particles = InputParticles();
        if (particles == null)
            return null;
        // Beam publishes its output only when complete, so once it is there it can be counted.
        string[] files;
        if (IsFolder(particles))
        {
            if (!Directory.Exists(ResParticlesDirectory))
                return null;
            files = Directory.GetFiles(ResParticlesDirectory, "*.star");
        }
        else
        {
            try
            {
                // Open rather than gating on File.Exists: close-to-open consistency refreshes shared-filesystem attributes.
                using var published = File.OpenRead(ResParticlesStar);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            files = [ResParticlesStar];
        }
        long count = files.Sum(CountParticles);
        int kept = checked((int)count);
        return () => KeptParticleCount = kept;
    }

    /// <summary>Rows of a STAR file's particle loop: <c>data_particles</c>, or its one loop with coordinates.</summary>
    internal static long CountParticles(string path)
    {
        var star = new StreamingStar(path, CancellationToken.None);
        var block = star.FindBlock("particles");
        if (block == null || !block.IsLoop)
        {
            var loops = star.Blocks.Where(b => b.IsLoop && (b.Columns.Contains("rlnCoordinateX") ||
                                                            b.Columns.Contains("rlnCenteredCoordinateXAngst"))).ToArray();
            if (loops.Length != 1)
                throw new InvalidDataException($"{path}: expected one particle loop.");
            block = loops[0];
        }
        return block.RowCount;
    }
}
