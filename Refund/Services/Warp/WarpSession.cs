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
internal sealed record WarpHistogramCheckpoint(int Schema,WarpParticleHistogram Histogram,string CohortSignature,string FilterSignature);
public sealed record WarpFilters(double MinCtf=0,double MaxCtf=5,double MinMotion=0,double MaxMotion=2.5,
    double MinIce=0,double MaxIce=2000,int MinParticles=0,int MaxParticles=int.MaxValue,double MaxAstigmatismSigma=3,
    double MinPickCorrelation=0,double MaxPickCorrelation=1,double MinPickPower=0,double? MaxPickPower=null);

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
    private string _histogramCohort="",_histogramFilter="";
    private readonly Dictionary<int,(long Revision,WarpParticle[] Particles)> _particleCache=[];
    private readonly Queue<int> _particleCacheOrder=[];
    private static readonly JsonSerializerOptions JsonOptions=new(){NumberHandling=JsonNumberHandling.AllowNamedFloatingPointLiterals};
    public event Action Changed;
    public WarpSettings Settings {get;private set;}=new();
    public IReadOnlyList<WarpMovie> Movies=>_view;
    public IReadOnlyList<WarpExportManifest> Exports {get{lock(_sync)return _exports.ToArray();}}
    public bool Running {get;private set;}
    public bool Training {get;private set;}
    public int TrainingPercent {get;private set;}
    /// <summary>Version of the newest denoiser; it keeps counting when a model file goes missing.</summary>
    public int Model {get;private set;}
    /// <summary>A trained model is on disk, so movies can be denoised.</summary>
    public bool HasModel=>Model>0&&_modelPath.Length>0;
    public bool Exporting {get;private set;}
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
    /// <summary>Movies with CTF results that did not fail; accepted movies are a subset.</summary>
    public int Processed {get;private set;}
    public int Complete {get;private set;}
    public int Pending {get;private set;}
    public int Failed {get;private set;}
    public int Outdated {get;private set;}
    public long ParticleCount {get;private set;}
    public int[] StageCounts {get;}=new int[5];
    public int AwaitingDenoiser=>Settings.Denoise && !HasModel ? _view.Count(m=>Current(m,"prepare")&&!m.Done.HasFlag(WarpStage.Enhance)&&!m.Failed):0;
    public string DenoiserStatus=>!Settings.Denoise?"Off":Training?"Training":HasModel?$"Model v{Model}":$"Waiting for {Settings.TrainAfter} prepared movies";
    public static readonly string[] StatusNames=["Unprocessed","Partial","Ready","Outdated","Filtered","Kept","Excluded","Failed"];
    private string CheckpointPath=>Path.Combine(Identity.SessionDirectory,"warp-mini.json");
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
                Settings=NormalizeSettings(saved.Settings);_movies.AddRange(saved.Movies);_exports.AddRange(saved.Exports);
                _modelPreparationKey=saved.ModelPreparationKey;Model=saved.Model;_modelPath=saved.ModelPath??"";_lastTrainingCount=saved.LastTrainingCount;_minDenoiseModel=saved.MinDenoiseModel;
                LastInputAt=saved.LastInputAt;LastResultAt=saved.LastResultAt;_filters=saved.Filters??new();
                foreach(var m in _movies){m.ActiveStage="";_byInput[m.InputPath]=m;}
                foreach(var m in _movies)_changedRows.Add(m.Index);PublishRows();
                // A missing model file only stops new denoising until the next model; existing results stay valid.
                if(_modelPath.Length>0&&!File.Exists(_modelPath))_modelPath="";
                RecomputeStageKeys();if(Model>0&&_modelPreparationKey!=_stageKeys["prepare"])ForgetModel();RefreshStates();
                if(saved.Histogram is {Schema:1} h && h.CohortSignature==ParticleCohortSignature() &&
                    h.Histogram.Bins?.Length==64*48 && h.Histogram.Bins.All(v=>v>=0) && h.Histogram.Bins.Sum(v=>(long)v)==h.Histogram.Total)
                {_histogram=h.Histogram;_histogramCohort=h.CohortSignature;_histogramFilter=h.FilterSignature;foreach(var m in _movies.Where(HistogramEligible))_histogramMembers[m.Index]=m.PickingRevision;}
                PublishRows();
                SavedAt=File.GetLastWriteTimeUtc(CheckpointPath);
            }
            catch(Exception ex) when(ex is IOException or JsonException or InvalidDataException)
            { _logger.Error(ex,"Cannot restore Warp session {Session}",identity.Key);throw new InvalidDataException("Warp session checkpoint could not be restored; original file has been retained.",ex); }
        }
        else {Settings=NormalizeSettings(initialSettings??Settings) with{OutputPath=Path.Combine(identity.SessionDirectory,"processing")};RecomputeStageKeys();}
        _checkpointLoop=Task.Run(async()=>{try{while(!_lifetime.IsCancellationRequested){await Task.Delay(5000,_lifetime.Token);if(!Running)UpdateHistogramBatch();if(_dirty)await SaveAsync();}}catch(OperationCanceledException){}catch(Exception ex){_logger.Error(ex,"Warp session checkpoint loop failed");}});
    }

    public void Apply(WarpSettings settings)
    {
        lock(_sync)
        {
            if(Running)throw new InvalidOperationException("Stop processing before changing settings.");
            var error=ValidateSettings(settings);if(error!=null)throw new ArgumentException(error);
            var next=NormalizeSettings(settings) with{OutputPath=Path.Combine(Identity.SessionDirectory,"processing")};
            if(_stageKeys["prepare"]!=BuildStageKeys(next)["prepare"])ForgetModel();
            bool processingChanged=StageOrder.Any(stage=>_stageKeys[stage]!=BuildStageKeys(next)[stage]);
            Settings=next;RecomputeStageKeys();
            if (processingChanged)
            {
                foreach (var movie in _movies.Where(m => m.Failed))
                {
                    movie.Failed = false; movie.Error = ""; _changedRows.Add(movie.Index);
                }
                ResetHistogram();
            }
            RefreshStates();Save();
        }
        SaveAsync().GetAwaiter().GetResult();Publish();
    }
    public string GetSettingsImpact(WarpSettings next)
    {
        if(next==Settings)return "";
        var before=BuildStageKeys(Settings);var after=BuildStageKeys(NormalizeSettings(next));
        string[] changed=StageOrder.Where(k=>before[k]!=after[k]).ToArray();
        return changed.Length==0?"Update discovery and training schedule; retain current results.":"Redo "+string.Join(", ",changed.Select(StageLabel))+".";
    }
    public WarpStage ActiveStale(WarpMovie m)=>m.Stale&RequiredStages;
    public bool PassesFilters(WarpMovie m)=>
        (!m.Done.HasFlag(WarpStage.Ctf)||(m.Ctf>=MinCtf&&m.Ctf<=MaxCtf)&&(!double.IsFinite(m.Ice)||m.Ice>=MinIce&&m.Ice<=MaxIce)&&(!(AstigmatismSigma(m)>MaxAstigmatismSigma)))&&
        (!m.Done.HasFlag(WarpStage.Motion)||m.Motion>=MinMotion&&m.Motion<=MaxMotion)&&
        (MinParticles==0&&MaxParticles==int.MaxValue||m.Done.HasFlag(WarpStage.Pick)&&m.Picks>=MinParticles&&m.Picks<=MaxParticles);
    public bool Passes(WarpMovie m)=>!m.Failed&&!m.Excluded&&m.Disposition!=WarpDisposition.Exclude&&(m.Disposition==WarpDisposition.Keep||PassesFilters(m));
    public string Status(WarpMovie m)=>m.Failed?"Failed":m.Excluded||m.Disposition==WarpDisposition.Exclude?"Excluded":m.Done==0?"Unprocessed":ActiveStale(m)!=0?"Outdated":m.Disposition==WarpDisposition.Keep?"Kept":!PassesFilters(m)?"Filtered":(m.Done&RequiredStages)==RequiredStages?"Ready":"Partial";
    public string StatusColor(WarpMovie m)=>StatusColorFor(Status(m));
    public static string StatusColorFor(string status)=>StatusColorMapping.GetColor(status switch{"Failed"=>ProcessingStatus.Failed,"Kept"=>ProcessingStatus.Kept,"Excluded"=>ProcessingStatus.Deselected,"Outdated"=>ProcessingStatus.Outdated,"Filtered"=>ProcessingStatus.FilteredOut,"Ready"=>ProcessingStatus.Processed,"Partial"=>ProcessingStatus.Partial,_=>ProcessingStatus.Unprocessed});
    public void SetDisposition(IEnumerable<int> indices,WarpDisposition value)=>Mutate(()=>{foreach(int i in indices.Distinct())if(i>=0&&i<_movies.Count){_movies[i].Disposition=value;_movies[i].Excluded=value==WarpDisposition.Exclude;_changedRows.Add(i);}});
    public void Retry(IEnumerable<int> indices)=>Mutate(()=>{foreach(int i in indices.Distinct())if(i>=0&&i<_movies.Count){_movies[i].Failed=false;_movies[i].Error="";_changedRows.Add(i);}});
    /// <summary>A model trained on other deconvolution settings no longer fits the data, so training starts over.</summary>
    private void ForgetModel(){_modelPath="";Model=0;_lastTrainingCount=0;_minDenoiseModel=0;}
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
        Accepted=Processed=Complete=Pending=Failed=Outdated=0;ParticleCount=0;Array.Clear(StageCounts);
        foreach(var m in _movies)
        {
            var priorStale=m.Stale;m.Stale=0;
            foreach(string stage in StageOrder)if(m.Completed.TryGetValue(stage,out ulong completed)&&(completed!=_stageKeys[stage]||RedoneByModel(m,stage)))m.Stale|=StageFlag(stage);
            if(priorStale!=m.Stale)_changedRows.Add(m.Index);
            if(m.Failed)Failed++;else if(m.Done.HasFlag(WarpStage.Ctf))Processed++;
            if(ActiveStale(m)!=0)Outdated++;
            else if(!m.Failed&&(m.Done&RequiredStages)!=RequiredStages)Pending++;
            else if(!m.Failed)Complete++;
            if(Passes(m)&&m.Done.HasFlag(WarpStage.Ctf)){Accepted++;if(m.Done.HasFlag(WarpStage.Pick))ParticleCount+=m.Picks;}
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
                var histogram=_histogram!=null&&_histogramCohort==ParticleCohortSignature()?new WarpHistogramCheckpoint(1,_histogram,_histogramCohort,_histogramFilter):null;
                saved=new(1,Settings,_view,_exports.ToList(),_modelPath,Model,_lastTrainingCount,LastInputAt,LastResultAt,_filters,histogram,_modelPreparationKey,_minDenoiseModel);
            }
            string temporary=CheckpointPath+".writing";
            using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None,65536,true))
                await JsonSerializer.SerializeAsync(stream,saved,JsonOptions).ConfigureAwait(false);
            File.Move(temporary,CheckpointPath,true);
            foreach(string terminal in terminalFiles)try{File.Delete(terminal);}catch(IOException ex){_logger.Warning(ex,"Could not prune compiled worker result {Path}",terminal);}
            lock(_sync){for(int i=0;i<terminalFiles.Length;i++)_terminalFiles.Dequeue();SavedAt=DateTime.UtcNow;_checkpointProblem="";if(version==_mutationVersion)_dirty=false;}
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_checkpointProblem="Checkpoint could not be saved: "+ex.Message;_logger.Error(ex,"Warp session checkpoint failed");}
        finally{_saveGate.Release();}
    }
    internal static ulong Fingerprint(object value)=>BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,JsonOptions))),0);
    public void Dispose(){lock(_sync){if(_disposed)return;_disposed=true;_runCancellation?.Cancel();Save();}_lifetime.Cancel();_checkpointLoop.GetAwaiter().GetResult();SaveAsync().GetAwaiter().GetResult();}
}
