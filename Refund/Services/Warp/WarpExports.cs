using System.Globalization;
using System.Text;
using System.Text.Json;
using Warp;
using Warp.Tools;

namespace Refund.Services.Warp;

public sealed partial class WarpSession
{
    // Each export job holds a lease while it writes, so the session shows the work; several may run at once.
    internal sealed class ExportOperation(WarpSession owner) : IDisposable
    {
        private WarpSession _owner=owner;
        internal bool BelongsTo(WarpSession session)=>ReferenceEquals(_owner,session);
        public void Dispose()
        {
            var session=Interlocked.Exchange(ref _owner,null);
            if(session==null)return;
            lock(session._sync)session._exportOperations--;
            session.Publish();
        }
    }
    internal ExportOperation BeginExportOperation()
    {
        lock(_sync)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(WarpSession));
            _exportOperations++;
        }
        Publish();return new(this);
    }

    public static string NothingToExport(bool particles)=>particles
        ? "No accepted movies have current extracted particles to export." : "No accepted movies have current motion and CTF results to export.";
    private bool Exportable(WarpMovie m,bool particles)=>Passes(m)&&Current(m,"motion")&&Current(m,"average")&&Current(m,"ctf")&&
        (!particles||Current(m,"pick")&&Current(m,"extract"));
    /// <summary>Movies an export would take now.</summary>
    public int CountExportable(bool particles)=>ExportCounts(particles).Movies;
    /// <summary>
    /// What an export would take now: accepted movies with the results it needs (motion and CTF; for particles also
    /// current picking and extraction), and their particles. Unlike <see cref="Accepted"/>, a movie outdated only in
    /// extraction still counts for micrographs.
    /// </summary>
    public (int Movies, long Particles) ExportCounts(bool particles)
    {
        lock(_sync)
        {
            int movies=0;long count=0;
            foreach(var m in _movies)if(Exportable(m,particles)){movies++;count+=m.Particles;}
            return (movies,count);
        }
    }

    /// <summary>
    /// Writes the accepted selection as it is now into <paramref name="directory"/>: particles.star or micrographs.star,
    /// processed_items.json and movies.txt, replacing a previous export there. Their paths are relative to
    /// <paramref name="spaceRoot"/>, which jobs run in, so they hold when the space moves; the returned manifest's are absolute.
    /// </summary>
    public async Task<WarpExportManifest> ExportAsync(bool particles,string directory,string spaceRoot,CancellationToken token=default)
    {
        using var operation=BeginExportOperation();
        return await ExportAsync(particles,directory,spaceRoot,operation,token);
    }
    internal async Task<WarpExportManifest> ExportAsync(bool particles,string directory,string spaceRoot,ExportOperation operation,CancellationToken token)
    {
        WarpMovie[] selected;
        WarpSettings settings;
        WarpFilters filters;
        (string InputPath, WarpBeamShift BeamShift)[] all;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WarpSession));
            if (!operation.BelongsTo(this)) throw new InvalidOperationException("The export operation lease is no longer active.");
            selected = _movies.Where(m => Exportable(m, particles)).Select(m => m with { Completed = new Dictionary<string, ulong>(m.Completed) }).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException(NothingToExport(particles));
            settings = Settings; filters = _filters;
            all = _movies.Select(m => (m.InputPath, m.BeamShift)).ToArray();
        }
        // Selection, settings and filters are frozen above; worker files are read as they exist now.
        var found = new Dictionary<int, WarpBeamShift>();
        var manifest = await Task.Run(() => CompileExport(selected, settings, filters, particles, directory, spaceRoot, all, found, token), token);
        // Beam shifts read for this export are kept, so later exports read only movies new since.
        if (found.Count > 0) Mutate(() => { foreach (var (index, shift) in found) if (index < _movies.Count && _movies[index].BeamShift == null) { _movies[index].BeamShift = shift; _changedRows.Add(index); } });
        return manifest;
    }

    /// <summary>The list of an export's movies, one path per line, beside its STAR.</summary>
    internal const string MoviesListName = "movies.txt";
    /// <summary>The optics group of every row when groups do not come from EPU.</summary>
    internal const string OpticsGroupName = "opticsGroup1";
    /// <summary>The optics group of micrographs without EPU metadata, when the others are grouped by beam shift.</summary>
    internal const string NoEpuGroupName = "noEpuMetadata";

    /// <summary>
    /// Each session movie's EPU optics group (<see cref="WarpEpu.Group"/>) by index, reading beam shifts not read before
    /// into <paramref name="found"/>; null when EPU grouping is off. Groups are formed over every movie the session
    /// received, so a group's number does not depend on which movies an export takes.
    /// </summary>
    private static int[] EpuGroups(WarpSettings settings, (string InputPath, WarpBeamShift BeamShift)[] all, Dictionary<int, WarpBeamShift> found, CancellationToken token)
    {
        if (!settings.EpuOpticsGroups) return null;
        var finder = new WarpEpu.Finder(settings.EpuMetadataPath);
        var shifts = new WarpBeamShift[all.Length];
        for (int i = 0; i < all.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            shifts[i] = all[i].BeamShift;
            if (shifts[i] == null && finder.Find(all[i].InputPath) is { } xml && WarpEpu.Read(xml) is { } shift) found[i] = shifts[i] = shift;
        }
        return WarpEpu.Group(shifts, (double)settings.EpuGroupRadius);
    }

    /// <summary>
    /// Writes a RELION 3.1+ STAR (optics table, then particles or micrographs), processed_items.json and movies.txt,
    /// all referring to the session's own files: its averages, particle stacks, metadata and links to the raw movies.
    /// Nothing is copied or linked, so an export is three files however many movies it holds. movies.txt is the
    /// selection for WarpTools jobs downstream (--input_data), which would otherwise take every movie in the session.
    /// Paths are relative to <paramref name="spaceRoot"/>, the directory RELION and WarpTools jobs run in.
    /// </summary>
    private WarpExportManifest CompileExport(WarpMovie[] selected, WarpSettings settings, WarpFilters filters, bool particles, string directory,
        string spaceRoot, (string InputPath, WarpBeamShift BeamShift)[] all, Dictionary<int, WarpBeamShift> found, CancellationToken token)
    {
        string Relative(string path) => WarpPaths.Relative(spaceRoot, path);
        // RELION numbers optics groups 1..n in table order: the export's groups, in session order, then those without EPU metadata.
        int[] epu = EpuGroups(settings, all, found, token);
        int GroupOf(WarpMovie row) => epu != null && row.Index < epu.Length ? epu[row.Index] : 0;
        int[] present = selected.Select(GroupOf).Distinct().OrderBy(g => g == 0 ? int.MaxValue : g).ToArray();
        bool fromEpu = present.Any(g => g > 0);
        var number = fromEpu ? present.Select((g, i) => (g, i)).ToDictionary(p => p.g, p => p.i + 1) : null;
        string NumberOf(WarpMovie row) => fromEpu ? number[GroupOf(row)].ToString(CultureInfo.InvariantCulture) : "1";
        var groups = fromEpu ? present.Select(g => g == 0 ? NoEpuGroupName : $"beamShift{g}").ToArray() : [OpticsGroupName];
        int withoutMetadata = epu == null ? 0 : selected.Count(row => GroupOf(row) == 0);
        Directory.CreateDirectory(directory);
        // The session's processing folder: every movie's metadata, averages and stacks, and its links to the raw movies.
        string processing = Path.GetDirectoryName(selected[0].ProcessingPath)!, data = Path.Combine(processing, "input");
        string starPath = Path.Combine(directory, particles ? "particles.star" : "micrographs.star");
        string jsonPath = Path.Combine(directory, "processed_items.json"), listPath = Path.Combine(directory, MoviesListName);
        long count = 0;
        using var list = new StreamWriter(listPath, false, new UTF8Encoding(false));
        using var star = new StreamWriter(starPath, false, new UTF8Encoding(false));
        using var jsonStream = File.Create(jsonPath);
        using var mini = new Utf8JsonWriter(jsonStream);
        void Table(string name, string[] columns)
        {
            star.WriteLine($"data_{name}\n\nloop_");
            for (int c = 0; c < columns.Length; c++) star.WriteLine($"_{columns[c]} #{c + 1}");
        }
        // Coordinates are in micrograph pixels (rlnMicrographPixelSize); particle images are at rlnImagePixelSize.
        string[] optics = particles
            ? ["rlnOpticsGroup", "rlnOpticsGroupName", "rlnMicrographOriginalPixelSize", "rlnVoltage", "rlnSphericalAberration", "rlnAmplitudeContrast", "rlnMicrographPixelSize", "rlnImagePixelSize", "rlnImageSize", "rlnImageDimensionality", "rlnCtfDataAreCtfPremultiplied"]
            : ["rlnOpticsGroup", "rlnOpticsGroupName", "rlnMicrographOriginalPixelSize", "rlnVoltage", "rlnSphericalAberration", "rlnAmplitudeContrast", "rlnMicrographPixelSize"];
        Table("optics", optics);
        for (int g = 0; g < groups.Length; g++)
        {
            string[] group = [ Number(g + 1), groups[g], Number(settings.Pixel), Number(settings.Voltage), Number(settings.Cs), Number(settings.Amplitude), Number(settings.TargetPixelSize) ];
            star.WriteLine(string.Join(" ", particles ? group.Concat([Number(settings.ParticlePixelSize), Number(settings.ParticleBox), "2", "0"]) : group));
        }
        star.WriteLine();
        if (particles) Table("particles", ["rlnCoordinateX", "rlnCoordinateY", "rlnAutopickFigureOfMerit", "wrpAutopickPower", "rlnImageName", "rlnMicrographName", "rlnOpticsGroup", "rlnDefocusU", "rlnDefocusV", "rlnDefocusAngle", "rlnPhaseShift", "rlnCtfMaxResolution"]);
        else Table("micrographs", ["rlnMicrographName", "rlnOpticsGroup", "rlnDefocusU", "rlnDefocusV", "rlnDefocusAngle", "rlnPhaseShift", "rlnCtfMaxResolution"]);
        mini.WriteStartArray();
        foreach (var row in selected)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(row.MetadataPath) || !File.Exists(row.AveragePath))
                throw new IOException($"Results for {row.Name} are no longer available.");
            var metadata = new Movie(row.ProcessingPath, null, []);
            if (metadata.CTF == null) throw new InvalidDataException($"CTF metadata is missing for {row.Name}.");
            var summary = metadata.ToMiniJson();
            summary["Particles"] = row.Particles;
            summary.WriteTo(mini);
            list.WriteLine(Relative(Path.Combine(data, Path.GetFileName(row.ProcessingPath))));
            var ctf = metadata.CTF;
            double halfAstig = (double)ctf.DefocusDelta / 2;
            string[] ctfTail = [Number(ctf.DefocusAngle), Number((double)ctf.PhaseShift * 180), Number(metadata.CTFResolutionEstimate)];
            if (!particles)
            {
                star.WriteLine(string.Join(" ", new[] { StarToken(Relative(row.AveragePath)), NumberOf(row), Number(((double)ctf.Defocus + halfAstig) * 1e4), Number(((double)ctf.Defocus - halfAstig) * 1e4) }.Concat(ctfTail)));
                continue;
            }
            // Extraction kept only the picks within the score bounds; its list is in the stack's order.
            var picks = row.Particles > 0 ? ReadParticles(row.ExtractedStarPath) : [];
            if (picks.Length > 0 && !File.Exists(row.ParticleStackPath))
                throw new IOException($"Extracted particles for {row.Name} are no longer available.");
            var dimensions = metadata.OptionsCTF?.Dimensions ?? new float3(0);
            // A particle names its movie as Warp does: the movie's name in the processing folder, beside the metadata
            // (the same name ending in .xml), which leads to the average (average/) and the raw movie (its data folder).
            string stack = Relative(row.ParticleStackPath), movie = StarToken(Relative(row.ProcessingPath));
            foreach (var pick in picks)
            {
                double xAngstrom = pick.X * row.EnhancedPixelSize, yAngstrom = pick.Y * row.EnhancedPixelSize;
                double defocus = dimensions.X > 0 && dimensions.Y > 0 && metadata.GridCTFDefocus != null
                    ? metadata.GridCTFDefocus.GetInterpolated(new float3((float)(xAngstrom / dimensions.X), (float)(yAngstrom / dimensions.Y), .5f)) : (double)ctf.Defocus;
                star.WriteLine(string.Join(" ", new[] { Number(xAngstrom / (double)settings.TargetPixelSize), Number(yAngstrom / (double)settings.TargetPixelSize), Number(pick.Correlation), Number(pick.Power),
                    StarToken($"{pick.RowIndex + 1:D6}@{stack}"), movie, NumberOf(row), Number((defocus + halfAstig) * 1e4), Number((defocus - halfAstig) * 1e4) }
                    .Concat(ctfTail)));
                count++;
            }
        }
        mini.WriteEndArray(); mini.Flush(); star.Flush(); list.Flush();
        return new(starPath, particles, selected.Length, count, settings, Identity.SessionDirectory, DateTime.UtcNow, jsonPath, particles, processing, data, filters, listPath,
            new(fromEpu, groups.Length, withoutMetadata));
    }

    private static string Number<T>(T value) where T : IConvertible => value.ToString(CultureInfo.InvariantCulture);
    private static string StarToken(string value) => Star.QuoteValue(value);
}
