using System.Globalization;
using System.Text.Json;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Services.Warp;
using Refund.Utils;
using Warp;
using Warp.Headers;
using Warp.Tools;

namespace Refund.Jobs.Fs.Warp;

/// <summary>
/// Exports a Warp session's accepted selection. The job writes it when it runs (DataManager runs it against the source
/// session), so running it again exports the session as it is then. It has no input edge: the session's job need not
/// finish, since it processes for as long as data arrives.
/// </summary>
[GenerateReadOnly]
public abstract class WarpExportJob : Job, ILocalJob
{
    protected WarpExportJob() => PortsIn = new(new Dictionary<string, PortIn>());

    [RelayProperty] public int SourceWarpJobId { get; set; }
    /// <summary>
    /// What the last run wrote, with its files relative to the space (see <see cref="WarpExportManifest.ResolvedIn"/>);
    /// null until the job has run.
    /// </summary>
    [RelayProperty, Clearable] public WarpExportManifest Manifest { get; set; }
    public bool Particles => this is WarpParticlesExportJob;
    // Two sample micrographs beside the export's numbers (WarpExportCardContent).
    public override int2 CardSquareCount { get; set; } = new(3, 1);
    public override Type CardViewType => typeof(WarpExportCardContent);
    public override JobQueueType QueueType => JobQueueType.Local;
    public override int GpuCount => 0;
    public override string NameStdOut => Path.GetRelativePath(DirectoryPath, LifecycleFilePath);
    public override Type ExpandedViewType => null;
    public override string TypeDescription => "A Warp session's accepted selection, written when the job runs";
    public string StarPath => Path.Combine(DirectoryPath, Particles ? "particles.star" : "micrographs.star");
    public string ProcessedItemsJson => Path.Combine(DirectoryPath, "processed_items.json");
    /// <summary>What the card shows (<see cref="WarpExportCard"/>), written when the export runs.</summary>
    public string CardPath => Path.Combine(DirectoryPath, "card.json");

    public override Dictionary<string, string> ValidateInputs() => Space?.FindJob(SourceWarpJobId) switch
    {
        WarpJob { Status: JobStatus.Clearing or JobStatus.Deleted } => new() { [nameof(SourceWarpJobId)] = $"Warp session J{SourceWarpJobId} is being removed." },
        WarpJob => new(),
        _ => new() { [nameof(SourceWarpJobId)] = "The Warp session this export comes from no longer exists." }
    };

    /// <summary>The export needs its source session, so Relay runs it through the session (DataManager), not here.</summary>
    public void RunLocal(CancellationToken token) =>
        throw new InvalidOperationException("A Warp export runs through its source session.");

