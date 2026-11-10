using System.Globalization;
using System.Buffers.Binary;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ComponentDev.Services;

public readonly record struct PreviewParticleMetric(double X, double Y, double Correlation, double Power);

// Bins are row-major: power increases with row; correlation increases with column.
// The snapshot is shared centrally and its bins must be treated as read-only by views.
public sealed record PreviewParticleHistogram(int[] Bins, int Columns, int Rows, double PowerMaximum,
    long Total, long Accepted, int Movies);

// Contributions are runtime-only. The checkpoint contains one global aggregate.
internal sealed record PreviewParticleBins(int[] Indices, int[] Counts, int Total);
public sealed record PreviewParticleHistogramCheckpoint(int Schema, PreviewParticleHistogram Histogram,
    string SettingsSignature = null, string CohortSignature = null, string SourceSignature = null);

public sealed partial class WarpSessionPreview
{
    public double MinPickCorrelation { get; private set; }
    public double MaxPickCorrelation { get; private set; } = 1;
    public double MinPickPower { get; private set; }
    public double? MaxPickPower { get; private set; }

    private PreviewParticleMetric[][] _fixtureParticleMetrics;
    private PreviewParticleHistogram _particleHistogram;
    private PreviewParticleBins[] _particleContributions;
    private string _particleSourceSignature;
    private string _histogramSettingsSignature;
    private PreviewSettings _signatureSettings;
    private int _signatureModel = -1;
    private string _currentSettingsSignature;
    private int[] _histogramCohort;
    private int _particleFilterRevision;
    private int _histogramFilterRevision = -1;
    private double _particlePowerMaximum;
    private readonly Dictionary<int, (int Revision, string Path)> _filteredParticleStars = [];

    public void SetParticleRange(double minCorrelation, double maxCorrelation, double minPower, double? maxPower)
    {
        if (!double.IsFinite(minCorrelation) || !double.IsFinite(maxCorrelation) || !double.IsFinite(minPower) ||
            maxPower.HasValue && !double.IsFinite(maxPower.Value)) return;
        Change(() => {
            RestoreParticleFilters(minCorrelation, maxCorrelation, minPower, maxPower);
            _particleFilterRevision++;
        });
    }

