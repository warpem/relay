using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Refund.Components.SingleAxisScatter;
using Refund.Components.ThumbnailPanel;

namespace ComponentDev.Services;

[Flags]
public enum PreviewStage { None = 0, Motion = 1, Ctf = 2, Enhance = 4, Pick = 8, Extract = 16, All = 31 }

public sealed class PreviewMovie
{
    public int Index { get; set; }
    [JsonIgnore] public string Name => $"FoilHole_{128000 + Index / 8}_Data_{Index + 1:D6}.eer";
    public double Defocus { get; set; }
    public double Astigmatism { get; set; }
    public double AstigmatismAngle { get; set; }
    public PreviewDisposition Disposition { get; set; }
    public double Ctf { get; set; }
    public double Motion { get; set; }
    public double Ice { get; set; }
    public int Picks { get; set; }
    public PreviewStage Done { get; set; }
    public PreviewStage Stale { get; set; }
    public bool Excluded { get; set; }
    public bool Failed { get; set; }
}

public sealed partial record PreviewSettings(decimal Pixel = .83m, decimal Dose = 1.1m, int MotionGrid = 5,
    decimal CtfMin = 30, decimal CtfMax = 4, decimal Diameter = 140, decimal Threshold = .65m,
    int Box = 256, int TrainAfter = 100, bool FineTune = false,
    string Preset = "Krios · Falcon 4", string InputPath = "/data/krios/apoferritin/",
    string OutputPath = "/projects/apoferritin/warp/", string GainPath = "/calibration/falcon4/gain.mrc",
    string Acquisition = "EER", int EerGrouping = 10);
public sealed record PreviewExport(int Number, string Kind, int Movies, long Items, string Path, DateTime Created);
public sealed record PreviewCheckpoint(PreviewSettings Settings, List<PreviewMovie> Movies,
    List<PreviewExport> Exports, int Workers, int Model, double MaxCtf, double MaxMotion, string StudyScenario = "Stable collection", double MaxIce = 2000, int MinParticles = 0, double MaxAstigmatism = 1, DateTime? LastInputAt = null, DateTime? LastResultAt = null, double MinCtf = 0, double MinMotion = 0, double MinIce = 0, int MaxParticles = int.MaxValue, double MinPickCorrelation = 0, double MaxPickCorrelation = 1, double MinPickPower = 0, double? MaxPickPower = null, PreviewParticleHistogramCheckpoint ParticleHistogram = null);

// A UI prototype, deliberately separate from DataManager and the real execution coordinator.
// One catalog per server. Views own only selection, filters, and their rendering resources.
public sealed partial class WarpSessionPreview : IDisposable
{
    private readonly object _sync = new();
    private readonly ILogger<WarpSessionPreview> _logger;
    private readonly Timer _timer;
    private int _ticks;
    private int _lastFineTuneCount;
    private bool _startup;
    private bool _disposed;
    public event Action Changed;
    public string Root { get; }
    public WarpPreviewFixtures Fixtures { get; }
    public List<PreviewMovie> Movies { get; private set; } = [];
    public List<ThumbnailData> Thumbnails { get; private set; } = [];
    public List<ScatterPoint>[] Plots { get; private set; } = [[], [], [], []];
    public int PlotVersion { get; private set; }
    public int CatalogVersion { get; private set; }
    public PreviewSettings Settings { get; private set; } = new();
    public List<PreviewExport> Exports { get; private set; } = [];
    public DateTime LastInputAt { get; private set; } = DateTime.UtcNow.AddSeconds(-12);
    public DateTime LastResultAt { get; private set; } = DateTime.UtcNow.AddSeconds(-36);
    public string StudyScenario { get; private set; } = "Stable collection";
    public bool Running { get; private set; }
    public int Workers { get; private set; } = 8;
    public int Model { get; private set; } = 2;
    public int TrainingPercent { get; private set; } = 100;
    public bool Training { get; private set; }
    public double MaxCtf { get; private set; } = 5;
    public double MaxMotion { get; private set; } = 2.5;
    public int Accepted { get; private set; }
    public int Complete { get; private set; }
    public int Failed { get; private set; }
    public int Pending { get; private set; }
    public int Outdated { get; private set; }
    public long ParticleCount { get; private set; }
    public int[] StageCounts { get; } = new int[5];
    public DateTime SavedAt { get; private set; }
    public string Notice { get; private set; } = "";
    public int Viewers { get; private set; }
    public bool Exporting { get; private set; }

