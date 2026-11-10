using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.JobResources;
using Refund.Services.WarpLive;
using Refund.Utils;
using Warp;
using Warp.Tools;

namespace Refund.Jobs.Fs.WarpLive;

/// <summary>The selection is captured at request time; ordinary Relay execution materializes its export.</summary>
[GenerateReadOnly]
public abstract class WarpLiveExportJob : Job, ILocalJob
{
    protected WarpLiveExportJob() => PortsIn = new(new Dictionary<string, PortIn>());

    [RelayProperty] public int SourceLiveJobId { get; set; }
    [RelayProperty] public WarpLiveExportManifest Manifest { get; set; }
    public override int2 CardSquareCount { get; set; } = new(2, 1);
    public override JobQueueType QueueType => JobQueueType.Local;
    public override int GpuCount => 0;
    public override string NameStdOut => Path.GetRelativePath(DirectoryPath, LifecycleFilePath);
    public override Type ExpandedViewType => null;
    public override string TypeDescription => "A captured selection exported from a Warp Live session";
    public string StarPath => Path.Combine(DirectoryPath, Manifest?.Particles == true ? "particles.star" : "micrographs.star");
    public string ProcessedItemsJson => Path.Combine(DirectoryPath, "processed_items.json");

    public override Dictionary<string, string> ValidateInputs() => Manifest == null
        ? new() { [nameof(Manifest)] = "Create an export from a Warp Live session." } : new();

    public void RunLocal(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Manifest == null) throw new InvalidOperationException("No captured export was supplied.");
        File.Copy(Manifest.StarPath, StarPath, overwrite: true);
        token.ThrowIfCancellationRequested();
        File.Copy(Manifest.ProcessedItemsJson, ProcessedItemsJson, overwrite: true);
    }

    protected MicrographSet Micrographs()
    {
        if (Manifest == null) return null;
        var s = Manifest.Settings;
        string root = Manifest.ProcessingDirectory;
        return new ExportedMicrographSet(StarPath)
        {
            DataSetFs = new DataSetFs
            {
                DataDirectory = Manifest.DataDirectory, FileSearchPattern = s.InputPattern,
                DoRecursiveSearch = false, PixelSize = s.Pixel,
                BinFactor = s.TargetPixelSize / s.Pixel, OverallExposure = s.TotalDose,
                Voltage = s.Voltage, Cs = s.Cs, AmplitudeContrast = s.Amplitude,
                EerFrames = s.EerGrouping, GainPath = s.CorrectGain ? s.GainPath : "",
                DefectsPath = s.CorrectDefects ? s.DefectsPath : "",
                GainFlipX = s.GainFlipX, GainFlipY = s.GainFlipY, GainTranspose = s.GainTranspose
            },
            HasMovies = true, HasMotion = true, HasCtf = true, HasMetadata = true,
            ItemCount = Manifest.MovieCount, ProcessedItemsJson = Manifest.ProcessedItemsJson,
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
            [new Downloadable("Micrographs", "Captured Warp Live micrograph STAR", starPath)];
    }
}

[GenerateReadOnly]
[HideFromMenu]
public class WarpLiveMicrographsExportJob : WarpLiveExportJob
{
    public override string TypeGuid => "6504c297-2299-4632-893b-1b81274cd1ae";
    public override string TypeCategory => "Frame-series.Export.Warp Live micrographs";
    public override string TypeName => "Warp Live micrographs";
    public override string TypeNameShort => "Live micrographs";
    public WarpLiveMicrographsExportJob()
    {
        var output = new PortOut(this, typeof(MicrographSet), "Micrographs", "Micrographs", _ => Micrographs());
        PortsOut = new(new Dictionary<string, PortOut> { [output.Name] = output });
    }
}

[GenerateReadOnly]
[HideFromMenu]
public class WarpLiveParticlesExportJob : WarpLiveExportJob
{
    public override string TypeGuid => "8eca0b85-72e4-45e5-b2e9-380c7baf95f0";
    public override string TypeCategory => "Frame-series.Export.Warp Live particles";
    public override string TypeName => "Warp Live particles";
    public override string TypeNameShort => "Live particles";
    public WarpLiveParticlesExportJob()
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