    /// <summary>
    /// Writes the card (<see cref="CardPath"/>) and the log lines describing what <paramref name="manifest"/> exported.
    /// The export itself is done by then, so a summary that fails is only logged.
    /// </summary>
    internal void Summarize(WarpExportManifest manifest)
    {
        WarpExportCard card;
        try
        {
            var items = JsonSerializer.Deserialize<List<WarpTools.MiniJsonFsItem>>(File.ReadAllText(manifest.ProcessedItemsJson)) ?? [];
            card = WarpExportCard.Create(manifest, items, manifest.StarPath);
            // The card's thumbnails are the session's, saved relative to the space like the export's other paths.
            string root = Space?.RootDirectory;
            var saved = card with { Samples = card.Samples.Select(sample => sample with { Thumbnail = WarpPaths.Relative(root, sample.Thumbnail) }).ToArray() };
            File.WriteAllText(CardPath, JsonSerializer.Serialize(saved));
        }
        // Any unreadable file (a truncated average's header throws ArgumentException) only costs the card its samples.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            card = new(manifest.MovieCount, manifest.ParticleCount, null, null, null, null, []);
            WriteToLifecycleLog($"The card summary could not be made: {ex.Message}").GetAwaiter().GetResult();
        }
        foreach (string line in Describe(manifest, SourceWarpJobId, card))
            WriteToLifecycleLog(line).GetAwaiter().GetResult();
    }

    private const string NoEpuGroup = "noEpuMetadata";

    /// <summary>What the export holds and how it was selected, for the job's log.</summary>
    internal static IEnumerable<string> Describe(WarpExportManifest m, int sourceJobId, WarpExportCard card)
    {
        var s = m.Settings;
        string when = m.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        yield return m.Particles
            ? $"Exported from Warp session J{sourceJobId} on {when}: {m.ParticleCount:N0} particles from {m.MovieCount:N0} accepted micrographs with current picking and extraction."
            : $"Exported from Warp session J{sourceJobId} on {when}: {m.MovieCount:N0} accepted micrographs with current motion and CTF results.";
        var f = m.Filters;
        var limits = new List<string>
        {
            $"CTF fit {f.MinCtf:0.##}–{f.MaxCtf:0.##} Å",
            f.MaxCtfSignal is { } maxSignal ? $"CTF signal {f.MinCtfSignal:0.#}–{maxSignal:0.#} σ" : $"CTF signal ≥ {f.MinCtfSignal:0.#} σ",
            $"motion {f.MinMotion:0.##}–{f.MaxMotion:0.##} Å/frame"
        };
        if (s.FitThickness) limits.Add($"ice {f.MinIce:0}–{f.MaxIce:0} Å");
        limits.Add($"astigmatism within {f.MaxAstigmatismSigma:0.#} σ of its neighbours");
        if (s.PickingEnabled && (f.MinParticles > 0 || f.MaxParticles < int.MaxValue))
            limits.Add(f.MaxParticles < int.MaxValue ? $"{f.MinParticles:N0}–{f.MaxParticles:N0} particles" : $"at least {f.MinParticles:N0} particles");
        yield return "Accepted: movies marked Keep, or passing the filters (" + string.Join(", ", limits) + ").";
        if (m.Particles)
        {
            var b = s.ParticleBounds;
            string power = b.MaxPower is { } maxPower ? $"{b.MinPower:0.###}–{maxPower:0.###}" : $"≥ {b.MinPower:0.###}";
            yield return $"Particles: picks with correlation {b.MinCorrelation:0.##}–{b.MaxCorrelation:0.##} and local power {power}, extracted in {s.Box} px boxes at {s.TargetPixelSize:0.###} Å/px" +
                (s.RescaleParticles ? $" and rescaled to {s.RescaledBox} px at {s.ParticlePixelSize:0.###} Å/px" : "") + $"; particle diameter {s.Diameter:0} Å.";
        }
        var o = m.OpticsGroups;
        yield return !s.EpuOpticsGroups ? "Optics groups: one for all micrographs (EPU optics groups are off)."
            : !o.FromEpu ? "Optics groups: one for all micrographs; none had EPU metadata with a beam shift."
            : $"Optics groups: {o.Groups:N0} by EPU beam shift, each within {s.EpuGroupRadius:0.###} of its group's mean" +
              (o.WithoutMetadata > 0 ? $"; {NoEpuGroup} holds the {o.WithoutMetadata:N0} micrographs without EPU metadata." : ".");
        yield return $"Data: {s.Pixel:0.####} Å/px movies at {s.TargetPixelSize:0.###} Å/px, {s.TotalDose:0.#} e⁻/Å², {s.Voltage} kV, Cs {s.Cs:0.##} mm.";
        var stats = new List<string>();
        if (card.MedianResolution is { } resolution) stats.Add($"median CTF fit {resolution:0.0} Å");
        if (card.MedianMotion is { } motion) stats.Add($"median motion {motion:0.00} Å/frame");
        if (card.DefocusMin is { } low && card.DefocusMax is { } high) stats.Add($"defocus {low:0.00}–{high:0.00} µm");
        if (stats.Count > 0) yield return "Micrographs: " + string.Join(", ", stats) + ".";
        string list = $", processed_items.json and {Path.GetFileName(m.InputListPath)}";
        yield return m.Particles
            ? $"Wrote particles.star ({m.ParticleCount:N0} particles){list}."
            : $"Wrote micrographs.star ({m.MovieCount:N0} micrographs){list}.";
    }

    protected MicrographSet Micrographs()
    {
        if (Manifest == null) return null;
        var manifest = Manifest.ResolvedIn(Space.RootDirectory);
        var s = manifest.Settings;
        string root = manifest.ProcessingDirectory;
        return new ExportedMicrographSet(StarPath)
        {
            DataSetFs = new DataSetFs
            {
                DataDirectory = manifest.DataDirectory, FileSearchPattern = s.InputPattern,
                DoRecursiveSearch = false, PixelSize = s.Pixel,
                BinFactor = s.TargetPixelSize / s.Pixel, OverallExposure = s.TotalDose,
                Voltage = s.Voltage, Cs = s.Cs, AmplitudeContrast = s.Amplitude,
                EerFrames = s.EerGrouping, GainPath = s.CorrectGain ? s.GainPath : "",
                DefectsPath = s.CorrectDefects ? s.DefectsPath : "",
                GainFlipX = s.GainFlipX, GainFlipY = s.GainFlipY, GainTranspose = s.GainTranspose
            },
            HasMovies = true, HasMotion = true, HasCtf = true, HasMetadata = true,
            ItemCount = manifest.MovieCount, ProcessedItemsJson = manifest.ProcessedItemsJson,
            // The data directory is the session's, holding every movie it received; the list holds the exported ones.
            InputListPath = manifest.InputListPath,
            AverageDirectory = Path.Combine(root, Movie.AverageDirName),
            ToAveragePath = name => Path.Combine(root, Movie.ToAveragePath(name)),
            AverageOddDirectory = Directory.Exists(Path.Combine(root, Movie.AverageOddDirName)) ? Path.Combine(root, Movie.AverageOddDirName) : "",
            ToAverageOddPath = name => Path.Combine(root, Movie.ToAverageOddPath(name)),
            AverageEvenDirectory = Directory.Exists(Path.Combine(root, Movie.AverageEvenDirName)) ? Path.Combine(root, Movie.AverageEvenDirName) : "",
            ToAverageEvenPath = name => Path.Combine(root, Movie.ToAverageEvenPath(name)),
            AverageDenoisedDirectory = Directory.Exists(Path.Combine(root, Movie.AverageDenoisedDirName)) ? Path.Combine(root, Movie.AverageDenoisedDirName) : "",
            ToAverageDenoisedPath = name => Path.Combine(root, Movie.ToAverageDenoisedPath(name)),
            PowerspectrumDirectory = Path.Combine(root, Movie.PowerSpectrumDirName),
            ToPowerspectrumPath = name => Path.Combine(root, Movie.ToPowerSpectrumPath(name)),
            ThumbnailDirectory = Path.Combine(root, Movie.ThumbnailsDirName),
            ToThumbnailPath = name => Path.Combine(root, Movie.ToThumbnailsPath(name))
        };
    }

    private sealed class ExportedMicrographSet(string starPath) : MicrographSet
    {
        public override IEnumerable<Downloadable> GetDownloadables() =>
            [new Downloadable("Micrographs", "Warp micrograph STAR", starPath)];
    }
}

