using System.Text.Json.Serialization;
namespace Refund.Services.Warp;

/// <param name="SpaceDirectory">The root of the session's space, which jobs run in; the movies' metadata names their data
/// folder relative to it. Null keeps that folder absolute.</param>
public sealed record WarpSessionIdentity(int ProjectId,int SpaceId,int JobId,string SessionDirectory,string QueueDirectory,string SpaceDirectory=null)
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
    /// <summary>The picks extraction keeps; picks outside are never extracted, so changing these redoes extraction only.</summary>
    public WarpParticleBounds ParticleBounds {get;init;}=new();
    public int Box {get;init;}=256;
    /// <summary>Saves particles Fourier-cropped to <see cref="RescaledBox"/>: the same field of view at a coarser pixel size.</summary>
    public bool RescaleParticles {get;init;}
    public int RescaledBox {get;init;}=128;
    /// <summary>The box extracted particles are saved in.</summary>
    [JsonIgnore] public int ParticleBox=>RescaleParticles?RescaledBox:Box;
    /// <summary>The pixel size of saved particles, Å.</summary>
    [JsonIgnore] public decimal ParticlePixelSize=>ParticleBox>0?TargetPixelSize*Box/ParticleBox:TargetPixelSize;
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
    /// <summary>Exports put micrographs in optics groups by EPU's beam shift (<see cref="WarpEpu"/>); processing ignores it.</summary>
    public bool EpuOpticsGroups { get; init; } = true;
    /// <summary>Where EPU's per-exposure XMLs are when not beside the movies; searched recursively. "" looks beside them only.</summary>
    public string EpuMetadataPath { get; init; } = "";
    /// <summary>
    /// How far an exposure's beam shift may be from a group's mean to join it, in EPU's units. The default keeps each hole
    /// of a multi-hole pattern together (shots within a hole are ~0.025 apart, holes ~0.1); ~0.01 separates shots.
    /// </summary>
    public decimal EpuGroupRadius { get; init; } = .05m;
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
    public int SkipFirst { get; init; }
    public int SkipLast { get; init; }
    public bool Denoise { get; init; } = true;
    public int FineTuneEvery { get; init; } = 1000;
    public int TrainCount { get; init; } = 100;
    /// <summary>
    /// Epochs of 4 patches per training movie. Fine-tuning (the usual case, from a starting model or the session's
    /// last one) needs 4; Noise2Mic trained from scratch for 8.
    /// </summary>
    public int TrainingEpochs { get; init; } = 4;
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
    public string ThumbnailPath {get;set;}="";
    public string ParticleStarPath {get;set;}="";
    public string ParticleStackPath {get;set;}="";
    public double PixelSize {get;set;}
    public double EnhancedPixelSize {get;set;}
    public double Defocus {get;set;}=double.NaN;
    public double Ctf {get;set;}=double.NaN;
    /// <summary>How clearly the spectrum shows the fitted CTF (Warp's CtfSignal score); NaN until measured.</summary>
    public double CtfSignal {get;set;}=double.NaN;
    public double Motion {get;set;}=double.NaN;
    public double Ice {get;set;}=double.NaN;
    public double Astigmatism {get;set;}=double.NaN;
    public double AstigmatismAngle {get;set;}=double.NaN;
    /// <summary>Blob picks detected on the micrograph, before the particle score bounds.</summary>
    public int Picks {get;set;}
    /// <summary>Particles extracted: the picks within the score bounds when extraction ran.</summary>
    public int Particles {get;set;}
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
    public long ExtractionRevision {get;set;}
    /// <summary>EPU's beam shift for the exposure, read when first exported with EPU optics groups; null until found.</summary>
    public WarpBeamShift BeamShift {get;set;}
    public bool TrainingPairReady {get;set;}
    /// <summary>Version of the denoiser that produced this movie's denoised micrograph; 0 if none.</summary>
    public int DenoiseModel {get;set;}
    public bool InputReady {get;set;}=true;
    public long SourceLength {get;set;}
    public long SourceWriteTicks {get;set;}
    public DateTime DiscoveredAt {get;set;}
    public Dictionary<string,ulong> Completed {get;set;}=[];
    /// <summary>The extracted picks' rows, in the particle stack's order (written by extraction next to the stack).</summary>
    [JsonIgnore] public string ExtractedStarPath=>string.IsNullOrEmpty(ParticleStackPath)?"":Path.ChangeExtension(ParticleStackPath,".star");
}
/// <summary>
/// Pearson correlation with the blob template and local power a pick needs to be extracted, inclusive; a null maximum
/// power is unbounded.
/// </summary>
public sealed record WarpParticleBounds(double MinCorrelation=0,double MaxCorrelation=1,double MinPower=0,double? MaxPower=null)
{
    public bool Contains(WarpParticle p)=>p.Correlation>=MinCorrelation&&p.Correlation<=MaxCorrelation&&p.Power>=MinPower&&(!MaxPower.HasValue||p.Power<=MaxPower.Value);
    [JsonIgnore] public bool Valid=>double.IsFinite(MinCorrelation)&&double.IsFinite(MaxCorrelation)&&double.IsFinite(MinPower)&&(!MaxPower.HasValue||double.IsFinite(MaxPower.Value))&&
        MinCorrelation>=0&&MinCorrelation<=MaxCorrelation&&MaxCorrelation<=1&&MinPower>=0&&(!MaxPower.HasValue||MaxPower.Value>=MinPower);
    /// <summary>Clamped into a valid range: correlation within 0–1, nonnegative power, minimums at most maximums.</summary>
    public WarpParticleBounds Clamped()
    {
        double min=Math.Clamp(double.IsFinite(MinCorrelation)?MinCorrelation:0,0,1),power=Math.Max(0,double.IsFinite(MinPower)?MinPower:0);
        return new(min,Math.Clamp(double.IsFinite(MaxCorrelation)?MaxCorrelation:1,min,1),power,MaxPower is {} max&&double.IsFinite(max)?Math.Max(power,max):null);
    }
}
public readonly record struct WarpParticle(double X,double Y,double Correlation,double Power,int RowIndex);
public sealed record WarpParticleHistogram(int[] Bins,int Columns,int Rows,double PowerMaximum,long Total,int Movies)
{
    /// <summary>Picks within the bounds, estimated from bin centres; extraction applies the exact limits.</summary>
    public long CountWithin(WarpParticleBounds bounds)
    {
        long count=0;
        for(int y=0;y<Rows;y++)for(int x=0;x<Columns;x++)
            if(Bins[y*Columns+x] is int n&&n>0&&bounds.Contains(new WarpParticle(0,0,(x+.5)/Columns,(y+.5)*PowerMaximum/Rows,0)))count+=n;
        return count;
    }
}
/// <param name="Filters">The filter thresholds that decided which movies were accepted; null in exports made before they were recorded.</param>
/// <param name="InputListPath">The exported movies, one path per line in <paramref name="DataDirectory"/>, for WarpTools'
/// --input_data; null for exports from before they were listed, whose data directory held only their movies.</param>
/// <param name="OpticsGroups">How the export's rows were put in optics groups; null for exports from before groups.</param>
public sealed record WarpExportManifest(string StarPath,bool Particles,int MovieCount,long ParticleCount,WarpSettings Settings,string SessionDirectory,DateTime Created,string ProcessedItemsJson,bool HasParticleData,string ProcessingDirectory,string DataDirectory,WarpFilters Filters=null,string InputListPath=null,WarpOpticsGroups OpticsGroups=null)
{
    /// <summary>The manifest with its files relative to <paramref name="root"/> (the space's), as export jobs save it.</summary>
    public WarpExportManifest RelativeTo(string root)=>MapPaths(path=>WarpPaths.Relative(root,path));
    /// <summary>A saved manifest with its files as absolute paths under <paramref name="root"/>; older manifests saved them absolute.</summary>
    public WarpExportManifest ResolvedIn(string root)=>MapPaths(path=>WarpPaths.Resolve(root,path));
    internal WarpExportManifest MapPaths(Func<string,string> map)=>this with
    {
        StarPath=map(StarPath),SessionDirectory=map(SessionDirectory),ProcessedItemsJson=map(ProcessedItemsJson),
        ProcessingDirectory=map(ProcessingDirectory),DataDirectory=map(DataDirectory),InputListPath=map(InputListPath),
        // The raw data's paths (input, gain, defects, EPU metadata) are outside the space and stay as they are.
        Settings=Settings==null?null:Settings with{OutputPath=map(Settings.OutputPath)}
    };
}
/// <summary>An export's optics groups: from EPU beam shift (<paramref name="FromEpu"/>) or one group for everything.</summary>
/// <param name="WithoutMetadata">Exported micrographs without EPU metadata, together in a group of their own.</param>
public sealed record WarpOpticsGroups(bool FromEpu,int Groups,int WithoutMetadata);
public sealed record WarpCalibrationPreset(string Name,Dictionary<string,System.Text.Json.JsonElement> Values);
/// <summary>
/// What a Warp session's card and header show, readable without loading the session: the session keeps it current while
/// loaded and saves it beside its checkpoint (warp-summary.json).
/// </summary>
/// <param name="StatusCounts">Movies per status, in <see cref="WarpSession.StatusNames"/> order.</param>
/// <param name="ProcessingRate">Movies per hour while processing; null otherwise.</param>
public sealed record WarpSessionSummary(int[] StatusCounts,DateTime? LastInputAt,int Received,int Pending,int Outdated,int Complete,
    int Accepted,int Failed,long ParticleCount,double? ProcessingRate,bool Running,bool Denoise,bool Training,int TrainingPercent,
    bool HasModel,int Model,int TrainingMoviesReady,int TrainAfter);
public sealed record WarpInputCheck(string Item,bool? Passed,string Detail);
