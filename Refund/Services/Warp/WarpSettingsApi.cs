using System.Text.Json;
namespace Refund.Services.Warp;
public sealed partial class WarpSession
{
    // Presets are shared by sessions in this space; serialize read-modify-write across managers.
    private static readonly object CalibrationPresetGate = new();
    private static readonly string[] CalibrationFields = ["Pixel", "InputPattern", "EerGrouping", "GainPath", "CorrectGain", "DefectsPath", "CorrectDefects", "GainFlipX", "GainFlipY", "GainTranspose", "Voltage", "Cs", "Cc", "Amplitude"];
    public string CalibrationPresetError { get; private set; }
    private string PresetsPath => Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Identity.SessionDirectory)) ?? Identity.SessionDirectory, "warp-calibration-presets.json");

    private List<WarpCalibrationPreset> ReadCalibrationPresets() => File.Exists(PresetsPath)
        ? JsonSerializer.Deserialize<List<WarpCalibrationPreset>>(File.ReadAllText(PresetsPath)) ?? [] : [];

    public IReadOnlyList<WarpCalibrationPreset> CalibrationPresets
    {
        get
        {
            lock (CalibrationPresetGate)
            {
                try { var presets = ReadCalibrationPresets(); CalibrationPresetError = null; return presets; }
                catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                {
                    _logger.Warning(ex, "Could not read calibration presets");
                    CalibrationPresetError = "Saved presets could not be read. Current collection settings are retained.";
                    return [];
                }
            }
        }
    }

    // Derived by the session, not edited: they never count as a pending change.
    private static readonly System.Reflection.PropertyInfo[] EditableSettings = typeof(WarpSettings).GetProperties()
        .Where(p => p.Name is not (nameof(WarpSettings.OutputPath) or nameof(WarpSettings.TrainAfter) or nameof(WarpSettings.BinTimes))).ToArray();

    /// <summary>Settings whose draft value differs from the applied one.</summary>
    public static IReadOnlySet<string> ChangedSettings(WarpSettings draft, WarpSettings applied) =>
        EditableSettings.Where(p => !Equals(p.GetValue(draft), p.GetValue(applied))).Select(p => p.Name).ToHashSet();

    public static WarpSettings NormalizeSettings(WarpSettings settings) => settings with
    {
        TrainAfter = settings.TrainCount,
        BinTimes = settings.Pixel > 0 && settings.TargetPixelSize > 0 ? (decimal)Math.Log2((double)(settings.TargetPixelSize/settings.Pixel)) : 0
    };

    private static WarpCalibrationPreset CapturePreset(string name, WarpSettings settings)
    {
        settings = NormalizeSettings(settings);
        return new(name, CalibrationFields.ToDictionary(n => n, n => JsonSerializer.SerializeToElement(typeof(WarpSettings).GetProperty(n)!.GetValue(settings))));
    }

    public WarpSettings LoadCalibrationPreset(string name, WarpSettings current)
    {
        var preset = CalibrationPresets.FirstOrDefault(p => p.Name == name) ?? throw new InvalidOperationException("Preset no longer exists.");
        current = NormalizeSettings(current);
        var result = current with { Preset = name };
        foreach (string field in CalibrationFields)
            if (preset.Values.TryGetValue(field, out var value))
            {
                var property = typeof(WarpSettings).GetProperty(field)!;
                property.SetValue(result, value.Deserialize(property.PropertyType));
            }
        // A preset changes camera calibration. Preserve the chosen physical target unless it
        // would upsample the new camera's raw pixels; direct user edits are validated instead.
        return NormalizeSettings(result with { TargetPixelSize = Math.Max(current.TargetPixelSize, result.Pixel) });
    }

    public void SaveCalibrationPreset(string name, WarpSettings settings, bool overwrite = false)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) throw new ArgumentException("Enter a preset name.");
        lock (CalibrationPresetGate)
        {
            // Reread under the shared gate so another session's save is never overwritten.
            // A corrupt/unreadable store is an error, never permission to replace it with an empty one.
            var next = ReadCalibrationPresets();
            int existing = next.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0 && !overwrite) throw new InvalidOperationException("That preset already exists. Use Update or choose another name.");
            if (existing < 0 && overwrite) throw new InvalidOperationException("Preset no longer exists. Use Save as to create it.");
            if (existing >= 0) next[existing] = CapturePreset(name, settings); else next.Add(CapturePreset(name, settings));
            Directory.CreateDirectory(Path.GetDirectoryName(PresetsPath)!);
            File.WriteAllText(PresetsPath, JsonSerializer.Serialize(next));
            CalibrationPresetError = null;
        }
        Publish();
    }

    public void DeleteCalibrationPreset(string name)
    {
        lock (CalibrationPresetGate)
        {
            var next = ReadCalibrationPresets();
            next.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Path.GetDirectoryName(PresetsPath)!);
            File.WriteAllText(PresetsPath, JsonSerializer.Serialize(next));
            CalibrationPresetError = null;
        }
        Publish();
    }

    public static string ValidateSettings(WarpSettings s)
    {
        if(string.IsNullOrWhiteSpace(s.InputPath))return "Choose the movie input folder.";
        if(string.IsNullOrWhiteSpace(s.InputPattern)||s.InputPattern.IndexOfAny([Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar])>=0)return "Enter a filename pattern such as *.eer.";
        if(s.Pixel<=0||s.TargetPixelSize<s.Pixel)return "Target pixel size must be at least the positive detector pixel size.";
        if(s.TotalDose<=0||s.MinimumFileSizeMb<0)return "Total dose must be positive and minimum file size nonnegative.";
        if(s.EerGrouping<1)return "EER group count must be positive.";
        if(s.CtfMin<=s.CtfMax||s.CtfMax<=0||s.MotionMin<=s.MotionMax||s.MotionMax<=0)return "Low-resolution limits must exceed high-resolution limits.";
        if(s.DefocusMin>=s.DefocusMax)return "Minimum defocus must be below maximum.";
        if(s.CorrectGain&&string.IsNullOrWhiteSpace(s.GainPath))return "Select the gain reference.";
        if(s.CorrectDefects&&string.IsNullOrWhiteSpace(s.DefectsPath))return "Select the defect map.";
        if(!s.MotionGridAuto&&(s.MotionGrid<1||s.MotionGridY<1||s.MotionGridZ<1))return "Motion grid dimensions must be positive.";
        if(!s.CtfGridAuto&&(s.CtfGridX<1||s.CtfGridY<1))return "CTF grid dimensions must be positive.";
        if(s.TrainCount<1||s.TrainingEpochs<1||s.FineTuneEvery<1)return "Training sample, epochs and interval are invalid.";
        if(s.Denoise&&!string.IsNullOrWhiteSpace(s.DenoiserStart)&&!File.Exists(s.DenoiserStart))return "The starting model for denoiser training does not exist.";
        if(s.Diameter<=0||s.MinSeparation<=0||s.Box<2||s.Box%2!=0)return "Particle diameter/separation must be positive and box size positive/even.";
        if(s.SkipFirst<0||s.SkipLast<0||s.StackGroup<1)return "Frame skips must be nonnegative; stack grouping must be positive.";
        return null;
    }
    public Task<IReadOnlyList<WarpInputCheck>> CheckInputAsync(WarpSettings draft) => Task.Run<IReadOnlyList<WarpInputCheck>>(() =>
    {
        var checks = new List<WarpInputCheck>();
        try
        {
            if (draft.MinimumFileSizeMb < 0) throw new ArgumentException("Minimum movie size must be nonnegative.");
            using var entries = Directory.EnumerateFiles(draft.InputPath, draft.InputPattern,
                draft.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).GetEnumerator();
            if (entries.MoveNext())
            {
                using var file = new FileStream(entries.Current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long minimumBytes = checked((long)Math.Ceiling(draft.MinimumFileSizeMb * 1_000_000m));
                string detail = Path.GetFileName(entries.Current) + $" · {file.Length:N0} bytes";
                if (file.Length < minimumBytes)
                    checks.Add(new("Movie input", null, detail + $"; below the {minimumBytes:N0}-byte minimum. Processing waits for a stable file meeting that size."));
                else if (file.ReadByte() < 0)
                    checks.Add(new("Movie input", null, detail + "; empty. Processing waits for a stable nonempty file."));
                else checks.Add(new("Movie input", true, detail + " readable on Relay; meets the minimum size."));
            }
            else checks.Add(new("Movie input", null, "Folder is readable; no matching movies yet."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException)
        { checks.Add(new("Movie input", false, ex.Message)); }
        foreach (var (name, enabled, path) in new[] { ("Gain", draft.CorrectGain, draft.GainPath), ("Defects", draft.CorrectDefects, draft.DefectsPath) })
            if (enabled)
                try
                {
                    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    bool nonempty = file.ReadByte() >= 0;
                    checks.Add(new(name, nonempty, nonempty ? "Readable on Relay; format and dimensions checked during processing." : "File is empty."));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                { checks.Add(new(name, false, ex.Message)); }
        checks.Add(new("Workers", null, "Cluster path access and GPU processing are verified by submitted workers."));
        return checks;
    });
}
