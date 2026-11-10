using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Warp;
using Warp.Tools;
using Warp.Workers;
using Warp.Workers.Queue;
using Warp.Workers.Scheduling;

namespace Refund.Services.Warp;
public sealed partial class WarpSession
{
    private static readonly string[] StageOrder=["motion","average","ctf","prepare","denoise","pick","extract"];
    private Dictionary<string,ulong> _stageKeys=[];
    private readonly Dictionary<string,PendingTask> _inflight=[];
    private readonly Dictionary<string,(long Length,long WriteTicks,DateTime Observed)> _candidates=new(StringComparer.Ordinal);
    private IEnumerator<string> _discovery;
    private DateTime _nextDiscovery;
    private string _runId="";
    private bool _trainRequested,_trainingFailed,_processingChanged;
    private string _trainingOutput="";
    private sealed record StageCompletion(string Stage,ulong Fingerprint);
    private sealed record PendingTask(int MovieIndex,string Stage,ulong Fingerprint,string OutputModel="",long SourceLength=0,long SourceWriteTicks=0,StageCompletion[] Completions=null);
    private static string StageLabel(string s)=>s switch{"average"=>"averages","ctf"=>"CTF","prepare"=>"deconvolution","denoise"=>"denoising","pick"=>"picking","extract"=>"extraction",_=>s};
    private static WarpStage StageFlag(string stage)=>stage switch{"motion" or "average"=>WarpStage.Motion,"ctf"=>WarpStage.Ctf,"prepare" or "denoise"=>WarpStage.Enhance,"pick"=>WarpStage.Pick,"extract"=>WarpStage.Extract,_=>0};
    private static string FileRevision(string path){if(string.IsNullOrEmpty(path))return "";var f=new FileInfo(path);return f.Exists?$"{path}|{f.Length}|{f.LastWriteTimeUtc.Ticks}":path+"|missing";}
    private static Dictionary<string,ulong> BuildStageKeys(WarpSettings s,string model)
    {
        ulong raw=Fingerprint(new{s.Pixel,s.TargetPixelSize,s.EerGrouping,Gain=s.CorrectGain?FileRevision(s.GainPath):"",Defects=s.CorrectDefects?FileRevision(s.DefectsPath):"",s.GainFlipX,s.GainFlipY,s.GainTranspose});
        ulong motion=Fingerprint(new{raw,AutoDose=s.MotionGridAuto?s.TotalDose:0,s.MotionMin,s.MotionMax,s.MotionBfactor,s.MotionGridAuto,X=s.MotionGridAuto?0:s.MotionGrid,Y=s.MotionGridAuto?0:s.MotionGridY,Z=s.MotionGridAuto?0:s.MotionGridZ});
        ulong average=Fingerprint(new{motion,s.TotalDose,s.SkipFirst,s.SkipLast,s.ExportStack,s.StackGroup});
        ulong ctf=Fingerprint(new{raw,AutoDose=s.CtfGridAuto?s.TotalDose:0,Average=s.UseMovieSum?average:0,s.CtfMin,s.CtfMax,s.CtfWindow,s.Voltage,s.Cs,s.Cc,s.Amplitude,s.DefocusMin,s.DefocusMax,s.FitPhase,s.FitThickness,s.UseMovieSum,s.CtfGridAuto,X=s.CtfGridAuto?0:s.CtfGridX,Y=s.CtfGridAuto?0:s.CtfGridY});
        ulong prepare=Fingerprint(new{average,ctf,s.DeconvolutionStrength,s.DeconvolutionFalloff});
        ulong denoise=Fingerprint(new{prepare,s.Denoise,Model=s.Denoise?FileRevision(model):""});
        ulong pick=Fingerprint(new{denoise,s.Diameter,s.MinSeparation});
        ulong extract=Fingerprint(new{raw,motion,ctf,pick,s.Box,s.TotalDose,s.SkipFirst,s.SkipLast});
        return new(){["motion"]=motion,["average"]=average,["ctf"]=ctf,["prepare"]=prepare,["denoise"]=denoise,["pick"]=pick,["extract"]=extract};
    }
    private void RecomputeStageKeys()=>_stageKeys=BuildStageKeys(Settings,_modelPath);
    private bool Current(WarpMovie movie,string stage)=>movie.Completed.TryGetValue(stage,out ulong value)&&value==_stageKeys[stage];