[GenerateReadOnly]
[HideFromMenu]
public class WarpMicrographsExportJob : WarpExportJob
{
    public override string TypeGuid => "6504c297-2299-4632-893b-1b81274cd1ae";
    public override string TypeCategory => "Frame-series.Export.Warp micrographs";
    public override string TypeName => "Warp micrographs";
    public override string TypeNameShort => "Warp micrographs";
    public WarpMicrographsExportJob()
    {
        var output = new PortOut(this, typeof(MicrographSet), "Micrographs", "Micrographs", _ => Micrographs());
        PortsOut = new(new Dictionary<string, PortOut> { [output.Name] = output });
    }
}

[GenerateReadOnly]
[HideFromMenu]
public class WarpParticlesExportJob : WarpExportJob
{
    public override string TypeGuid => "8eca0b85-72e4-45e5-b2e9-380c7baf95f0";
    public override string TypeCategory => "Frame-series.Export.Warp particles";
    public override string TypeName => "Warp particles";
    public override string TypeNameShort => "Warp particles";
    public WarpParticlesExportJob()
    {
        var output = new PortOut(this, typeof(ParticleSet), "Particles", "Particles", _ => Manifest == null ? null : new ParticleSet
        {
            ParticlesSingleStarPath = StarPath, ItemCount = Manifest.ParticleCount,
            HasData = Manifest.HasParticleData, HasPositions = true, HasCtf = true,
            CoordPixelSize = Manifest.Settings.TargetPixelSize,
            Diameter = (int)Math.Round(Manifest.Settings.Diameter),
            PickedInMicrographs = Micrographs()
        });
        PortsOut = new(new Dictionary<string, PortOut> { [output.Name] = output });
    }
}

