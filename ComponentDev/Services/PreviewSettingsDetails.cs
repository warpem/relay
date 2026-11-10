using System.Text.Json.Serialization;

namespace ComponentDev.Services;

// Frame-series parameters from Warp OptionsImport/CTF/Movement/Grids/Export.
public sealed partial record PreviewSettings
{
    // Version 0 identifies the original prototype's format/per-frame-dose/bin-exponent UI.
    public int SettingsSchemaVersion { get; init; }
    public string InputPattern { get; init; } = "*.eer";
    /// <summary>Minimum movie file size in decimal megabytes; zero accepts any size.</summary>
    public decimal MinimumFileSizeMb { get; init; }
    public decimal TotalDose { get; init; } = 40;
    public decimal TargetPixelSize { get; init; }
    public bool MotionGridAuto { get; init; } = true;
    public bool CtfGridAuto { get; init; } = true;

    // WarpTools uses a negative internal dose to mean total exposure and zero grids for auto.
    // Actual per-movie grid resolution remains WarpLib's responsibility.
    [JsonIgnore] public decimal WorkerDosePerAngstromFrame => -TotalDose;
    [JsonIgnore] public string MotionGridArgument => MotionGridAuto ? null : $"{MotionGrid}x{MotionGridY}x{MotionGridZ}";
    [JsonIgnore] public string CtfGridArgument => CtfGridAuto ? null : $"{CtfGridX}x{CtfGridY}";

    public bool Recursive { get; init; }
    public decimal BinTimes { get; init; }
    public bool CorrectGain { get; init; } = true;
    public string DefectsPath { get; init; } = "";
    public bool CorrectDefects { get; init; }
    public bool GainFlipX { get; init; }
    public bool GainFlipY { get; init; }
    public bool GainTranspose { get; init; }
    public int MotionGridY { get; init; } = 5;
    public int MotionGridZ { get; init; } = 20;
    public decimal MotionMin { get; init; } = 500;
    public decimal MotionMax { get; init; } = 8;
    public decimal MotionBfactor { get; init; } = -500;
    public int Voltage { get; init; } = 300;
    public decimal Cs { get; init; } = 2.7m;
    public decimal Cc { get; init; } = 2.7m;
    public decimal Amplitude { get; init; } = .07m;
    public decimal IlluminationAperture { get; init; } = 30;
    public decimal EnergySpread { get; init; } = .7m;
    public int CtfWindow { get; init; } = 512;
    public decimal CtfQuality { get; init; } = .8m;
    public int CtfGridX { get; init; } = 5;
    public int CtfGridY { get; init; } = 5;
    public decimal DefocusMin { get; init; } = .3m;
    public decimal DefocusMax { get; init; } = 5;
    public bool FitPhase { get; init; }
    public bool FitThickness { get; init; } = true;
    public bool UseMovieSum { get; init; }
    public decimal Thickness { get; init; }
    public bool ExportAverage { get; init; } = true;
    public bool ExportStack { get; init; }
    public int StackGroup { get; init; } = 1;
    public int SkipFirst { get; init; }
    public int SkipLast { get; init; }
    public decimal DeconvolutionStrength { get; init; } = 1;
    public decimal DeconvolutionFalloff { get; init; } = 1;
    public bool Denoise { get; init; } = true;
    public int FineTuneEvery { get; init; } = 1000;
    public int TrainCount { get; init; } = 100;
    public int TrainingIterations { get; init; } = 1000;
}