    public WarpSessionPreview(ILogger<WarpSessionPreview> logger, IConfiguration configuration)
    {
        _logger = logger;
        Root = configuration["WarpPreview:Directory"] ?? Path.Combine(Path.GetTempPath(), "relay-warp-preview");
        Directory.CreateDirectory(Root);
        Fixtures = new WarpPreviewFixtures(Root);
        try
        {
            if (File.Exists(CheckpointPath))
            {
                var saved = JsonSerializer.Deserialize<PreviewCheckpoint>(File.ReadAllText(CheckpointPath));
                bool legacySettings = saved.Settings.SettingsSchemaVersion < CurrentSettingsSchemaVersion;
                Settings = NormalizeSettings(saved.Settings, legacySettings); Movies = saved.Movies; Exports = saved.Exports;
                Workers = saved.Workers; Model = saved.Model; MaxCtf = saved.MaxCtf; MaxMotion = saved.MaxMotion; StudyScenario = saved.StudyScenario ?? "Stable collection"; MaxIce = saved.MaxIce; MinParticles = saved.MinParticles; MaxAstigmatism = saved.MaxAstigmatism; MinCtf = saved.MinCtf; MinMotion = saved.MinMotion; MinIce = saved.MinIce; MaxParticles = saved.MaxParticles; LastInputAt = saved.LastInputAt ?? LastInputAt; LastResultAt = saved.LastResultAt ?? LastResultAt;
                RestoreParticleFilters(saved.MinPickCorrelation, saved.MaxPickCorrelation, saved.MinPickPower, saved.MaxPickPower);
                RestoreParticleHistogram(saved.ParticleHistogram);
                Notice = legacySettings ? LegacySettingsMigrationNotice : "Restored session · processing stopped";
                if (legacySettings) _logger.LogWarning("{MigrationNotice}", LegacySettingsMigrationNotice);
                SavedAt = File.GetLastWriteTime(CheckpointPath);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _logger.LogWarning(ex, "Could not restore the prototype checkpoint");
        }
        Settings = NormalizeSettings(Settings);
        if (Movies.Count == 0) Seed(12480);
        RebuildCatalog();
        _lastFineTuneCount = StageCounts[1];
        _timer = new Timer(Tick, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    private string CheckpointPath => Path.Combine(Root, "session-mini.json");
    public void Attach() { lock (_sync) Viewers++; }
    public void Detach() { lock (_sync) Viewers--; }
    public string StageState(PreviewMovie m, PreviewStage stage) => m.Stale.HasFlag(stage) ? "Redo" : m.Done.HasFlag(stage) ? "Done" : "Waiting";

    private void Seed(int count)
    {
        var random = new Random(42);
        Movies = new List<PreviewMovie>(count);
        for (int i = 0; i < count; i++)
        {
            double noise = Math.Sqrt(-2 * Math.Log(Math.Max(.00001, random.NextDouble()))) * Math.Cos(6.28 * random.NextDouble());
            Movies.Add(new PreviewMovie {
                Index = i, Defocus = 1.1 + (i % 16) * .08 + .02 * noise,
                Ctf = Math.Max(2.4, 3.5 + .3 * noise + (i > 7700 && i < 8300 ? 2 : 0) + (i % 47 == 0 ? 2 : 0)),
                Motion = Math.Max(.25, .85 + .22 * noise + (i % 79 == 0 ? 2.6 : 0)),
                Ice = Math.Max(150, 610 + 80 * noise + (i > 7700 && i < 8300 ? 400 : 0)),
                Picks = Math.Max(12, (int)(175 + 35 * noise)),
                Done = count < 1000 || i < count - 192 ? PreviewStage.All : i < count - 96 ? PreviewStage.Motion | PreviewStage.Ctf : PreviewStage.None,
                Failed = count > 1000 && i % 613 == 17,
                Excluded = count > 1000 && i % 431 == 25
            });
        }
    }

    private void RebuildCatalog()
    {
        foreach (var m in Movies) if (m.Astigmatism <= 0) { double x = .045 + .015 * Math.Sin(m.Index * .71) + .012 * Math.Sin(m.Index * .13); double y = .02 + .016 * Math.Cos(m.Index * .51) + .01 * Math.Cos(m.Index * .21); m.Astigmatism = Math.Sqrt(x * x + y * y); m.AstigmatismAngle = (Math.Atan2(y, x) * 90 / Math.PI + 180) % 180; }
        Thumbnails = Movies.Select(m => new ThumbnailData { Index = m.Index, DisplayName = m.Name, ImagePath = Fixtures.Thumbnail(m.Index) }).ToList();
        Plots = [[], [], [], []];
        foreach (var m in Movies)
        {
            Plots[0].Add(new ScatterPoint(m.Defocus, Color.YellowGreen, m));
            Plots[1].Add(new ScatterPoint(m.Ctf, Color.YellowGreen, m));
            Plots[2].Add(new ScatterPoint(m.Motion, Color.YellowGreen, m));
            Plots[3].Add(new ScatterPoint(m.Picks, Color.YellowGreen, m));
        }
        CatalogVersion++;
        UpdateCounts();
        Recolor();
    }

    private void UpdateCounts()
    {
        Accepted = Complete = Failed = Pending = Outdated = 0; ParticleCount = 0;
        Array.Clear(StageCounts);
        foreach (var m in Movies)
        {
            if (m.Failed) Failed++;
            if (Passes(m) && m.Done.HasFlag(PreviewStage.Ctf)) { Accepted++; if (m.Done.HasFlag(PreviewStage.Pick)) ParticleCount += m.Picks; }
            if ((m.Done & RequiredStages) == RequiredStages && ActiveStale(m) == 0 && !m.Failed) Complete++;
            if (ActiveStale(m) != 0) Outdated++;
            else if ((m.Done & RequiredStages) != RequiredStages && !m.Failed) Pending++;
            for (int s = 0; s < 5; s++)
                if (m.Done.HasFlag((PreviewStage)(1 << s)) && !m.Stale.HasFlag((PreviewStage)(1 << s))) StageCounts[s]++;
            if (m.Index < Thumbnails.Count)
                Thumbnails[m.Index].Status = ThumbnailStatus(m);
        }
    }

    private void Recolor()
    {
        for (int i = 0; i < Movies.Count; i++)
        {
            var m = Movies[i];
            var color = ColorTranslator.FromHtml(StatusColor(m));
            for (int j = 0; j < 4; j++) { var p = Plots[j][i]; p.Color = color; Plots[j][i] = p; }
        }
        PlotVersion++;
    }

    public void ToggleRunning() => Change(() => { Running = !Running; Notice = Running ? $"Processing with {Workers} workers" : "Processing stopped · parameters unlocked"; });
    public void Resize(int target) => Change(() => Workers = Math.Clamp(target, 1, 64));
    public void Exclude(int index) => Change(() => { Movies[index].Excluded = !Movies[index].Excluded; Movies[index].Disposition = Movies[index].Excluded ? PreviewDisposition.Exclude : PreviewDisposition.Auto; Recolor(); });
    public void Filter(double ctf, double motion) => Change(() => { MaxCtf = ctf; MaxMotion = motion; Recolor(); });
    public void ExcludeRange(int start, int end, bool excluded) => Change(() => {
        start = Math.Clamp(start, 0, Movies.Count); end = Math.Clamp(end, start, Movies.Count);
        for (int i = start; i < end; i++) { Movies[i].Excluded = excluded; Movies[i].Disposition = excluded ? PreviewDisposition.Exclude : PreviewDisposition.Auto; }
        Recolor(); Notice = excluded ? $"{end - start:N0} movies excluded" : $"Manual exclusion cleared for {end - start:N0} movies; quality filters still apply";
    });
    public void RetryFailures() => Change(() => { foreach (var m in Movies.Where(m => m.Failed)) { m.Failed = false; m.Stale = PreviewStage.All; } Notice = "Failed movies queued for retry"; Recolor(); });

    public void Apply(PreviewSettings value)
    {
        Change(() => {
            if (Running) { Notice = "Stop processing before changing parameters"; return; }
            string error = ValidateSettings(value);
            if (error != null) { Notice = error; return; }
            value = NormalizeSettings(value);
            PreviewStage redo = DetermineRedo(Settings, value);
            Settings = value;
            if (!Settings.Denoise) { Training = false; TrainingPercent = Model > 0 ? 100 : 0; }
            foreach (var m in Movies) m.Stale |= redo & m.Done;
            Recolor();
            Notice = redo == PreviewStage.None ? "Parameters saved" : $"Queued: {redo.ToString().Replace("Ctf", "CTF").Replace("Enhance", "denoise")}";
        });
    }

    public void Train() => Change(() => { if (Settings.Denoise && Running && !Training && StageCounts[1] >= Settings.TrainCount) { Training = true; TrainingPercent = 0; Notice = $"Training on {Settings.TrainCount:N0} representative micrographs"; } });
    public void Scenario(bool startup) => Change(() => {
        Running = false; Training = false; _startup = startup;
        Model = startup ? 0 : 2; TrainingPercent = startup ? 0 : 100;
        Seed(startup ? 96 : 12480);
        if (startup) foreach (var m in Movies) m.Done = PreviewStage.Motion | PreviewStage.Ctf;
        RebuildCatalog(); Notice = startup ? "96 movies · start to watch the first training cycle" : "12,480 movies loaded";
    });

    public void SetStudyScenario(string scenario) => Change(() => {
        if (scenario is not ("Stable collection" or "Quality shift" or "No incoming movies" or "Processing backlog" or "Denoiser startup")) return;
        Running = false; Training = false; Model = 2; TrainingPercent = 100; _startup = false;
        StudyScenario = scenario;
        LastInputAt = DateTime.UtcNow - (scenario == "No incoming movies" ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(12));
        LastResultAt = DateTime.UtcNow.AddSeconds(-36);
        Running = scenario is "No incoming movies" or "Processing backlog";
        Seed(12480);
        if (scenario == "Denoiser startup") { Seed(96); foreach (var m in Movies) m.Done = PreviewStage.Motion | PreviewStage.Ctf; Model = 0; TrainingPercent = 0; _startup = true; }
        if (scenario == "Quality shift")
        {
            foreach (var m in Movies.Where(m => m.Index >= Movies.Count - 500 && m.Index < Movies.Count - 192))
            { m.Ctf += 2.4; m.Ice += 650; m.Picks = Math.Max(12, m.Picks / 3); m.Astigmatism = .18 + .07 * Math.Abs(Math.Sin(m.Index)); m.AstigmatismAngle = 30 + 12 * Math.Sin(m.Index * .7); }
        }
        if (scenario == "No incoming movies") foreach (var m in Movies) m.Done = PreviewStage.All;
        if (scenario == "Processing backlog") foreach (var m in Movies.TakeLast(1500)) m.Done = PreviewStage.None;
        RebuildCatalog();
        Notice = "";
    });

    private void Tick(object state)
    {
        try
        {
            lock (_sync)
            {
                if (_disposed || !Running) return;
                _ticks++;
                if (_startup && Movies.Count < Math.Max(120, Settings.TrainCount + 20))
                {
                    LastInputAt = DateTime.UtcNow;
                    int oldCount = Movies.Count;
                    var previous = Movies;
                    Seed(Math.Min(Math.Max(120, Settings.TrainCount + 20), oldCount + 4));
                    for (int i = 0; i < oldCount; i++) Movies[i] = previous[i];
                    foreach (var m in Movies.Skip(oldCount)) m.Done = PreviewStage.Motion | PreviewStage.Ctf;
                    RebuildCatalog();
                }
                if (Settings.Denoise && Model == 0 && StageCounts[1] >= Settings.TrainCount && !Training) { Training = true; TrainingPercent = 0; }
                if (Settings.Denoise && Settings.FineTune && Model > 0 && !Training && StageCounts[1] - _lastFineTuneCount >= Settings.FineTuneEvery) { Training = true; TrainingPercent = 0; _lastFineTuneCount = StageCounts[1]; }
                if (Settings.Denoise && Training)
                {
                    TrainingPercent = Math.Min(100, TrainingPercent + 10);
                    if (TrainingPercent == 100)
                    {
                        Training = false; Model++;
                        foreach (var m in Movies) m.Stale |= m.Done & (PreviewStage.Enhance | PreviewStage.Pick | PreviewStage.Extract);
                        Notice = $"Denoiser v{Model} ready · motion and CTF retained";
                    }
                }
                int budget = Workers * 3;
                foreach (var m in Movies)
                {
                    if (budget == 0) break;
                    if (m.Failed) continue;
                    var needed = (PreviewStage.All & ~m.Done) | m.Stale;
                    if (Settings.Denoise && Model == 0) needed &= PreviewStage.Motion | PreviewStage.Ctf;
                    if (!Settings.PickingEnabled) needed &= ~(PreviewStage.Pick | PreviewStage.Extract);
                    if (needed == 0) continue;
                    var next = (PreviewStage)((int)needed & -(int)needed);
                    m.Done |= next; m.Stale &= ~next; budget--;
                }
                if (budget < Workers * 3) LastResultAt = DateTime.UtcNow;
                UpdateCounts();
                PlotVersion++;
                if (_ticks % 10 == 0) Save();
            }
            Changed?.Invoke();
        }
        catch (Exception ex) { _logger.LogError(ex, "Prototype simulation tick failed"); }
    }

    private void Change(Action action)
    {
        lock (_sync) { action(); UpdateCounts(); Save(); }
        Changed?.Invoke();
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(CheckpointPath, JsonSerializer.Serialize(new PreviewCheckpoint(Settings, Movies, Exports, Workers, Model, MaxCtf, MaxMotion, StudyScenario, MaxIce, MinParticles, MaxAstigmatism, LastInputAt, LastResultAt, MinCtf, MinMotion, MinIce, MaxParticles, MinPickCorrelation, MaxPickCorrelation, MinPickPower, MaxPickPower, CaptureParticleHistogram())));
            SavedAt = DateTime.Now;
        }
        catch (IOException ex) { _logger.LogWarning(ex, "Could not save prototype mini JSON"); Notice = "Checkpoint could not be saved"; }
    }

    public async Task ExportAsync(bool picks)
    {
        (int Index, double Defocus)[] snapshot;
        PreviewParticleMetric[][] particleRows = null;
        int number;
        lock (_sync)
        {
            if (Exporting) return;
            number = Exports.Count + 43;
            snapshot = Movies.Where(m => Passes(m) && (picks ? HasCurrentParticleMetrics(m) : m.Done.HasFlag(PreviewStage.Ctf)))
                .Select(m => (m.Index, m.Defocus)).ToArray();
            if (picks)
            {
                EnsureParticleMetrics();
                // Capture both the eligible movie cohort and exact particle selection once.
                // Views can change filters while the exporter writes its copied rows.
                particleRows = _fixtureParticleMetrics.Select(rows => rows.Where(PassesParticleFilter).ToArray()).ToArray();
            }
            Exporting = true;
        }
        Changed?.Invoke();
        try
        {
            string path = Path.Combine(Root, $"J{number}-{(picks ? "picks" : "micrographs")}.star");
            long rows = await Task.Run(() => {
                long count = 0;
                using var writer = new StreamWriter(path);
                writer.WriteLine(picks ? "data_particles\n\nloop_\n_rlnMicrographName #1\n_rlnCoordinateX #2\n_rlnCoordinateY #3\n_rlnAutopickFigureOfMerit #4\n_wrpAutopickPower #5" : "data_micrographs\n\nloop_\n_rlnMicrographName #1\n_rlnDefocusU #2\n_rlnDefocusV #3");
                foreach (var item in snapshot)
                {
                    string image = "'" + Fixtures.Image(item.Index, false) + "'";
                    if (picks)
                    {
                        foreach (var particle in particleRows[item.Index % WarpPreviewFixtures.Count])
                        {
                            writer.WriteLine(FormattableString.Invariant($"{image} {particle.X:G9} {particle.Y:G9} {particle.Correlation:G9} {particle.Power:G9}"));
                            count++;
                        }
                    }
                    else { writer.WriteLine(FormattableString.Invariant($"{image} {item.Defocus * 10000:F0} {item.Defocus * 10000:F0}")); count++; }
                }
                return count;
            });
            lock (_sync) { Exports.Add(new PreviewExport(number, picks ? "Particle coordinates" : "Micrographs", snapshot.Length, rows, path, DateTime.Now)); Save(); Notice = $"J{number} · {rows:N0} {(picks ? "coordinates" : "micrographs")} exported"; }
        }
        finally { lock (_sync) Exporting = false; Changed?.Invoke(); }
    }

    public void Dispose() { lock (_sync) { _disposed = true; _timer.Dispose(); Save(); } }
}
