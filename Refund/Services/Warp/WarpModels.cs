using System.Text.Json.Serialization;
namespace Refund.Services.Warp;

public sealed record WarpSessionIdentity(int ProjectId,int SpaceId,int JobId,string SessionDirectory,string QueueDirectory)
{
    public string Key => $"{ProjectId}/{SpaceId}/{JobId}";
}
[Flags] public enum WarpStage { None=0,Motion=1,Ctf=2,Enhance=4,Pick=8,Extract=16,All=31 }
public enum WarpDisposition { Auto,Keep,Exclude }
public sealed record WarpSettings
{
    public decimal Pixel {get;init;}=1;
    public decimal TargetPixelSize {get;init;}=1;
    public decimal TotalDose {get;init;}=40;
    public string InputPath {get;init;}="";
    public string OutputPath {get;init;}="";
    public string InputPattern {get;init;}="*.eer";
    public decimal MinimumFileSizeMb {get;init;}
    public string GainPath {get;init;}="";
    public string Preset {get;init;}="";
    public int EerGrouping {get;init;}=40;
    public int MotionGrid {get;init;}=5;
    public bool MotionGridAuto {get;init;}=true;
    public bool CtfGridAuto {get;init;}=true;
    public decimal CtfMin {get;init;}=30;
    public decimal CtfMax {get;init;}=4;
    public decimal Diameter {get;init;}=140;
    public decimal MinSeparation {get;init;}=.8m;
    public bool PickingEnabled {get;init;}=true;
    public int Box {get;init;}=256;
    [JsonIgnore] public int TrainAfter {get;init;}=100;
    public bool FineTune {get;init;}
    public bool Recursive { get; init; }
    [JsonIgnore] public decimal BinTimes { get; init; }
    public bool CorrectGain { get; init; }
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
    public int CtfWindow { get; init; } = 512;
    public int CtfGridX { get; init; } = 5;
    public int CtfGridY { get; init; } = 5;
    public decimal DefocusMin { get; init; } = .3m;
    public decimal DefocusMax { get; init; } = 5;
    public bool FitPhase { get; init; }
    public bool FitThickness { get; init; }
    public bool UseMovieSum { get; init; }
    public bool ExportStack { get; init; }
    public int StackGroup { get; init; } = 1;
    public int SkipFirst { get; init; }
    public int SkipLast { get; init; }
    // Noise2Mic's settings for the BoxNet picking corpus; the denoiser and picker expect images made this way.
    public decimal DeconvolutionStrength { get; init; } = .9m;
    public decimal DeconvolutionFalloff { get; init; } = .1m;
    public bool Denoise { get; init; } = true;
    public int FineTuneEvery { get; init; } = 1000;
    public int TrainCount { get; init; } = 100;
    /// <summary>Noise2Mic's recipe: 4 patches per training movie per epoch, 8 epochs.</summary>
    public int TrainingEpochs { get; init; } = 8;
    /// <summary>A trained Noise2Mic model the session's first training fine-tunes, e.g. the BoxNet corpus model; "" trains from scratch.</summary>
    public string DenoiserStart { get; init; } = "";

}
public sealed record WarpMovie
{
    public int Index {get;set;}
    public string Id {get;set;}="";
    public string Name {get;set;}="";
    public string InputPath {get;set;}="";
    public string ProcessingPath {get;set;}="";
    public string MetadataPath {get;set;}="";
    public string AveragePath {get;set;}="";
    public string EnhancedPath {get;set;}="";
    public string DeconvolvedPath {get;set;}="";
    public string ThumbnailPath {get;set;}="";
    public string ParticleStarPath {get;set;}="";
    public string ParticleStackPath {get;set;}="";
    public string TrainingOddPath {get;set;}="";
    public string TrainingEvenPath {get;set;}="";
    public double PixelSize {get;set;}
    public double EnhancedPixelSize {get;set;}
    public double Defocus {get;set;}=double.NaN;
    public double Ctf {get;set;}=double.NaN;
    public double Motion {get;set;}=double.NaN;
    public double Ice {get;set;}=double.NaN;
    public double Astigmatism {get;set;}=double.NaN;
    public double AstigmatismAngle {get;set;}=double.NaN;
    public int Picks {get;set;}
    public WarpStage Done {get;set;}
    public WarpStage Stale {get;set;}
    public bool Failed {get;set;}
    public bool Excluded {get;set;}
    public WarpDisposition Disposition {get;set;}
    public string Error {get;set;}="";
    public string ActiveStage {get;set;}="";
    public long ResultRevision {get;set;}
    public long MotionRevision {get;set;}
    public long CtfRevision {get;set;}
    public long PickingRevision {get;set;}
    public bool TrainingPairReady {get;set;}
    /// <summary>Version of the denoiser that produced this movie's denoised micrograph; 0 if none.</summary>
    public int DenoiseModel {get;set;}
    public bool InputReady {get;set;}=true;
    public long SourceLength {get;set;}
    public long SourceWriteTicks {get;set;}
    public DateTime DiscoveredAt {get;set;}
    public Dictionary<string,ulong> Completed {get;set;}=[];
}
public readonly record struct WarpParticle(double X,double Y,double Correlation,double Power,int RowIndex);
public sealed record WarpParticleHistogram(int[] Bins,int Columns,int Rows,double PowerMaximum,long Total,long Accepted,int Movies);
public sealed record WarpExportManifest(string StarPath,bool Particles,int MovieCount,long ParticleCount,WarpSettings Settings,string SessionDirectory,DateTime Created,string ProcessedItemsJson,bool HasParticleData,string ProcessingDirectory,string DataDirectory);
public sealed record WarpCalibrationPreset(string Name,Dictionary<string,System.Text.Json.JsonElement> Values);
public sealed record WarpInputCheck(string Item,bool? Passed,string Detail);
