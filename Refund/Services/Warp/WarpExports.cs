using System.Globalization;
using System.Text;
using System.Text.Json;
using Warp;
using Warp.Tools;

namespace Refund.Services.Warp;

public sealed partial class WarpSession
{
    // The facade retains this shared lease until its export job has also been queued.
    // Direct callers retain the same API and scope their lease to compilation.
    internal sealed class ExportOperation(WarpSession owner) : IDisposable
    {
        private WarpSession _owner=owner;
        internal bool BelongsTo(WarpSession session)=>ReferenceEquals(_owner,session);
        public void Dispose()
        {
            var session=Interlocked.Exchange(ref _owner,null);
            if(session==null)return;
            lock(session._sync)session.Exporting=false;
            session.Publish();
        }
    }
    internal ExportOperation BeginExportOperation()
    {
        lock(_sync)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(WarpSession));
            if(Exporting)throw new InvalidOperationException("An export is already being compiled.");
            Exporting=true;
        }
        Publish();return new(this);
    }
    public async Task<WarpExportManifest> ExportAsync(bool particles)
    {
        using var operation=BeginExportOperation();
        return await ExportAsync(particles,operation);
    }
    internal async Task<WarpExportManifest> ExportAsync(bool particles,ExportOperation operation)
    {
        WarpMovie[] selected;
        WarpSettings settings;
        WarpFilters filters;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WarpSession));
            if (!operation.BelongsTo(this)) throw new InvalidOperationException("The export operation lease is no longer active.");
            selected = _movies.Where(m => Passes(m) && Current(m, "motion") && Current(m, "average") && Current(m, "ctf") &&
                (!particles || Current(m, "pick") && Current(m, "extract")))
                .Select(m => m with { Completed = new Dictionary<string, ulong>(m.Completed) }).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException(particles
                ? "No accepted movies have current extracted particles to export." : "No accepted movies have current motion and CTF results to export.");
            settings = Settings; filters = _filters;
        }
        // Selection, limits and settings are frozen above; worker files are copied as they exist now.
        var result = await Task.Run(() => CompileExport(selected, settings, filters, particles));
        lock (_sync) { _exports.Add(result); Save(); }
        await SaveAsync();
        return result;
    }

    private WarpExportManifest CompileExport(WarpMovie[] selected, WarpSettings settings,
        WarpFilters filters, bool particles)
    {
        string root = Path.Combine(Identity.SessionDirectory, "exports", Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "input");
        Directory.CreateDirectory(data);
        string starPath = Path.Combine(root, particles ? "particles.star" : "micrographs.star");
        string jsonPath = Path.Combine(root, "processed_items.json");
        long count = 0;
        using var star = new StreamWriter(starPath, false, new UTF8Encoding(false));
        using var jsonStream = File.Create(jsonPath);
        using var mini = new Utf8JsonWriter(jsonStream);
        string[] columns = particles
            ? ["rlnCoordinateX", "rlnCoordinateY", "rlnAutopickFigureOfMerit", "wrpAutopickPower", "rlnImageName", "rlnMicrographName", "rlnDefocusU", "rlnDefocusV", "rlnDefocusAngle", "rlnPhaseShift", "rlnVoltage", "rlnSphericalAberration", "rlnAmplitudeContrast", "rlnImagePixelSize", "rlnMicrographPixelSize", "rlnCtfMaxResolution", "rlnDetectorPixelSize", "rlnMagnification"]
            : ["rlnMicrographName", "rlnDefocusU", "rlnDefocusV", "rlnDefocusAngle", "rlnPhaseShift", "rlnVoltage", "rlnSphericalAberration", "rlnAmplitudeContrast", "rlnMicrographPixelSize", "rlnCtfMaxResolution", "rlnDetectorPixelSize", "rlnMagnification"];
        star.WriteLine(particles ? "data_particles\n\nloop_" : "data_micrographs\n\nloop_");
        for (int c = 0; c < columns.Length; c++) star.WriteLine($"_{columns[c]} #{c + 1}");
        mini.WriteStartArray();
        foreach (var row in selected)
        {
            if (!File.Exists(row.MetadataPath) || !File.Exists(row.AveragePath))
                throw new IOException($"Results for {row.Name} are no longer available.");
            string alias = Path.GetFileName(row.ProcessingPath);
            string xmlPath = Path.Combine(root, Movie.ToXMLPath(alias));
            File.Copy(row.MetadataPath, xmlPath);
            LinkIfPresent(Path.Combine(data, alias), row.InputPath);
            var metadata = new Movie(Path.Combine(root, alias), data, []);
            metadata.SaveMeta();
            if (metadata.CTF == null) throw new InvalidDataException($"CTF metadata is missing for {row.Name}.");
            LinkIfPresent(Path.Combine(root, Movie.ToAveragePath(alias)), row.AveragePath);
            LinkIfPresent(Path.Combine(root, Movie.ToPowerSpectrumPath(alias)), Path.Combine(settings.OutputPath, Movie.ToPowerSpectrumPath(alias)));
            LinkIfPresent(Path.Combine(root, Movie.ToAverageOddPath(alias)), Path.Combine(settings.OutputPath, Movie.ToAverageOddPath(alias)));
            LinkIfPresent(Path.Combine(root, Movie.ToAverageEvenPath(alias)), Path.Combine(settings.OutputPath, Movie.ToAverageEvenPath(alias)));
            if (settings.Denoise) LinkIfPresent(Path.Combine(root, Movie.ToAverageDenoisedPath(alias)), row.EnhancedPath);
            LinkIfPresent(Path.Combine(root, Movie.ToDeconvolvedPath(alias)), row.DeconvolvedPath);
            LinkIfPresent(Path.Combine(root, Movie.ToDenoiseTrainingOddPath(alias)), row.TrainingOddPath);
            LinkIfPresent(Path.Combine(root, Movie.ToDenoiseTrainingEvenPath(alias)), row.TrainingEvenPath);
            LinkIfPresent(Path.Combine(root, Movie.ToThumbnailsPath(alias)), row.ThumbnailPath);
            var summary = metadata.ToMiniJson();
            summary["Particles"] = row.Picks;
            summary.WriteTo(mini);
            string micrographPath = Path.Combine(root, Movie.ToAveragePath(alias));
            var ctf = metadata.CTF;
            double halfAstig = (double)ctf.DefocusDelta / 2;
            double phase = (double)ctf.PhaseShift * 180;
            string[] ctfTail = [Number(ctf.DefocusAngle), Number(phase), Number(settings.Voltage), Number(settings.Cs), Number(settings.Amplitude)];
            if (!particles)
            {
                star.WriteLine(string.Join(" ", new[] { StarToken(micrographPath), Number(((double)ctf.Defocus + halfAstig) * 1e4), Number(((double)ctf.Defocus - halfAstig) * 1e4) }
                    .Concat(ctfTail).Concat([Number(settings.TargetPixelSize), Number(metadata.CTFResolutionEstimate), Number(settings.TargetPixelSize), "10000"])));
                continue;
            }
            var picks = ReadParticles(row.ParticleStarPath);
            if (picks.Length > 0 && !File.Exists(row.ParticleStackPath))
                throw new IOException($"Extracted particles for {row.Name} are no longer available.");
            var dimensions = metadata.OptionsCTF?.Dimensions ?? new float3(0);
            foreach (var pick in picks)
            {
                if (pick.Correlation < filters.MinPickCorrelation || pick.Correlation > filters.MaxPickCorrelation ||
                    pick.Power < filters.MinPickPower || filters.MaxPickPower.HasValue && pick.Power > filters.MaxPickPower.Value) continue;
                double xAngstrom = pick.X * row.EnhancedPixelSize, yAngstrom = pick.Y * row.EnhancedPixelSize;
                double defocus = dimensions.X > 0 && dimensions.Y > 0 && metadata.GridCTFDefocus != null
                    ? metadata.GridCTFDefocus.GetInterpolated(new float3((float)(xAngstrom / dimensions.X), (float)(yAngstrom / dimensions.Y), .5f)) : (double)ctf.Defocus;
                star.WriteLine(string.Join(" ", new[] { Number(xAngstrom / (double)settings.TargetPixelSize), Number(yAngstrom / (double)settings.TargetPixelSize), Number(pick.Correlation), Number(pick.Power),
                    StarToken($"{pick.RowIndex + 1:D6}@{row.ParticleStackPath}"), StarToken(micrographPath), Number((defocus + halfAstig) * 1e4), Number((defocus - halfAstig) * 1e4) }
                    .Concat(ctfTail).Concat([Number(settings.TargetPixelSize), Number(settings.TargetPixelSize), Number(metadata.CTFResolutionEstimate), Number(settings.TargetPixelSize), "10000"])));
                count++;
            }
        }
        mini.WriteEndArray(); mini.Flush(); star.Flush();
        return new(starPath, particles, selected.Length, count, settings, Identity.SessionDirectory, DateTime.UtcNow, jsonPath, particles, root, data);
    }

    private static string Number<T>(T value) where T : IConvertible => value.ToString(CultureInfo.InvariantCulture);
    private static void LinkIfPresent(string link, string target)
    {
        if (string.IsNullOrEmpty(target) || !File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, Path.GetFullPath(target));
    }
    private static string StarToken(string value) => Star.QuoteValue(value);
}
