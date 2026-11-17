using System.Text.Json;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using Refund.Components.ThumbnailPanel;

namespace Refund.Services.Warp;

internal sealed record WarpCheckpoint(int Schema,WarpSettings Settings,IReadOnlyList<WarpMovie> Movies,
    List<WarpExportManifest> Exports,string ModelPath,int Model,int LastTrainingCount,DateTime? LastInputAt,DateTime? LastResultAt,
    WarpFilters Filters,WarpHistogramCheckpoint Histogram,ulong ModelPreparationKey=0,int MinDenoiseModel=0);
internal sealed record WarpHistogramCheckpoint(int Schema,WarpParticleHistogram Histogram,string CohortSignature);
public sealed record WarpFilters(double MinCtf=0,double MaxCtf=5,double MinMotion=0,double MaxMotion=2.5,
    double MinIce=0,double MaxIce=2000,int MinParticles=0,int MaxParticles=int.MaxValue,double MaxAstigmatismSigma=3,
    double MinCtfSignal=global::Warp.Tools.CtfSignal.DefaultThreshold,double? MaxCtfSignal=null);

public sealed partial class WarpSession : IDisposable
{
    private readonly object _sync=new();
    private readonly Serilog.ILogger _logger;
    public WarpSessionIdentity Identity {get;}
    private readonly List<WarpMovie> _movies=[];
    private ImmutableList<WarpMovie> _view=ImmutableList<WarpMovie>.Empty;
    private readonly HashSet<int> _changedRows=[];
    private readonly Queue<string> _terminalFiles=[];
    private readonly SemaphoreSlim _saveGate=new(1,1);
    private readonly CancellationTokenSource _lifetime=new();
    private Task _checkpointLoop;
    private long _mutationVersion;
    private readonly Dictionary<string,WarpMovie> _byInput=new(StringComparer.Ordinal);
    private readonly List<WarpExportManifest> _exports=[];
    private CancellationTokenSource _runCancellation;
    private bool _disposed,_dirty,_plotDirty;
    private string _modelPath="";
    private int _lastTrainingCount;
    /// <summary>Denoising from a model older than this is outdated; set by <see cref="RedoDenoising"/>.</summary>
    private int _minDenoiseModel;
    private ulong _modelPreparationKey;
    private WarpFilters _filters=new();
    private WarpParticleHistogram _histogram;
    private string _histogramCohort="";
    private readonly Dictionary<int,(long Revision,WarpParticle[] Particles)> _particleCache=[];
    private readonly Queue<int> _particleCacheOrder=[];
    private static readonly JsonSerializerOptions JsonOptions=new(){NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals};
    public event Action Changed;
    public WarpSettings Settings {get;private set;}=new();
    public IReadOnlyList<WarpMovie> Movies=>_view;
    /// <summary>
    /// One past the last movie with any result, partial or outdated; 0 before the first. Movies discovered after it
    /// have nothing to show yet.
    /// </summary>
    public int ProcessedEnd{get{var view=_view;for(int i=view.Count-1;i>=0;i--)if(view[i].Done!=0)return i+1;return 0;}}
    /// <summary>Exports the session compiled itself before export jobs wrote their own; kept so its checkpoint still records their files.</summary>
    public IReadOnlyList<WarpExportManifest> Exports {get{lock(_sync)return _exports.ToArray();}}
    public bool Running {get;private set;}
    public bool Training {get;private set;}
    public int TrainingPercent {get;private set;}
    /// <summary>The running training's last reported step, from the progress file its worker writes; null until the first report.</summary>
    public global::Warp.Tools.TrainingProgress TrainingProgress {get;private set;}
    /// <summary>Version of the newest denoiser; it keeps counting when a model file goes missing.</summary>
    public int Model {get;private set;}
    /// <summary>A trained model is on disk, so movies can be denoised.</summary>
    public bool HasModel=>Model>0&&_modelPath.Length>0;
    private int _exportOperations;
    /// <summary>An export job is writing this session's selection.</summary>
    public bool Exporting {get{lock(_sync)return _exportOperations>0;}}
    /// <summary>The latest problem that needs attention (a failed run, training or checkpoint), or "".</summary>
    public string Problem=>_checkpointProblem.Length>0?_checkpointProblem:_runProblem.Length>0?_runProblem:_discoveryProblem;
    private string _runProblem="",_checkpointProblem="",_discoveryProblem="";
    public DateTime? StartedAt {get;private set;}
    public DateTime? LastInputAt {get;private set;}
    public DateTime? LastResultAt {get;private set;}
    public DateTime SavedAt {get;private set;}
    public int CatalogVersion {get;private set;}
    public int PlotVersion {get;private set;}
    public int Accepted {get;private set;}
    /// <summary>Movies with current results from every stage the settings require; accepted movies are a subset.</summary>
    public int Complete {get;private set;}
    public int Pending {get;private set;}
    public int Failed {get;private set;}
    public int Outdated {get;private set;}
    public long ParticleCount {get;private set;}
    /// <summary>Movies counted as processed at the last refresh, to tell when one newly becomes so.</summary>
    private readonly HashSet<int> _completeMovies=[];
    /// <summary>When movies became processed during the current run, oldest first (see <see cref="ProcessingRate"/>).</summary>
    private readonly Queue<DateTime> _completions=new();
    internal static readonly TimeSpan RateWindow=TimeSpan.FromMinutes(10);
    /// <summary>
    /// Movies newly processed per hour over the last 10 minutes of the current run, or since it started if that is more
    /// recent; null while stopped and during a run's first minute. Once caught up, it is the rate movies arrive at.
    /// </summary>
    public double? ProcessingRate{get{lock(_sync)return Rate(_completions,DateTime.UtcNow,Running?StartedAt:null);}}
    internal static double? Rate(Queue<DateTime> completions,DateTime now,DateTime? startedAt)
    {
        while(completions.Count>0&&now-completions.Peek()>RateWindow)completions.Dequeue();
        if(startedAt is not {} start)return null;
        var span=now-(start>now-RateWindow?start:now-RateWindow);
        return span<TimeSpan.FromMinutes(1)?null:completions.Count/span.TotalHours;
    }
    public int[] StageCounts {get;}=new int[5];
    /// <summary>Movies with a current CTF that wait for the first model to denoise them.</summary>
    public int AwaitingDenoiser=>Settings.Denoise && !HasModel ? _view.Count(m=>!m.Failed&&Current(m,"average")&&Current(m,"ctf")&&!m.Done.HasFlag(WarpStage.Enhance)):0;
    /// <summary>Movies whose half-averages can train the denoiser; the first training starts once there are <see cref="WarpSettings.TrainAfter"/>.</summary>
    public int TrainingMoviesReady=>_view.Count(TrainingReady);
    public string DenoiserStatus=>!Settings.Denoise?"Off":Training?"Training denoiser":HasModel?$"Model v{Model}":$"Collecting movies to train the denoiser: {Math.Min(TrainingMoviesReady,Settings.TrainAfter):N0} of {Settings.TrainAfter:N0}";
    public static readonly string[] StatusNames=["Unprocessed","Partial","Ready","Outdated","Filtered","Kept","Excluded","Failed"];
    private string CheckpointPath=>Path.Combine(Identity.SessionDirectory,"warp-mini.json");
    /// <summary>The session's <see cref="WarpSessionSummary"/>, saved beside its checkpoint for cards of unloaded sessions.</summary>
    public const string SummaryFile="warp-summary.json";
    private int[] _statusCounts;private long _statusCountsVersion=-1;
    /// <summary>What the card and header show, as of now.</summary>
    public WarpSessionSummary Summary
    {
        get
        {
            lock(_sync)
            {
                // Counting statuses visits every movie; it changes only with the session.
                if(_statusCounts==null||_statusCountsVersion!=_mutationVersion)
                {
                    var counts=new int[StatusNames.Length];
                    foreach(var movie in _view)counts[Array.IndexOf(StatusNames,Status(movie))]++;
                    _statusCounts=counts;_statusCountsVersion=_mutationVersion;
                }
                return new(_statusCounts.ToArray(),LastInputAt,_view.Count,Pending,Outdated,Complete,Accepted,Failed,ParticleCount,
                    Rate(_completions,DateTime.UtcNow,Running?StartedAt:null),Running,Settings.Denoise,Training,TrainingPercent,HasModel,Model,
                    TrainingMoviesReady,Settings.TrainAfter);
            }
        }
    }
    /// <summary>Writes <see cref="Summary"/> to <see cref="SummaryFile"/>, replacing the previous one in one step.</summary>
    internal void WriteSummary()
    {
        string path=Path.Combine(Identity.SessionDirectory,SummaryFile),temporary=path+".writing";
        File.WriteAllText(temporary,JsonSerializer.Serialize(Summary,JsonOptions));
        File.Move(temporary,path,true);
    }
    private WarpStage RequiredStages=>Settings.PickingEnabled?WarpStage.All:WarpStage.Motion|WarpStage.Ctf|WarpStage.Enhance;