/// <summary>
/// What a Warp export's card shows, summarised when the export runs: its micrographs' CTF, motion and defocus, and two
/// sample micrographs (thumbnail, size in pixels of the average, and for particle exports the extracted positions).
/// </summary>
public sealed record WarpExportCard(int Micrographs, long Particles, double? MedianResolution, double? MedianMotion,
    double? DefocusMin, double? DefocusMax, WarpExportCardSample[] Samples)
{
    public static WarpExportCard Create(WarpExportManifest manifest, IReadOnlyList<WarpTools.MiniJsonFsItem> items, string starPath)
    {
        string root = manifest.ProcessingDirectory;
        static double? Median(IEnumerable<double?> values)
        {
            var sorted = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v.Value).OrderBy(v => v).ToArray();
            return sorted.Length == 0 ? null : sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }
        var defoci = items.Select(i => i.Defocus).Where(d => d.HasValue && double.IsFinite(d.Value)).Select(d => d.Value).ToArray();

        // Particle positions per micrograph, in the average's pixels, in the order the STAR lists them. Micrographs are
        // matched by name: particles name their movie, whose name the average shares.
        var positions = new Dictionary<string, (List<float> X, List<float> Y)>();
        if (manifest.Particles && File.Exists(starPath))
        {
            var table = new Star(starPath, "particles");
            string[] names = table.GetColumn("rlnMicrographName"), x = table.GetColumn("rlnCoordinateX"), y = table.GetColumn("rlnCoordinateY");
            for (int i = 0; names != null && x != null && y != null && i < table.RowCount; i++)
            {
                string name = Path.GetFileNameWithoutExtension(names[i]);
                if (!positions.TryGetValue(name, out var list)) positions[name] = list = ([], []);
                list.X.Add(float.Parse(x[i], CultureInfo.InvariantCulture)); list.Y.Add(float.Parse(y[i], CultureInfo.InvariantCulture));
            }
        }
        // Two micrographs a third and two thirds into the acquisition; for particles, among those with particles.
        var candidates = items.Select(i => i.Path).Where(alias => !manifest.Particles || positions.ContainsKey(Path.GetFileNameWithoutExtension(alias))).ToArray();
        var picks = candidates.Length <= 2 ? candidates : [candidates[candidates.Length / 3], candidates[2 * candidates.Length / 3]];
        var samples = new List<WarpExportCardSample>();
        foreach (string alias in picks)
        {
            string average = Path.Combine(root, Movie.ToAveragePath(alias)), thumbnail = Path.Combine(root, Movie.ToThumbnailsPath(alias));
            try
            {
                var dims = MapHeader.ReadFromFile(average).Dimensions;
                positions.TryGetValue(Path.GetFileNameWithoutExtension(alias), out var p);
                samples.Add(new(thumbnail, dims.X, dims.Y, p.X?.ToArray() ?? [], p.Y?.ToArray() ?? []));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) { }
        }
        return new(manifest.MovieCount, manifest.ParticleCount, Median(items.Select(i => i.Resolution)), Median(items.Select(i => i.Motion)),
            defoci.Length > 0 ? defoci.Min() : null, defoci.Length > 0 ? defoci.Max() : null, samples.ToArray());
    }
}

/// <param name="Thumbnail">The micrograph's thumbnail; card.json saves it relative to the space.</param>
/// <param name="Width">The average's width in pixels, the unit of <paramref name="X"/>.</param>
public sealed record WarpExportCardSample(string Thumbnail, int Width, int Height, float[] X, float[] Y);
