using System.Text.Json;


namespace ComponentDev.Services;

public sealed record WarpCalibrationPreset(string Name, Dictionary<string, JsonElement> Values);
public sealed record WarpInputCheck(string Item, bool? Passed, string Detail);

public sealed partial class WarpSessionPreview
{
    private List<WarpCalibrationPreset> _calibrationPresets;
    private static readonly string[] CalibrationFields = ["Pixel", "InputPattern", "EerGrouping", "GainPath", "CorrectGain", "DefectsPath", "CorrectDefects", "GainFlipX", "GainFlipY", "GainTranspose", "Voltage", "Cs", "Cc", "Amplitude", "IlluminationAperture", "EnergySpread"];
    public string CalibrationPresetError { get; private set; }
    private string PresetsPath => Path.Combine(Root, "calibration-presets.json");

    public IReadOnlyList<WarpCalibrationPreset> CalibrationPresets
    {
        get
        {
            lock (_sync)
            {
                if (_calibrationPresets != null) return _calibrationPresets.ToArray();
                try
                {
                    _calibrationPresets = File.Exists(PresetsPath)
                        ? JsonSerializer.Deserialize<List<WarpCalibrationPreset>>(File.ReadAllText(PresetsPath)) ?? []
                        : [CapturePreset("Krios · Falcon 4", new()), CapturePreset("Krios · K3", new PreviewSettings(Pixel: 1.06m, GainPath: "/calibration/k3/gain.mrc", Acquisition: "TIFF", EerGrouping: 1) { InputPattern = "*.tif" })];
                }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Could not read calibration presets");
                    CalibrationPresetError = "Saved presets could not be read. Current collection settings are retained.";
                    _calibrationPresets = [];
                }
                return _calibrationPresets.ToArray();
            }
        }
    }

    public const int CurrentSettingsSchemaVersion = 1;
    public const string LegacySettingsMigrationNotice = "Legacy per-frame dose could not be converted without a frame count. Total dose is set to the prototype default of 40 e⁻/Å²; verify it for this collection.";

    private static string LegacyInputPattern(string acquisition) => acquisition?.ToUpperInvariant() switch
    {
        "EER" => "*.eer", "TIFF" => "*.tif*", "MRC" => "*.mrc*", "EM" => "*.em", _ => "*.dat"
    };

    public static PreviewSettings NormalizeSettings(PreviewSettings settings, bool legacy = false)
    {
        settings ??= new();
        decimal target = settings.TargetPixelSize;
        if (legacy || target == 0)
        {
            double derived = (double)settings.Pixel * Math.Pow(2, (double)settings.BinTimes);
            target = double.IsFinite(derived) && derived > 0 && derived <= (double)decimal.MaxValue ? (decimal)derived : 0;
        }
        // This is a UI migration, not an estimate of the collection's exposure: a frame count
        // is unavailable, so the old Dose value cannot establish a total dose.
        return settings with
        {
            SettingsSchemaVersion = CurrentSettingsSchemaVersion,
            InputPattern = (legacy ? LegacyInputPattern(settings.Acquisition) : settings.InputPattern)?.Trim() ?? "",
            TotalDose = legacy ? 40 : settings.TotalDose,
            TrainAfter = settings.TrainCount,
            TargetPixelSize = target,
            BinTimes = settings.Pixel > 0 && target > 0 ? (decimal)Math.Log2((double)target / (double)settings.Pixel) : settings.BinTimes,
            MotionGridAuto = legacy ? settings.MotionGrid == 0 || settings.MotionGridY == 0 || settings.MotionGridZ == 0 : settings.MotionGridAuto,
            CtfGridAuto = legacy ? settings.CtfGridX == 0 || settings.CtfGridY == 0 : settings.CtfGridAuto,
            MotionGrid = legacy && settings.MotionGrid == 0 ? 5 : settings.MotionGrid,
            MotionGridY = legacy && settings.MotionGridY == 0 ? 5 : settings.MotionGridY,
            MotionGridZ = legacy && settings.MotionGridZ == 0 ? 20 : settings.MotionGridZ,
            CtfGridX = legacy && settings.CtfGridX == 0 ? 5 : settings.CtfGridX,
            CtfGridY = legacy && settings.CtfGridY == 0 ? 5 : settings.CtfGridY
        };
    }

    private static WarpCalibrationPreset CapturePreset(string name, PreviewSettings settings)
    {
        settings = NormalizeSettings(settings);
        return new(name, CalibrationFields.ToDictionary(n => n, n => JsonSerializer.SerializeToElement(typeof(PreviewSettings).GetProperty(n)!.GetValue(settings))));
    }

    public PreviewSettings LoadCalibrationPreset(string name, PreviewSettings current)
    {
        var preset = CalibrationPresets.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException("Preset no longer exists.");
        current = NormalizeSettings(current);
        var result = current with { Preset = name };
        foreach (string field in CalibrationFields)
            if (preset.Values.TryGetValue(field, out var value))
            {
                var property = typeof(PreviewSettings).GetProperty(field)!;
                property.SetValue(result, value.Deserialize(property.PropertyType));
            }
        if (!preset.Values.ContainsKey("InputPattern") && preset.Values.TryGetValue("Acquisition", out var acquisition))
            result = result with { Acquisition = acquisition.GetString(), InputPattern = LegacyInputPattern(acquisition.GetString()) };
        // A preset changes camera calibration. Preserve the chosen physical target unless it
        // would upsample the new camera's raw pixels; direct user edits are validated instead.
        return NormalizeSettings(result with { TargetPixelSize = Math.Max(current.TargetPixelSize, result.Pixel) });
    }

    public void SaveCalibrationPreset(string name, PreviewSettings settings, bool overwrite = false)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Enter a preset name.");
        _ = CalibrationPresets;
        lock (_sync)
        {
            var existing = _calibrationPresets.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0 && !overwrite) throw new InvalidOperationException("That preset already exists. Use Update or choose another name.");
            var next = _calibrationPresets.ToList();
            if (existing >= 0) next[existing] = CapturePreset(name, settings); else next.Add(CapturePreset(name, settings));
            File.WriteAllText(PresetsPath, JsonSerializer.Serialize(next));
            _calibrationPresets = next;
            CalibrationPresetError = null;
        }
    }

    public void DeleteCalibrationPreset(string name)
    {
        _ = CalibrationPresets;
        lock (_sync)
        {
            var next = _calibrationPresets.Where(p => p.Name != name).ToList();
            File.WriteAllText(PresetsPath, JsonSerializer.Serialize(next));
            _calibrationPresets = next;
            CalibrationPresetError = null;
        }
    }

    private static bool Differ(PreviewSettings old, PreviewSettings next, params string[] names) => names.Any(n => {
        var property = typeof(PreviewSettings).GetProperty(n);
        return property != null && !Equals(property.GetValue(old), property.GetValue(next));
    });

    public static PreviewStage DetermineRedo(PreviewSettings old, PreviewSettings next)
    {
        old = NormalizeSettings(old);
        next = NormalizeSettings(next);
        bool motionGridChanged = old.MotionGridAuto != next.MotionGridAuto || !next.MotionGridAuto && Differ(old, next, "MotionGrid", "MotionGridY", "MotionGridZ");
        bool ctfGridChanged = old.CtfGridAuto != next.CtfGridAuto || !next.CtfGridAuto && Differ(old, next, "CtfGridX", "CtfGridY");
        if (motionGridChanged || Differ(old, next, "InputPath", "Pixel", "TotalDose", "TargetPixelSize", "GainPath", "CorrectGain", "DefectsPath", "CorrectDefects", "GainFlipX", "GainFlipY", "GainTranspose", "EerGrouping", "MotionMin", "MotionMax", "MotionBfactor", "SkipFirst", "SkipLast", "ExportAverage", "ExportStack", "StackGroup")) return PreviewStage.All;
        if (ctfGridChanged || Differ(old, next, "CtfMin", "CtfMax", "CtfWindow", "CtfQuality", "Voltage", "Cs", "Cc", "Amplitude", "IlluminationAperture", "EnergySpread", "DefocusMin", "DefocusMax", "FitPhase", "FitThickness", "Thickness")) return PreviewStage.Ctf | PreviewStage.Enhance | PreviewStage.Pick | PreviewStage.Extract;
        if (Differ(old, next, "Denoise", "DeconvolutionStrength", "DeconvolutionFalloff")) return PreviewStage.Enhance | PreviewStage.Pick | PreviewStage.Extract;
        if (Differ(old, next, "Diameter", "Threshold", "PickingEnabled", "MinSeparation")) return PreviewStage.Pick | PreviewStage.Extract;
        if (old.Box != next.Box) return PreviewStage.Extract;
        // File discovery patterns and the managed job output path do not change existing results.
        return PreviewStage.None;
    }

    public string GetSettingsImpact(PreviewSettings draft) => DetermineRedo(Settings, draft) switch {
        PreviewStage.All => "Reprocess motion, CTF, enhancement, picking and extraction.",
        PreviewStage.Ctf | PreviewStage.Enhance | PreviewStage.Pick | PreviewStage.Extract => "Redo CTF, enhancement, picking and extraction; keep motion.",
        PreviewStage.Enhance | PreviewStage.Pick | PreviewStage.Extract => "Redo enhancement, picking and extraction; keep motion and CTF.",
        PreviewStage.Pick | PreviewStage.Extract => "Redo picking and extraction; keep motion, CTF and enhanced images.",
        PreviewStage.Extract => "Redo extraction only.",
        _ => draft == Settings ? "" : "Update collection and training settings; keep existing results."
    };

    public static string ValidateSettings(PreviewSettings value)
    {
        if (string.IsNullOrWhiteSpace(value.InputPath)) return "An input folder is required.";
        if (string.IsNullOrWhiteSpace(value.InputPattern) || value.InputPattern.IndexOfAny(['/', '\\', '\0', '\r', '\n']) >= 0 || value.InputPattern is "." or "..") return "Enter a filename pattern such as *.eer or FoilHole*_Data_*.tif; choose its folder separately.";
        if (value.Pixel <= 0) return "Raw pixel size must be positive.";
        if (value.MinimumFileSizeMb < 0 || value.MinimumFileSizeMb > 1000000) return "Minimum file size must be between 0 and 1,000,000 MB.";
        if (value.TotalDose <= 0) return "Total dose must be positive, in e⁻/Å² for the whole movie.";
        if (value.TargetPixelSize < value.Pixel) return "Target pixel size must be at least the raw pixel size.";
        if (value.CtfMin <= value.CtfMax || value.CtfMax <= 0) return "CTF low-resolution limit must exceed its high-resolution limit.";
        if (value.MotionMin <= value.MotionMax || value.MotionMax <= 0) return "Motion low-resolution limit must exceed its high-resolution limit.";
        if (value.DefocusMin >= value.DefocusMax) return "Defocus minimum must be below maximum.";
        if (value.CorrectGain && string.IsNullOrWhiteSpace(value.GainPath)) return "Select a gain reference or turn gain correction off.";
        if (value.CorrectDefects && string.IsNullOrWhiteSpace(value.DefectsPath)) return "Select a defect map or turn defect correction off.";
        if (value.EerGrouping < 1) return "EER grouping must be at least 1.";
        if (!value.MotionGridAuto && (value.MotionGrid < 1 || value.MotionGridY < 1 || value.MotionGridZ < 1)) return "Manual motion grid dimensions must be at least 1.";
        if (!value.CtfGridAuto && (value.CtfGridX < 1 || value.CtfGridY < 1)) return "Manual defocus grid dimensions must be at least 1.";
        if (value.TrainCount < 1 || value.TrainingEpochs < 1 || value.FineTuneEvery < 1) return "Training counts and intervals must be positive.";
        if (value.MinSeparation < .1m || value.MinSeparation > 5) return "Minimum separation must be between 0.1 and 5 particle diameters.";
        if (value.Diameter <= 0 || value.Box < 1) return "Particle diameter and extraction box must be positive.";
        if (value.Threshold < 0 || value.Threshold > 1) return "Picking threshold must be between 0 and 1.";
        return null;
    }

    // Explicit checks run on the Relay host. They never claim remote-worker access.
    public Task<IReadOnlyList<WarpInputCheck>> CheckInputAsync(PreviewSettings draft) => Task.Run<IReadOnlyList<WarpInputCheck>>(() => {
        var checks = new List<WarpInputCheck>();
        string example = null;
        try
        {
            if (!Directory.Exists(draft.InputPath)) throw new IOException("Folder is not accessible on the Relay host.");
            if (string.IsNullOrWhiteSpace(draft.InputPattern) || draft.InputPattern.IndexOfAny(['/', '\\', '\0', '\r', '\n']) >= 0) throw new ArgumentException("Choose a filename search pattern, with its folder specified separately.");
            var options = new EnumerationOptions { RecurseSubdirectories = draft.Recursive, IgnoreInaccessible = false, MaxRecursionDepth = 12 };
            if (draft.MinimumFileSizeMb < 0 || draft.MinimumFileSizeMb > 1000000) throw new ArgumentException("Minimum file size must be between 0 and 1,000,000 MB.");
            long minimumBytes = (long)Math.Ceiling(draft.MinimumFileSizeMb * 1_000_000m);
            example = Directory.EnumerateFiles(draft.InputPath, draft.InputPattern, options).FirstOrDefault(path => new FileInfo(path).Length >= minimumBytes);
            checks.Add(new("Input folder", true, example == null ? "Readable; no matching movies meet the minimum file size." : "Readable on the Relay host."));
            if (example != null)
            {
                using var stream = new FileStream(example, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.ReadByte() < 0) throw new IOException("The first matching movie is empty.");
                checks.Add(new("Example movie", true, $"{Path.GetFileName(example)} · {stream.Length:N0} bytes readable"));
                checks.Add(new("Image / gain orientation", null, "Not checked; image decoding and worker preprocessing are not connected in this prototype."));
            }
            else checks.Add(new("Example movie", null, $"Waiting for a movie matching {draft.InputPattern} of at least {draft.MinimumFileSizeMb:0.###} MB."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { checks.Add(new("Input", false, ex.Message)); }
        foreach (var item in new[] { ("Gain reference", draft.CorrectGain, draft.GainPath), ("Defect map", draft.CorrectDefects, draft.DefectsPath) })
            if (item.Item2)
                try { using var stream = new FileStream(item.Item3, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); if (stream.ReadByte() < 0) throw new IOException("File is empty."); checks.Add(new(item.Item1, true, "Readable on the Relay host; dimensions and orientation not checked.")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { checks.Add(new(item.Item1, false, ex.Message)); }
        try
        {
            if (!Directory.Exists(Root)) throw new IOException("The managed job folder is not available on the Relay host.");
            string probe = Path.Combine(Root, $".warp-access-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            checks.Add(new("Job folder", true, "Managed by Relay; writable on the Relay host."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { checks.Add(new("Job folder", false, ex.Message)); }
        checks.Add(new("Worker access", null, "Not checked; no cluster worker is connected to the prototype."));
        return checks;
    });
}