    internal WarpSession(WarpSessionIdentity identity,Serilog.ILogger logger,WarpSettings initialSettings=null)
    {
        Identity=identity;_logger=logger;
        Directory.CreateDirectory(identity.SessionDirectory);
        if(File.Exists(CheckpointPath))
        {
            try
            {
                var saved=JsonSerializer.Deserialize<WarpCheckpoint>(File.ReadAllText(CheckpointPath),JsonOptions);
                if(saved?.Schema!=1)throw new InvalidDataException("Unsupported Warp session checkpoint schema.");
                // Paths are saved relative to the session's folder; older checkpoints saved them absolute, under the folder
                // their output path names, and move with the session if it has moved since.
                string root=identity.SessionDirectory,savedRoot=Path.IsPathRooted(saved.Settings.OutputPath)?Path.GetDirectoryName(saved.Settings.OutputPath):null;
                string Restore(string path)=>WarpPaths.Restore(root,savedRoot,path);
                Settings=NormalizeSettings(saved.Settings) with{OutputPath=Path.Combine(root,"processing")};
                _movies.AddRange(saved.Movies.Select(m=>MapPaths(m,Restore)));
                _exports.AddRange(saved.Exports.Select(e=>e.MapPaths(Restore)));
                _modelPreparationKey=saved.ModelPreparationKey;Model=saved.Model;_modelPath=Restore(saved.ModelPath??"");_lastTrainingCount=saved.LastTrainingCount;_minDenoiseModel=saved.MinDenoiseModel;
                LastInputAt=saved.LastInputAt;LastResultAt=saved.LastResultAt;_filters=saved.Filters??new();
                foreach(var m in _movies){m.ActiveStage="";_byInput[m.InputPath]=m;}
                foreach(var m in _movies)_changedRows.Add(m.Index);PublishRows();
                // A missing model file only stops new denoising until the next model; existing results stay valid.
                if(_modelPath.Length>0&&!File.Exists(_modelPath))_modelPath="";
                // Checkpoints from before ModelKey recorded the deconvolution key the model was trained under.
                RecomputeStageKeys();BuildStageKeys(Settings,out ulong legacyPrepare);
                if(Model>0&&_modelPreparationKey!=ModelKey&&_modelPreparationKey!=legacyPrepare)ForgetModel();
                else if(Model>0)_modelPreparationKey=ModelKey;
                RefreshStates();
                if(saved.Histogram is {Schema:1} h && h.CohortSignature==ParticleCohortSignature() &&
                    h.Histogram.Bins?.Length==64*48 && h.Histogram.Bins.All(v=>v>=0) && h.Histogram.Bins.Sum(v=>(long)v)==h.Histogram.Total)
                {_histogram=h.Histogram;_histogramCohort=h.CohortSignature;foreach(var m in _movies.Where(HistogramEligible))_histogramMembers[m.Index]=m.PickingRevision;}
                PublishRows();
                SavedAt=File.GetLastWriteTimeUtc(CheckpointPath);
            }
            catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException)
            { _logger.Error(ex,"Cannot restore Warp session {Session}",identity.Key);throw new InvalidDataException("Warp session checkpoint could not be restored; original file has been retained.",ex); }
        }
        else
        {
            Settings=NormalizeSettings(initialSettings??Settings) with{OutputPath=Path.Combine(identity.SessionDirectory,"processing")};RecomputeStageKeys();
            // A cleared session keeps its filter thresholds (see KeepFilters); a new one starts from the defaults.
            _filters=ReadSavedFilters(identity.SessionDirectory)??new();
        }
        _checkpointLoop=Task.Run(async()=>{try{while(!_lifetime.IsCancellationRequested){await Task.Delay(5000,_lifetime.Token);if(!Running)UpdateHistogramBatch();if(_dirty)await SaveAsync();}}catch(OperationCanceledException){}catch(Exception ex){_logger.Error(ex,"Warp session checkpoint loop failed");}});
    }

    public void Apply(WarpSettings settings)
    {
        lock(_sync)
        {
            if(Running)throw new InvalidOperationException("Stop processing before changing settings.");
            var error=ValidateSettings(settings);if(error!=null)throw new ArgumentException(error);
            var next=NormalizeSettings(settings) with{OutputPath=Path.Combine(Identity.SessionDirectory,"processing")};
            var nextKeys=BuildStageKeys(next);
            bool processingChanged=StageOrder.Any(stage=>_stageKeys[stage]!=nextKeys[stage]);
            // The histogram shows every pick's scores, so only new picks make it start over (not new score bounds).
            bool picksChanged=_stageKeys["pick"]!=nextKeys["pick"];
            bool modelChanged=Model>0&&ModelKeyFor(next)!=ModelKey;
            Settings=next;RecomputeStageKeys();
            // The model learned from inputs prepared the old way; the next one trains from the starting model (or scratch).
            if(modelChanged)ForgetModel();
            if (processingChanged)
            {
                foreach (var movie in _movies.Where(m => m.Failed))
                {
                    movie.Failed = false; movie.Error = ""; _changedRows.Add(movie.Index);
                }
            }
            if(picksChanged)ResetHistogram();
            RefreshStates();Save();
        }
        SaveAsync().GetAwaiter().GetResult();Publish();
    }
    public string GetSettingsImpact(WarpSettings next)
    {
        if(next==Settings)return "";
        var before=BuildStageKeys(Settings);var after=BuildStageKeys(NormalizeSettings(next));
        string[] changed=StageOrder.Where(k=>before[k]!=after[k]).ToArray();
        string retrain=Model>0&&ModelKeyFor(NormalizeSettings(next))!=ModelKey?" Train a new denoiser.":"";
        if(changed.Length>0)return "Redo "+string.Join(", ",changed.Select(StageLabel))+"."+retrain;
        // Optics groups are made when exporting: they change exports run from now on, never processing.
        return ChangedSettings(next,Settings).All(EpuSettings.Contains)?"Retain current results; exports from now on use the new optics groups.":"Update discovery and training schedule; retain current results.";
    }
    private static readonly HashSet<string> EpuSettings=[nameof(WarpSettings.EpuOpticsGroups),nameof(WarpSettings.EpuMetadataPath),nameof(WarpSettings.EpuGroupRadius)];
    /// <summary>What applying these particle score bounds redoes: extraction, never picking.</summary>
    public string GetParticleBoundsImpact(WarpParticleBounds bounds)
    {
        if(bounds==Settings.ParticleBounds)return "";
        int extracted=_view.Count(m=>m.Done.HasFlag(WarpStage.Extract));
        return extracted==0?"Extraction will use them. Picking is not redone.":$"Redo extraction for {extracted:N0} {(extracted==1?"micrograph":"micrographs")}. Picking is not redone.";
    }
    public WarpStage ActiveStale(WarpMovie m)=>m.Stale&RequiredStages;
    public bool PassesFilters(WarpMovie m)=>
        (!m.Done.HasFlag(WarpStage.Ctf)||(m.Ctf>=MinCtf&&m.Ctf<=MaxCtf)&&!(m.CtfSignal<MinCtfSignal)&&!(m.CtfSignal>MaxCtfSignal)&&(!double.IsFinite(m.Ice)||m.Ice>=MinIce&&m.Ice<=MaxIce)&&(!(AstigmatismSigma(m)>MaxAstigmatismSigma)))&&
        (!m.Done.HasFlag(WarpStage.Motion)||m.Motion>=MinMotion&&m.Motion<=MaxMotion)&&
        // Without picking, a particle range set earlier no longer applies.
        (!Settings.PickingEnabled||MinParticles==0&&MaxParticles==int.MaxValue||m.Done.HasFlag(WarpStage.Extract)&&m.Particles>=MinParticles&&m.Particles<=MaxParticles);
    public bool Passes(WarpMovie m)=>!m.Failed&&!m.Excluded&&m.Disposition!=WarpDisposition.Exclude&&(m.Disposition==WarpDisposition.Keep||PassesFilters(m));
    public string Status(WarpMovie m)=>m.Failed?"Failed":m.Excluded||m.Disposition==WarpDisposition.Exclude?"Excluded":m.Done==0?"Unprocessed":ActiveStale(m)!=0?"Outdated":m.Disposition==WarpDisposition.Keep?"Kept":!PassesFilters(m)?"Filtered":(m.Done&RequiredStages)==RequiredStages?"Ready":"Partial";
    public string StatusColor(WarpMovie m)=>StatusColorFor(Status(m));
    public static string StatusColorFor(string status)=>StatusColorMapping.GetColor(status switch{"Failed"=>ProcessingStatus.Failed,"Kept"=>ProcessingStatus.Kept,"Excluded"=>ProcessingStatus.Deselected,"Outdated"=>ProcessingStatus.Outdated,"Filtered"=>ProcessingStatus.FilteredOut,"Ready"=>ProcessingStatus.Processed,"Partial"=>ProcessingStatus.Partial,_=>ProcessingStatus.Unprocessed});
    public void SetDisposition(IEnumerable<int> indices,WarpDisposition value)=>Mutate(()=>{foreach(int i in indices.Distinct())if(i>=0&&i<_movies.Count){_movies[i].Disposition=value;_movies[i].Excluded=value==WarpDisposition.Exclude;_changedRows.Add(i);}});
    public void Retry(IEnumerable<int> indices)=>Mutate(()=>{foreach(int i in indices.Distinct())if(i>=0&&i<_movies.Count){_movies[i].Failed=false;_movies[i].Error="";_changedRows.Add(i);}});
    /// <summary>A model trained on other deconvolution settings no longer fits the data, so training starts over.</summary>
    private void ForgetModel(){_modelPath="";Model=0;_lastTrainingCount=0;_minDenoiseModel=0;}
    /// <summary>
    /// The deconvolution the denoiser's inputs go through: Noise2MicTorch's fixed values, as decimals written the way the
    /// former settings were, so the keys of sessions from then stay the same.
    /// </summary>
    public const decimal DeconvolutionStrength=.9m,DeconvolutionFalloff=.1m;
    /// <summary>
    /// What a trained denoiser depends on: the deconvolution its inputs went through, and whether objects were erased from
    /// them. Motion, averaging and CTF changes redo deconvolution and denoising with the same model; a new model can still
    /// be trained on demand. Sessions whose model was trained with the deconvolution formerly set otherwise forget it on
    /// load; changing <see cref="WarpSettings.EraseObjects"/> forgets it on <see cref="Apply"/>.
    /// </summary>
    private ulong ModelKey=>ModelKeyFor(Settings);
    private static ulong ModelKeyFor(WarpSettings s)=>s.EraseObjects
        ?Fingerprint(new{DeconvolutionStrength,DeconvolutionFalloff})
        :Fingerprint(new{DeconvolutionStrength,DeconvolutionFalloff,s.EraseObjects});
    /// <summary>Denoised movies whose model is older than the current one.</summary>
    public int OlderModelMovies=>Settings.Denoise&&HasModel?_view.Count(m=>!m.Failed&&Current(m,"denoise")&&m.DenoiseModel<Model):0;
    /// <summary>Outdates denoising, picking and extraction for every movie denoised with an older model.</summary>
    public void RedoDenoising()=>Mutate(()=>
    {
        if(!Settings.Denoise||!HasModel)throw new InvalidOperationException("There is no denoiser model to redo with.");
        _minDenoiseModel=Model;
    });
    public void Train()=>Mutate(()=>{if(!Running)throw new InvalidOperationException("Start processing before training.");_trainRequested=true;_trainingFailed=false;});
    private void Mutate(Action action){lock(_sync){action();_plotDirty=true;RefreshStates();Save();}Publish();}
    private void Publish()
    {
        lock (_sync) { PublishRows(); if (_plotDirty) { PlotVersion++; _plotDirty = false; } }
        var handlers = Changed;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception ex) { _logger.Warning(ex, "Warp session view notification failed"); }
        }
    }
    private void RefreshStates()
    {
        RefreshAstigmatism();
        Accepted=Complete=Pending=Failed=Outdated=0;ParticleCount=0;Array.Clear(StageCounts);var now=DateTime.UtcNow;
        foreach(var m in _movies)
        {
            var priorStale=m.Stale;m.Stale=0;
            foreach(string stage in StageOrder)if(m.Completed.TryGetValue(stage,out ulong completed)&&(completed!=_stageKeys[stage]||RedoneByModel(m,stage)))m.Stale|=StageFlag(stage);
            if(priorStale!=m.Stale)_changedRows.Add(m.Index);
            if(m.Failed)Failed++;
            // Processed, and so accepted, means current results from every stage the settings require.
            bool complete=false;
            if(ActiveStale(m)!=0)Outdated++;
            else if(!m.Failed&&(m.Done&RequiredStages)!=RequiredStages)Pending++;
            else if(!m.Failed){Complete++;complete=true;}
            // Only movies finishing while processing runs count towards the rate, not ones restored or already done.
            if(!complete)_completeMovies.Remove(m.Index);
            else if(_completeMovies.Add(m.Index)&&Running)_completions.Enqueue(now);
            if(complete&&Passes(m)){Accepted++;if(m.Done.HasFlag(WarpStage.Extract))ParticleCount+=m.Particles;}
            for(int k=0;k<5;k++)if(m.Done.HasFlag((WarpStage)(1<<k))&&!m.Stale.HasFlag((WarpStage)(1<<k)))StageCounts[k]++;
        }
    }
    private void PublishRows()
    {
        if(_changedRows.Count>0)_plotDirty=true;
        foreach(int index in _changedRows.OrderBy(i=>i))
        {
            var row=_movies[index] with{Completed=new Dictionary<string,ulong>(_movies[index].Completed)};
            _view=index<_view.Count?_view.SetItem(index,row):_view.Add(row);
        }
        _changedRows.Clear();
    }
    private void Save(){PublishRows();_dirty=true;_mutationVersion++;}
    /// <summary>The row with the session's files for it mapped; its raw movie (<see cref="WarpMovie.InputPath"/>) stays as it is.</summary>
    private static WarpMovie MapPaths(WarpMovie m,Func<string,string> map)=>m with
    {
        ProcessingPath=map(m.ProcessingPath),MetadataPath=map(m.MetadataPath),AveragePath=map(m.AveragePath),EnhancedPath=map(m.EnhancedPath),
        ThumbnailPath=map(m.ThumbnailPath),ParticleStarPath=map(m.ParticleStarPath),ParticleStackPath=map(m.ParticleStackPath)
    };
    private async Task SaveAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            WarpCheckpoint saved;long version;string[] terminalFiles;
            lock(_sync)
            {
                if(!_dirty)return;
                PublishRows();GetParticleHistogram();version=_mutationVersion;terminalFiles=_terminalFiles.ToArray();
                var histogram=_histogram!=null&&_histogramCohort==ParticleCohortSignature()?new WarpHistogramCheckpoint(1,_histogram,_histogramCohort):null;
                saved=new(1,Settings,_view,_exports.ToList(),_modelPath,Model,_lastTrainingCount,LastInputAt,LastResultAt,_filters,histogram,_modelPreparationKey,_minDenoiseModel);
            }
            // The session's own files are saved relative to its folder, so the checkpoint holds when the space moves.
            // The rows are the published view's copies, so they map outside the lock.
            string root=Identity.SessionDirectory;
            string Relative(string path)=>WarpPaths.Relative(root,path);
            saved=saved with
            {
                Settings=saved.Settings with{OutputPath=Relative(saved.Settings.OutputPath)},
                Movies=saved.Movies.Select(m=>MapPaths(m,Relative)).ToArray(),
                Exports=saved.Exports.Select(e=>e.MapPaths(Relative)).ToList(),
                ModelPath=Relative(saved.ModelPath)
            };
            string temporary=CheckpointPath+".writing";
            using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,65536,true))
                await JsonSerializer.SerializeAsync(stream,saved,JsonOptions).ConfigureAwait(false);
            File.Move(temporary,CheckpointPath,true);
            WriteSummary();
            foreach(string terminal in terminalFiles)try{File.Delete(terminal);}catch(IOException ex){_logger.Warning(ex,"Could not prune compiled worker result {Path}",terminal);}
            lock(_sync){for(int i=0;i<terminalFiles.Length;i++)_terminalFiles.Dequeue();SavedAt=DateTime.UtcNow;_checkpointProblem="";if(version==_mutationVersion)_dirty=false;}
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_checkpointProblem="Checkpoint could not be saved: "+ex.Message;_logger.Error(ex,"Warp session checkpoint failed");}
        finally{_saveGate.Release();}
    }
    private const string KeptFiltersFile="warp-filters.json";
    /// <summary>
    /// The filter thresholds saved in a session folder: its checkpoint's, else those kept across a clear; null when there
    /// are none or they cannot be read.
    /// </summary>
    internal static WarpFilters ReadSavedFilters(string sessionDirectory)
    {
        try
        {
            string checkpoint=Path.Combine(sessionDirectory,"warp-mini.json");
            if(File.Exists(checkpoint))
            {
                using var stream=File.OpenRead(checkpoint);using var document=JsonDocument.Parse(stream);
                if(document.RootElement.TryGetProperty(nameof(WarpCheckpoint.Filters),out var saved)&&saved.ValueKind==JsonValueKind.Object)
                    return saved.Deserialize<WarpFilters>(JsonOptions);
            }
            string kept=Path.Combine(sessionDirectory,KeptFiltersFile);
            return File.Exists(kept)?JsonSerializer.Deserialize<WarpFilters>(File.ReadAllText(kept),JsonOptions):null;
        }
        catch(Exception ex) when(ex is IOException or JsonException or UnauthorizedAccessException){return null;}
    }
    /// <summary>
    /// Filter thresholds are configuration, like the settings the job keeps: clearing a session's results keeps them, so
    /// the next session in the folder starts from them.
    /// </summary>
    internal static void KeepFilters(string sessionDirectory,WarpFilters filters)
    {
        Directory.CreateDirectory(sessionDirectory);
        File.WriteAllText(Path.Combine(sessionDirectory,KeptFiltersFile),JsonSerializer.Serialize(filters,JsonOptions));
    }
    internal static ulong Fingerprint(object value)=>BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,JsonOptions))),0);
    public void Dispose(){lock(_sync){if(_disposed)return;_disposed=true;_runCancellation?.Cancel();Save();}_lifetime.Cancel();_checkpointLoop.GetAwaiter().GetResult();SaveAsync().GetAwaiter().GetResult();}
}