    internal async Task RunAsync(WarpSettings settings,CancellationToken cancellationToken)
    {
        lock(_sync)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(WarpSession));
            if(Running)throw new InvalidOperationException("This Warp session is already running.");
            string error=ValidateSettings(settings);if(error!=null)throw new ArgumentException(error);
            Settings=NormalizeSettings(settings) with{OutputPath=Path.Combine(Identity.SessionDirectory,"processing")};
            Directory.CreateDirectory(Settings.OutputPath);Directory.CreateDirectory(Path.Combine(Settings.OutputPath,"input"));
            RecomputeStageKeys();RefreshStates();Running=true;StartedAt=DateTime.UtcNow;Training=false;_trainingFailed=false;
            _runId=Guid.NewGuid().ToString("N");_inflight.Clear();_runCancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _nextDiscovery=DateTime.MinValue;_runProblem=_discoveryProblem="";Save();
        }
        Publish();
        var token=_runCancellation.Token;
        var layout=new QueueLayout(Identity.QueueDirectory);layout.EnsureDirectories();
        var queue=new TaskQueue(layout);
        try
        {
            using var scheduler=new Scheduler(layout,queue,new ExternalProvisioner(),1,logDir:Path.Combine(Identity.SessionDirectory,"logs"));
            while(true)
            {
                token.ThrowIfCancellationRequested();
                scheduler.Tick();
                DiscoverBatch(token);
                CollectCompletions(layout,token);
                lock(_sync)
                {
                    token.ThrowIfCancellationRequested();
                    Dispatch(queue,token);
                    if(_changedRows.Count>0||_processingChanged){RefreshStates();Save();_processingChanged=false;}
                }
                UpdateHistogramBatch();
                Publish();
                await Task.Delay(1000,token);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
        catch(Exception ex){lock(_sync)_runProblem="Processing stopped: "+ex.Message;_logger.Error(ex,"Warp session coordinator failed for {Session}",Identity.Key);throw;}
        finally
        {
            lock(_sync)
            {
                Running=false;Training=false;_discovery?.Dispose();_discovery=null;
                foreach(var item in _inflight.Values)if(item.MovieIndex>=0){_movies[item.MovieIndex].ActiveStage="";_changedRows.Add(item.MovieIndex);}
                _inflight.Clear();_runCancellation.Dispose();_runCancellation=null;RefreshStates();Save();
            }
            await SaveAsync();Publish();
        }
    }

    private void DiscoverBatch(CancellationToken token)
    {
        if(DateTime.UtcNow<_nextDiscovery)return;
        try
        {
            _discovery??=Directory.EnumerateFiles(Settings.InputPath,Settings.InputPattern,new EnumerationOptions{RecurseSubdirectories=Settings.Recursive,IgnoreInaccessible=false,MaxRecursionDepth=32}).GetEnumerator();
            var time=Stopwatch.StartNew();int count=0;
            while(count++<4096&&time.ElapsedMilliseconds<250)
            {
                token.ThrowIfCancellationRequested();
                if(!_discovery.MoveNext()){_discovery.Dispose();_discovery=null;_discoveryProblem="";_nextDiscovery=DateTime.UtcNow.AddSeconds(3);break;}
                string path=Path.GetFullPath(_discovery.Current);
                if(path.StartsWith(Path.GetFullPath(Settings.OutputPath)+Path.DirectorySeparatorChar,StringComparison.Ordinal))continue;
                var info=new FileInfo(path);
                if(!info.Exists)continue;
                bool largeEnough=info.Length>0&&info.Length>=(long)(Settings.MinimumFileSizeMb*1000000);
                lock(_sync)
                {
                    if(_byInput.TryGetValue(path,out var existing))
                    {
                        if(existing.SourceLength!=info.Length||existing.SourceWriteTicks!=info.LastWriteTimeUtc.Ticks)
                        {
                            RemoveHistogramMovie(existing);
                            existing.SourceLength=info.Length;existing.SourceWriteTicks=info.LastWriteTimeUtc.Ticks;
                            existing.Completed.Clear();existing.Done=0;existing.Failed=false;existing.InputReady=false;_changedRows.Add(existing.Index);InvalidateHistogram();
                            _candidates[path]=(info.Length,info.LastWriteTimeUtc.Ticks,DateTime.UtcNow);
                        }
                        else if(largeEnough&&!existing.InputReady&&_candidates.TryGetValue(path,out var stable)&&DateTime.UtcNow-stable.Observed>=TimeSpan.FromSeconds(2)){existing.InputReady=true;_candidates.Remove(path);_changedRows.Add(existing.Index);}
                        continue;
                    }
                    if(!largeEnough)continue;
                    if(!_candidates.TryGetValue(path,out var observed)||observed.Length!=info.Length||observed.WriteTicks!=info.LastWriteTimeUtc.Ticks)
                    {_candidates[path]=(info.Length,info.LastWriteTimeUtc.Ticks,DateTime.UtcNow);continue;}
                    if(DateTime.UtcNow-observed.Observed<TimeSpan.FromSeconds(2))continue;
                    string id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
                    string alias=id+"_"+info.Name;
                    string inputAlias=Path.Combine(Settings.OutputPath,"input",alias);
                    if(!File.Exists(inputAlias))File.CreateSymbolicLink(inputAlias,path);
                    string processing=Path.Combine(Settings.OutputPath,alias);
                    var metadata=new Movie(processing,Path.Combine(Settings.OutputPath,"input"),Array.Empty<string>());
                    metadata.SaveMeta();
                    var m=new WarpMovie{Index=_movies.Count,Id=id,Name=info.Name,InputPath=path,ProcessingPath=processing,MetadataPath=metadata.XMLPath,
                        AveragePath=metadata.AveragePath,DeconvolvedPath=metadata.DeconvolvedPath,EnhancedPath=Settings.Denoise?metadata.AverageDenoisedPath:metadata.DeconvolvedPath,
                        ThumbnailPath=metadata.ThumbnailsPath,TrainingOddPath=metadata.DenoiseTrainingOddPath,TrainingEvenPath=metadata.DenoiseTrainingEvenPath,
                        ParticleStarPath=Path.Combine(Path.GetDirectoryName(Settings.Denoise?metadata.AverageDenoisedPath:metadata.DeconvolvedPath),"matching",metadata.RootName+"_blob.star"),
                        ParticleStackPath=Path.Combine(metadata.ParticlesDir,metadata.RootName+"_warp.mrcs"),PixelSize=(double)Settings.TargetPixelSize,EnhancedPixelSize=Math.Max(5,(double)Settings.TargetPixelSize),
                        SourceLength=info.Length,SourceWriteTicks=info.LastWriteTimeUtc.Ticks,DiscoveredAt=DateTime.UtcNow};
                    _movies.Add(m);_byInput[path]=m;_changedRows.Add(m.Index);_candidates.Remove(path);LastInputAt=DateTime.UtcNow;CatalogVersion++;
                }
            }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){_discovery?.Dispose();_discovery=null;_nextDiscovery=DateTime.UtcNow.AddSeconds(10);_discoveryProblem="Can't read the movie folder: "+ex.Message;_logger.Warning(ex,"Warp input discovery failed");}
    }

    private void Dispatch(TaskQueue queue,CancellationToken token)
    {
        if(_inflight.Count>=64)return;
        int prepared=_movies.Count(m=>!m.Failed&&Current(m,"prepare")&&m.TrainingPairReady);
        if(Settings.Denoise&&!Training&&!_trainingFailed&&prepared>=Settings.TrainAfter&&(_trainRequested||Model==0||Settings.FineTune&&prepared-_lastTrainingCount>=Settings.FineTuneEvery))
        {
            var sample=_movies.Where(m=>!m.Failed&&Current(m,"prepare")&&m.TrainingPairReady).TakeLast(Settings.TrainCount).Select(m=>m.ProcessingPath).ToArray();
            _trainingOutput=Path.Combine(Identity.SessionDirectory,"models",$"model-{Guid.NewGuid():N}.pt");Directory.CreateDirectory(Path.GetDirectoryName(_trainingOutput));
            var task=MakeTask("train",-1,[WorkerCommands.TrainMovieDenoiser(sample,_trainingOutput,Model>0?_modelPath:null,Settings.TrainingIterations)]);
            queue.Enqueue(task);_inflight[task.TaskId]=new(-1,"train",_stageKeys["prepare"],_trainingOutput);Training=true;TrainingPercent=0;_trainRequested=false;_lastTrainingCount=prepared;
        }
        int submitted=0;
        foreach(var m in _movies)
        {
            if(_inflight.Count>=64||submitted>=16)break;
            token.ThrowIfCancellationRequested();
            if(m.Failed||!m.InputReady||m.ActiveStage.Length>0)continue;
            string stage=NextStage(m);if(stage==null)continue;
            if(stage=="extract"&&m.Picks==0){m.Completed[stage]=_stageKeys[stage];m.Done|=WarpStage.Extract;_changedRows.Add(m.Index);continue;}
            // Motion, averaging and CTF share one decoded raw stack on their worker.
            // Later edits include only stages whose own fingerprints changed.
            string[] stages=stage is "motion" or "average" or "ctf"
                ? StageOrder.Take(3).Where(candidate=>!Current(m,candidate)).ToArray() : [stage];
            var commands=BuildCommandBatch(m,stages);if(commands.Length==0)continue;
            var task=MakeTask(stage,m.Index,commands);queue.Enqueue(task);
            _inflight[task.TaskId]=new(m.Index,stage,_stageKeys[stage],SourceLength:m.SourceLength,SourceWriteTicks:m.SourceWriteTicks,Completions:stages.Select(value=>new StageCompletion(value,_stageKeys[value])).ToArray());m.ActiveStage=string.Join(" → ",stages.Select(StageLabel));_changedRows.Add(m.Index);submitted++;
        }
    }
    private string NextStage(WarpMovie m)
    {
        if(!Current(m,"motion"))return "motion";
        if(!Current(m,"average"))return "average";
        if(!Current(m,"ctf"))return "ctf";
        if(!Current(m,"prepare"))return "prepare";
        if(!Current(m,"denoise"))return !Settings.Denoise?"denoise":Model>0?"denoise":null;
        if(Settings.PickingEnabled&&!Current(m,"pick"))return "pick";
        if(Settings.PickingEnabled&&!Current(m,"extract"))return "extract";
        return null;
    }
    private TaskItem MakeTask(string stage,int index,NamedSerializableObject[] commands)
    {
        var item=new TaskItem{TaskId=$"{_runId}-{index:D8}-{stage}-{Guid.NewGuid():N}",Stage=stage,RequiresGpu=true,
            Init=[WorkerCommands.LoadGainRef(Settings.CorrectGain?Settings.GainPath:"",Settings.GainFlipX,Settings.GainFlipY,Settings.GainTranspose,Settings.CorrectDefects?Settings.DefectsPath:"")],Main=commands};
        item.ComputeInitFingerprint();return item;
    }
    private NamedSerializableObject[] BuildCommandBatch(WarpMovie movie,string[] stages)
    {
        var commands=new List<NamedSerializableObject>();string loadedPath=null;
        foreach(string stage in stages)
            foreach(var command in BuildCommands(movie,stage))
            {
                if(command.Name==WorkerCommandNames.LoadStack)
                {
                    string path=(string)command.Content[0];
                    if(path==loadedPath)continue;
                    loadedPath=path;
                }
                commands.Add(command);
            }
        return commands.ToArray();
    }
    private NamedSerializableObject[] BuildCommands(WarpMovie m,string stage)
    {
        var options=WorkerOptions();
        decimal scale=Settings.Pixel/Settings.TargetPixelSize;
        var load=WorkerCommands.LoadStack(m.InputPath,scale,-Settings.EerGrouping);
        var commands=new List<NamedSerializableObject>();
        switch(stage)
        {
            case "motion":commands.Add(load);commands.Add(WorkerCommands.MovieProcessMovement(m.ProcessingPath,options.GetProcessingMovieMovement()));break;
            case "average":commands.Add(load);var export=options.GetProcessingMovieExport();export.DoAverage=true;export.DoDenoise=true;export.DoDenoiseDeconv=false;export.DoDeconv=false;commands.Add(WorkerCommands.MovieExportMovie(m.ProcessingPath,export));commands.Add(WorkerCommands.WaitAsyncTasks());commands.Add(WorkerCommands.MovieCreateThumbnail(m.ProcessingPath,384,3));break;
            case "ctf":commands.Add(Settings.UseMovieSum?WorkerCommands.LoadStack(m.AveragePath,1,-Settings.EerGrouping,false):load);commands.Add(WorkerCommands.MovieProcessCTF(m.ProcessingPath,options.GetProcessingMovieCTF()));break;
            case "prepare":commands.Add(WorkerCommands.MoviePrepareDenoising(m.ProcessingPath,(float)Settings.TargetPixelSize,(float)Settings.DeconvolutionStrength,(float)Settings.DeconvolutionFalloff));break;
            case "denoise":if(Settings.Denoise)commands.Add(WorkerCommands.MovieDenoise(m.ProcessingPath,_modelPath));else commands.Add(WorkerCommands.WaitAsyncTasks());break;
            case "pick":string enhanced=Settings.Denoise?new Movie(m.ProcessingPath).AverageDenoisedPath:m.DeconvolvedPath;commands.Add(WorkerCommands.MicrographPickBlobs(enhanced,(float)(Settings.Diameter/(decimal)m.EnhancedPixelSize/2),0,(float)(Settings.Diameter*Settings.MinSeparation/(decimal)m.EnhancedPixelSize)));break;
            case "extract":commands.Add(load);var particle=options.GetProcessingParticleExport();particle.Suffix="_warp";particle.BoxSize=Settings.Box;particle.BoxSizeResample=-1;particle.BinTimes=Settings.BinTimes;commands.Add(WorkerCommands.MovieExportParticlesFromStar(m.ProcessingPath,particle,m.ParticleStarPath,(float)m.EnhancedPixelSize));break;
        }
        commands.Add(WorkerCommands.WaitAsyncTasks());return commands.ToArray();
    }
    private OptionsWarp WorkerOptions()
    {
        var o=new OptionsWarp();o.Import.PixelSize=Settings.Pixel;o.Import.BinTimes=Settings.BinTimes;o.Import.BinnedPixelSize=Settings.TargetPixelSize;
        o.Import.EERGroupFrames=-Settings.EerGrouping;o.Import.DosePerAngstromFrame=-Settings.TotalDose;
        o.Import.GainPath=Settings.CorrectGain?Settings.GainPath:"";o.Import.CorrectGain=Settings.CorrectGain;o.Import.DefectsPath=Settings.CorrectDefects?Settings.DefectsPath:"";o.Import.CorrectDefects=Settings.CorrectDefects;
        o.Import.GainFlipX=Settings.GainFlipX;o.Import.GainFlipY=Settings.GainFlipY;o.Import.GainTranspose=Settings.GainTranspose;
        o.Movement.RangeMin=Settings.MotionMin;o.Movement.RangeMax=Settings.MotionMax;o.Movement.Bfactor=Settings.MotionBfactor;
        o.Grids.MovementX=Settings.MotionGridAuto?0:Settings.MotionGrid;o.Grids.MovementY=Settings.MotionGridAuto?0:Settings.MotionGridY;o.Grids.MovementZ=Settings.MotionGridAuto?0:Settings.MotionGridZ;
        o.CTF.Window=Settings.CtfWindow;o.CTF.RangeMin=Settings.CtfMin;o.CTF.RangeMax=Settings.CtfMax;o.CTF.ZMin=Settings.DefocusMin;o.CTF.ZMax=Settings.DefocusMax;
        o.CTF.Voltage=Settings.Voltage;o.CTF.Cs=Settings.Cs;o.CTF.Cc=Settings.Cc;o.CTF.Amplitude=Settings.Amplitude;o.CTF.DoPhase=Settings.FitPhase;o.CTF.FitThickness=Settings.FitThickness;o.CTF.UseMovieSum=Settings.UseMovieSum;
        o.Grids.CTFX=Settings.CtfGridAuto?0:Settings.CtfGridX;o.Grids.CTFY=Settings.CtfGridAuto?0:Settings.CtfGridY;
        o.Export.DoAverage=true;o.Export.DoStack=Settings.ExportStack;o.Export.StackGroupSize=Settings.StackGroup;o.Export.SkipFirstN=Settings.SkipFirst;o.Export.SkipLastN=Settings.SkipLast;
        o.Picking.BoxSize=Settings.Box;o.Picking.Diameter=(int)Settings.Diameter;o.Tasks.Export2DPixel=Settings.TargetPixelSize;
        return o;
    }

    private void CollectCompletions(QueueLayout layout,CancellationToken token)
    {
        KeyValuePair<string,PendingTask>[] pending;lock(_sync)pending=_inflight.ToArray();
        int count=0;
        foreach(var pair in pending)
        {
            token.ThrowIfCancellationRequested();if(count>=16)break;
            string done=Path.Combine(layout.Done,pair.Key+".json"),poison=Path.Combine(layout.Poisoned,pair.Key+".json");
            bool success=File.Exists(done);if(!success&&!File.Exists(poison))continue;count++;
            lock(_sync)
            {
                try
                {
                    if(!success)throw new IOException(TaskItem.FromJson(File.ReadAllText(poison)).Error??"Worker task exhausted retries.");
                    if(pair.Value.MovieIndex<0)
                    {
                        if(!File.Exists(pair.Value.OutputModel))throw new IOException("Training finished without its model file.");
                        _modelPath=pair.Value.OutputModel;_modelPreparationKey=pair.Value.Fingerprint;Model++;Training=false;TrainingPercent=100;RecomputeStageKeys();ResetHistogram();
                    }
                    else
                    {
                        var m=_movies[pair.Value.MovieIndex];
                        var source=new FileInfo(m.InputPath);
                        if(!source.Exists||source.Length!=pair.Value.SourceLength||source.LastWriteTimeUtc.Ticks!=pair.Value.SourceWriteTicks||m.SourceLength!=pair.Value.SourceLength||m.SourceWriteTicks!=pair.Value.SourceWriteTicks)
                        {
                            RemoveHistogramMovie(m);m.Completed.Clear();m.Done=0;m.InputReady=false;m.ActiveStage="";
                            if(source.Exists){m.SourceLength=source.Length;m.SourceWriteTicks=source.LastWriteTimeUtc.Ticks;_candidates[m.InputPath]=(source.Length,source.LastWriteTimeUtc.Ticks,DateTime.UtcNow);}
                            _changedRows.Add(m.Index);continue;
                        }
                        foreach(var completion in pair.Value.Completions??[new(pair.Value.Stage,pair.Value.Fingerprint)])
                        {
                            ReadResult(m,completion.Stage);
                            m.Completed[completion.Stage]=completion.Fingerprint;m.Done|=StageFlag(completion.Stage);
                            if(completion.Stage=="prepare"&&Settings.Denoise)m.Done&=~WarpStage.Enhance;
                            if(completion.Stage=="motion")m.MotionRevision++;
                            if(completion.Stage=="ctf")m.CtfRevision++;
                            if(completion.Stage=="pick"){m.PickingRevision++;InvalidateHistogram();}
                        }
                        m.ResultRevision++;m.ActiveStage="";m.Failed=false;m.Error="";_changedRows.Add(m.Index);
                    }
                    LastResultAt=DateTime.UtcNow;
                }
                catch(Exception ex) when(ex is not OperationCanceledException)
                {
                    if(pair.Value.MovieIndex<0){Training=false;_trainingFailed=true;_runProblem="Denoiser training failed: "+ex.Message;}
                    else{var m=_movies[pair.Value.MovieIndex];m.ActiveStage="";m.Failed=true;m.Error=ex.Message;_changedRows.Add(m.Index);}
                    _logger.Warning(ex,"Warp task {Task} failed",pair.Key);
                }
                finally{_inflight.Remove(pair.Key);_terminalFiles.Enqueue(success?done:poison);_processingChanged=true;}
            }
            // The checkpoint writer prunes compiled terminal records after its next flush.
        }
    }
    private void ReadResult(WarpMovie m,string stage)
    {
        if(!File.Exists(m.MetadataPath))throw new IOException("Worker did not produce movie metadata.");
        var metadata=new Movie(m.ProcessingPath);
        switch(stage)
        {
            case "motion":if(metadata.OptionsMovement==null)throw new InvalidDataException("Motion metadata is missing.");m.Motion=(double)metadata.MeanFrameMovement;break;
            case "average":if(!File.Exists(m.AveragePath))throw new IOException("Aligned average is missing.");m.PixelSize=(double)Settings.TargetPixelSize;break;
            case "ctf":if(metadata.OptionsCTF==null)throw new InvalidDataException("CTF metadata is missing.");m.Ctf=(double)metadata.CTFResolutionEstimate;m.Defocus=(double)metadata.CTF.Defocus;m.Astigmatism=(double)metadata.CTF.DefocusDelta;m.AstigmatismAngle=(double)metadata.CTF.DefocusAngle;m.Ice=Settings.FitThickness&&metadata.CTFSpecimenThicknessAngstrom>0?(double)metadata.CTFSpecimenThicknessAngstrom:double.NaN;break;
            case "prepare":if(!File.Exists(m.DeconvolvedPath))throw new IOException("Deconvolved micrograph is missing.");m.TrainingPairReady=File.Exists(m.TrainingOddPath)&&File.Exists(m.TrainingEvenPath);break;
            case "denoise":m.EnhancedPath=Settings.Denoise?metadata.AverageDenoisedPath:metadata.DeconvolvedPath;if(!File.Exists(m.EnhancedPath))throw new IOException("Enhanced micrograph is missing.");m.EnhancedPixelSize=Math.Max(5,(double)Settings.TargetPixelSize);m.ParticleStarPath=Path.Combine(Path.GetDirectoryName(m.EnhancedPath),"matching",metadata.RootName+"_blob.star");break;
            case "pick":if(!File.Exists(m.ParticleStarPath))throw new IOException("Particle STAR is missing.");_particleCache.Remove(m.Index);m.Picks=ReadParticles(m.ParticleStarPath).Length;break;
            case "extract":if(!File.Exists(m.ParticleStackPath)&&m.Picks>0)throw new IOException("Extracted particle stack is missing.");break;
        }
    }
}