    public string GetFilteredParticleStar(int movieIndex)
    {
        lock (_sync)
        {
            int variant = Math.Abs(movieIndex % WarpPreviewFixtures.Count);
            EnsureParticleVariant(variant);
            if (_filteredParticleStars.TryGetValue(variant, out var cached) && cached.Revision == _particleFilterRevision) return cached.Path;
            string directory = Path.Combine(Root, "particle-overlays");
            Directory.CreateDirectory(directory);
            string filterKey = FormattableString.Invariant($"v1|{MinPickCorrelation:R}|{MaxPickCorrelation:R}|{MinPickPower:R}|{MaxPickPower:R}");
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filterKey))).ToLowerInvariant()[..16];
            string path = Path.Combine(directory, $"picks-{variant:D2}-{hash}.star");
            using (var writer = new StreamWriter(path))
            {
                writer.WriteLine("data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnAutopickFigureOfMerit #3\n_wrpAutopickPower #4");
                foreach (var particle in _fixtureParticleMetrics[variant])
                    if (PassesParticleFilter(particle)) writer.WriteLine(FormattableString.Invariant($"{particle.X:G9} {particle.Y:G9} {particle.Correlation:G9} {particle.Power:G9}"));
            }
            _filteredParticleStars[variant] = (_particleFilterRevision, path);
            return path;
        }
    }

    // Extraction need not have finished; picks must exist and no required processing
    // may be stale, including disabled stages. QC/manual exclusion does not shrink this overview.
    public bool HasCurrentParticleMetrics(PreviewMovie movie) =>
        !movie.Failed && movie.Done.HasFlag(PreviewStage.Pick) && movie.Stale == PreviewStage.None;

    public IReadOnlyList<PreviewParticleMetric> GetParticleMetrics(int movieIndex)
    {
        lock (_sync)
        {
            int variant = Math.Abs(movieIndex % WarpPreviewFixtures.Count);
            EnsureParticleVariant(variant);
            return _fixtureParticleMetrics[variant];
        }
    }

    public bool PassesParticleFilter(PreviewParticleMetric particle) =>
        particle.Correlation >= MinPickCorrelation && particle.Correlation <= MaxPickCorrelation &&
        particle.Power >= MinPickPower && (!MaxPickPower.HasValue || particle.Power <= MaxPickPower.Value);

    public void FilterParticleMetric(string metric, double? value)
    {
        if (value.HasValue && !double.IsFinite(value.Value)) return;
        if (metric is not ("correlation-min" or "correlation-max" or "power-min" or "power-max")) return;
        Change(() => {
            switch (metric)
            {
                case "correlation-min": MinPickCorrelation = Math.Clamp(value ?? 0, 0, MaxPickCorrelation); break;
                case "correlation-max": MaxPickCorrelation = Math.Clamp(value ?? 1, MinPickCorrelation, 1); break;
                case "power-min": MinPickPower = Math.Clamp(value ?? 0, 0, MaxPickPower ?? double.MaxValue); break;
                case "power-max": MaxPickPower = value.HasValue ? Math.Max(MinPickPower, value.Value) : null; break;
            }
            _particleFilterRevision++;
        });
    }

    public PreviewParticleHistogram GetParticleHistogram()
    {
        lock (_sync)
        {
            string settingsSignature = ParticleSettingsSignature();
            if (_histogramSettingsSignature != null && _histogramSettingsSignature != settingsSignature)
            {
                // An applied processing configuration/model changes the aggregate's meaning.
                // Rebuild once; do not retain per-movie histogram state across this boundary.
                _particleHistogram = null;
                _histogramCohort = null;
                _particleContributions = null;
                _fixtureParticleMetrics = null;
                _particleSourceSignature = null;
                _filteredParticleStars.Clear();
            }
            _histogramSettingsSignature = settingsSignature;
            var cohort = CurrentParticleCohort();
            bool sameCohort = _particleHistogram != null && cohort.AsSpan().SequenceEqual(_histogramCohort);
            bool sameFilter = _histogramFilterRevision == _particleFilterRevision;
            if (sameCohort && sameFilter) return _particleHistogram;

            const int columns = 64, rows = 48;
            if (_particleHistogram == null) EnsureParticleMetrics();
            _particleContributions ??= new PreviewParticleBins[WarpPreviewFixtures.Count];
            var bins = sameCohort ? _particleHistogram.Bins : _particleHistogram?.Bins.ToArray() ?? new int[columns * rows];
            long total = _particleHistogram?.Total ?? 0;
            long accepted = sameFilter ? _particleHistogram?.Accepted ?? 0 : 0;
            int movies = 0;
            for (int variant = 0; variant < cohort.Length; variant++)
            {
                int multiplicity = cohort[variant];
                int delta = multiplicity - (_histogramCohort?[variant] ?? 0);
                if (delta != 0)
                {
                    // After a warm restore, read only a newly changed result's fixture.
                    // The historical global bins remain usable without loading old STARs.
                    var contribution = GetParticleContribution(variant, columns, rows);
                    for (int i = 0; i < contribution.Indices.Length; i++)
                        bins[contribution.Indices[i]] += contribution.Counts[i] * delta;
                    total += (long)contribution.Total * delta;
                }
                if ((!sameFilter && multiplicity > 0) || (sameFilter && delta != 0))
                {
                    EnsureParticleVariant(variant);
                    int kept = _fixtureParticleMetrics[variant].Count(PassesParticleFilter);
                    accepted += (long)kept * (sameFilter ? delta : multiplicity);
                }
                movies += multiplicity;
            }
            _histogramCohort = cohort;
            _histogramFilterRevision = _particleFilterRevision;
            return _particleHistogram = new(bins, columns, rows, _particlePowerMaximum, total, accepted, movies);
        }
    }

    private PreviewParticleBins GetParticleContribution(int variant, int columns, int rows)
    {
        if (_particleContributions[variant] is { } cached) return cached;
        EnsureParticleVariant(variant);
        var counts = new SortedDictionary<int, int>();
        foreach (var particle in _fixtureParticleMetrics[variant])
        {
            int x = Math.Clamp((int)(particle.Correlation * columns), 0, columns - 1);
            int y = Math.Clamp((int)(particle.Power / _particlePowerMaximum * rows), 0, rows - 1);
            int bin = y * columns + x;
            counts[bin] = counts.GetValueOrDefault(bin) + 1;
        }
        return _particleContributions[variant] = new(counts.Keys.ToArray(), counts.Values.ToArray(), _fixtureParticleMetrics[variant].Length);
    }

    private int[] CurrentParticleCohort()
    {
        var cohort = new int[WarpPreviewFixtures.Count];
        foreach (var movie in Movies)
            if (HasCurrentParticleMetrics(movie)) cohort[movie.Index % WarpPreviewFixtures.Count]++;
        return cohort;
    }

    private static string ParticleSignature(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string ParticleSettingsSignature()
    {
        if (!ReferenceEquals(_signatureSettings, Settings) || _signatureModel != Model)
        {
            _signatureSettings = Settings;
            _signatureModel = Model;
            _currentSettingsSignature = ParticleSignature(JsonSerializer.Serialize(Settings) + "|model:" + Model.ToString(CultureInfo.InvariantCulture));
        }
        return _currentSettingsSignature;
    }
    private string ParticleCohortSignature()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> index = stackalloc byte[4];
        foreach (var movie in Movies)
            if (HasCurrentParticleMetrics(movie))
            {
                BinaryPrimitives.WriteInt32LittleEndian(index, movie.Index);
                hash.AppendData(index);
            }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private string ParticleSourceSignature() => ParticleSignature(string.Join("|", Enumerable.Range(0, WarpPreviewFixtures.Count).Select(i =>
    {
        var file = new FileInfo(Fixtures.Particles(i));
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
    })));

    private PreviewParticleHistogramCheckpoint CaptureParticleHistogram() =>
        new(2, GetParticleHistogram(), ParticleSettingsSignature(), ParticleCohortSignature(), _particleSourceSignature ??= ParticleSourceSignature());

    private void RestoreParticleHistogram(PreviewParticleHistogramCheckpoint saved)
    {
        // Schema 1 persisted per-result contributions. Ignore that cache and replace it
        // once with schema 2; a valid schema 2 restore never reads particle STAR content.
        if (saved?.Schema != 2 || saved.Histogram is not { Columns: 64, Rows: 48 } histogram ||
            histogram.Bins?.Length != 64 * 48 || !double.IsFinite(histogram.PowerMaximum) || histogram.PowerMaximum <= 0 ||
            histogram.Bins.Any(n => n < 0) || histogram.Total < 0 || histogram.Accepted < 0 || histogram.Accepted > histogram.Total ||
            histogram.Bins.Sum(n => (long)n) != histogram.Total || saved.SettingsSignature != ParticleSettingsSignature() ||
            saved.CohortSignature != ParticleCohortSignature() || saved.SourceSignature != ParticleSourceSignature()) return;
        var cohort = CurrentParticleCohort();
        if (cohort.Sum() != histogram.Movies) return;
        _particleHistogram = histogram;
        _histogramCohort = cohort;
        _particleContributions = null;
        _fixtureParticleMetrics = null;
        _particleSourceSignature = saved.SourceSignature;
        _histogramSettingsSignature = saved.SettingsSignature;
        _particlePowerMaximum = histogram.PowerMaximum;
        _histogramFilterRevision = _particleFilterRevision;
    }

    private void EnsureParticleMetrics()
    {
        double maximumPower = 0;
        for (int variant = 0; variant < WarpPreviewFixtures.Count; variant++)
        {
            EnsureParticleVariant(variant);
            foreach (var particle in _fixtureParticleMetrics[variant]) maximumPower = Math.Max(maximumPower, particle.Power);
        }
        _particlePowerMaximum = Math.Max(100, Math.Ceiling(maximumPower / 100) * 100);
    }

    private void EnsureParticleVariant(int variant)
    {
        _fixtureParticleMetrics ??= new PreviewParticleMetric[WarpPreviewFixtures.Count][];
        if (_fixtureParticleMetrics[variant] != null) return;
        var random = new Random(7331 + variant * 101);
        var particles = new List<PreviewParticleMetric>();
        foreach (var line in File.ReadLines(Fixtures.Particles(variant)))
        {
            var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)) continue;
            // Synthetic metrics on every real fixture coordinate. These model positive
            // Pearson scores and local intensity variance, not calibrated confidence.
            double a = random.NextDouble(), b = random.NextDouble(), c = random.NextDouble();
            bool weak = particles.Count % 7 == 0;
            double correlation = weak ? .03 + .35 * a : .28 + .66 * Math.Sqrt(a);
            double power = weak ? 8 + 75 * b : 35 + 230 * correlation + 150 * b * b;
            if (particles.Count % 19 == 0) power += 250 + 200 * c;
            particles.Add(new(x, y, correlation, power));
        }
        _fixtureParticleMetrics[variant] = particles.ToArray();
    }

    private void RestoreParticleFilters(double correlationMin, double correlationMax, double powerMin, double? powerMax)
    {
        MinPickCorrelation = Math.Clamp(correlationMin, 0, 1);
        MaxPickCorrelation = Math.Clamp(correlationMax, MinPickCorrelation, 1);
        MinPickPower = Math.Max(0, powerMin);
        MaxPickPower = powerMax.HasValue ? Math.Max(MinPickPower, powerMax.Value) : null;
    }
}
