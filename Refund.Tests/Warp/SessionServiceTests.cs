using System.Reflection;
using Refund.Services.Warp;
using Warp.Workers.Queue;
using Warp.Workers;
using Warp;

namespace Refund.Tests.Warp;

public sealed class SessionServiceTests
{
    [Fact]
    public void ConcurrentConsumersShareOneCatalog()
    {
        using var fixture=new Fixture();
        var sessions=Enumerable.Range(0,32).AsParallel().Select(_=>fixture.Manager.GetOrCreate(fixture.Identity)).ToArray();
        Assert.All(sessions,s=>Assert.Same(fixture.Session,s));
    }
    [Fact]
    public void InvalidationRetainsUnrelatedStagesAndDetectsGainFileChanges()
    {
        using var f=new Fixture();var s=f.Session;
        var movie=f.AddMovie();
        s.Apply(s.Settings with{Box=512});
        Assert.Equal(WarpStage.Extract,s.Movies[0].Stale);
        s.Apply(s.Settings with{TotalDose=50});
        Assert.True(s.Movies[0].Stale.HasFlag(WarpStage.Motion));
        Assert.True(s.Movies[0].Stale.HasFlag(WarpStage.Ctf));
        string gain=Path.Combine(f.Root,"gain.mrc");File.WriteAllText(gain,"x");
        s.Apply(s.Settings with{CorrectGain=true,GainPath=gain});
        movie.Completed=new(Field<Dictionary<string,ulong>>(s,"_stageKeys"));
        s.SetDisposition([0],WarpDisposition.Auto);
        File.AppendAllText(gain,"changed");
        s.Apply(s.Settings);
        Assert.True(s.Movies[0].Stale.HasFlag(WarpStage.Motion));
    }
    [Fact]
    public void HistogramAddsOnceExpandsLinearlyAndWarmRestoresWithoutStars()
    {
        using var f=new Fixture();var first=f.AddMovie(1);
        Invoke(f.Session,"UpdateHistogramBatch");
        var initial=f.Session.GetParticleHistogram();Assert.Equal(2,initial.Total);Assert.Equal(2,initial.Bins.Sum());
        File.Delete(first.ParticleStarPath);
        var second=f.AddMovie(128);
        Invoke(f.Session,"UpdateHistogramBatch");
        var added=f.Session.GetParticleHistogram();Assert.Equal(4,added.Total);Assert.Equal(4,added.Bins.Sum());Assert.Equal(256,added.PowerMaximum);
        f.Session.SetParticleRange(.6,1,0,null);Assert.InRange(f.Session.GetParticleHistogram().Accepted,0,4);
        f.Session.Apply(f.Session.Settings with{InputPattern="*.mrc"});Assert.Equal(4,f.Session.GetParticleHistogram().Total);
        f.Session.SetParticleRange(.8,1,0,null);
        f.Manager.Remove(f.Identity);File.Delete(second.ParticleStarPath);
        var restored=f.Manager.GetOrCreate(f.Identity);Assert.Equal(4,restored.GetParticleHistogram().Total);
        Assert.Equal(2,Field<Dictionary<int,long>>(restored,"_histogramMembers").Count);
        restored.Apply(restored.Settings with{Diameter=150});Assert.Equal(0,restored.GetParticleHistogram().Total);
    }
    [Fact]
    public void OldSnapshotsRemainUnchangedAndDisposedSessionDoesNotRecreateDirectory()
    {
        using var f=new Fixture();f.AddMovie();var snapshot=f.Session.Movies[0];
        f.Session.SetDisposition([0],WarpDisposition.Exclude);
        Assert.Equal(WarpDisposition.Auto,snapshot.Disposition);
        Assert.NotSame(snapshot,f.Session.Movies[0]);
        f.Manager.Remove(f.Identity);Directory.Delete(f.Root,true);f.Session.Dispose();Assert.False(Directory.Exists(f.Root));
    }
    [Fact]
    public void DiscoveryRequiresSizeStabilityAndDoesNotCollideForRepeatedBasenames()
    {
        using var f=new Fixture();Directory.CreateDirectory(Path.Combine(f.Root,"a"));Directory.CreateDirectory(Path.Combine(f.Root,"b"));
        File.WriteAllText(Path.Combine(f.Root,"a","same.mrc"),"movie");File.WriteAllText(Path.Combine(f.Root,"b","same.mrc"),"movie");
        f.Session.Apply(f.Session.Settings with{Recursive=true,InputPattern="*.mrc",MinimumFileSizeMb=.001m});
        Invoke(f.Session,"DiscoverBatch",CancellationToken.None);Assert.Empty(f.Session.Movies);
        f.Session.Apply(f.Session.Settings with{MinimumFileSizeMb=0});SetField(f.Session,"_nextDiscovery",DateTime.MinValue);
        Invoke(f.Session,"DiscoverBatch",CancellationToken.None);Assert.Empty(f.Session.Movies);
        var candidates=Field<Dictionary<string,(long Length,long WriteTicks,DateTime Observed)>>(f.Session,"_candidates");
        foreach(string key in candidates.Keys.ToArray()){var c=candidates[key];candidates[key]=(c.Length,c.WriteTicks,DateTime.UtcNow.AddSeconds(-5));}
        Directory.CreateDirectory(f.Session.Settings.OutputPath);Directory.CreateDirectory(Path.Combine(f.Session.Settings.OutputPath,"input"));
        SetField(f.Session,"_nextDiscovery",DateTime.MinValue);Invoke(f.Session,"DiscoverBatch",CancellationToken.None);
        Invoke(f.Session,"Publish");Assert.Equal(2,f.Session.Movies.Count);Assert.Equal(2,f.Session.Movies.Select(m=>m.ProcessingPath).Distinct().Count());
    }
    [Fact]
    public async Task CancelledRunCreatesNoTasks()
    {
        using var f=new Fixture();using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await f.Manager.RunAsync(f.Identity,f.Session.Settings,cancelled.Token);
        Assert.False(f.Session.Running);Assert.Empty(Directory.EnumerateFiles(f.Identity.QueueDirectory,"*.json",SearchOption.AllDirectories));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalTasksFailIndividuallyOrDiscardChangedInput(bool sourceChanged)
    {
        using var f=new Fixture();var movie=f.AddMovie();
        File.WriteAllText(movie.InputPath,"raw");var source=new FileInfo(movie.InputPath);
        movie.SourceLength=source.Length;movie.SourceWriteTicks=source.LastWriteTimeUtc.Ticks;
        movie.Completed.Clear();movie.Done=0;
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();var queue=new TaskQueue(layout);
        Invoke(f.Session,"Dispatch",queue,CancellationToken.None);
        var pending=Field<System.Collections.IDictionary>(f.Session,"_inflight");string task=(string)pending.Keys.Cast<object>().Single();
        if(sourceChanged){File.AppendAllText(movie.InputPath,"changed");File.WriteAllText(Path.Combine(layout.Done,task+".json"),"{}");}
        else File.WriteAllText(Path.Combine(layout.Poisoned,task+".json"),new TaskItem{TaskId=task,Error="GPU worker failed"}.ToJson());
        Invoke(f.Session,"CollectCompletions",layout,CancellationToken.None);
        Invoke(f.Session,"RefreshStates");Invoke(f.Session,"Publish");
        Assert.Empty(pending);Assert.Equal("",movie.ActiveStage);
        if(sourceChanged){Assert.False(movie.InputReady);Assert.Equal(WarpStage.None,movie.Done);Assert.Empty(movie.Completed);}
        else{Assert.True(movie.Failed);Assert.Contains("GPU worker failed",movie.Error);}
    }
    [Fact]
    public async Task IdleNotificationsDoNotDirtyCheckpointOrPlotArrays()
    {
        using var f=new Fixture();
        await (Task)Invoke(f.Session,"SaveAsync")!;DateTime saved=f.Session.SavedAt;int plot=f.Session.PlotVersion;
        Invoke(f.Session,"RefreshStates");Invoke(f.Session,"Publish");
        Assert.False(Field<bool>(f.Session,"_dirty"));Assert.Equal(plot,f.Session.PlotVersion);
        await (Task)Invoke(f.Session,"SaveAsync")!;Assert.Equal(saved,f.Session.SavedAt);
        f.Session.FilterMetric("motion-max",3);Assert.True(Field<bool>(f.Session,"_dirty"));Assert.True(f.Session.PlotVersion>plot);
    }
    [Fact]
    public void TenThousandRowsReuseUnchangedSnapshotsAcrossConsumers()
    {
        using var f=new Fixture();var rows=Field<List<WarpMovie>>(f.Session,"_movies");
        for(int i=0;i<10001;i++)rows.Add(new(){Index=i,Id=i.ToString(),Name=i.ToString(),InputPath=Path.Combine(f.Root,i+".eer")});
        f.Session.SetDisposition(Enumerable.Range(0,rows.Count),WarpDisposition.Auto);
        var before=f.Session.Movies;var second=f.Manager.GetOrCreate(f.Identity);Assert.Same(before,second.Movies);
        f.Session.SetDisposition([5000],WarpDisposition.Exclude);
        Assert.Equal(10001,second.Movies.Count);Assert.Same(before[0],second.Movies[0]);Assert.Same(before[10000],second.Movies[10000]);Assert.NotSame(before[5000],second.Movies[5000]);
    }
    [Fact]
    public void FailingViewDoesNotBlockOtherViewersOrSessionMutations()
    {
        using var f = new Fixture();
        int delivered = 0;
        f.Session.Changed += () => throw new InvalidOperationException("Closed circuit");
        f.Session.Changed += () => delivered++;
        f.Session.FilterMetric("motion-max", 3);
        Assert.Equal(1, delivered);
        Assert.Equal(3, f.Session.MaxMotion);
    }
    [Fact]
    public void MissingModelFileKeepsDenoisedResultsAndWaitsForTheNextModel()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{PickingEnabled=false});
        InstallModel(f.Session,f.Root,1);var movie=f.AddMovie();movie.DenoiseModel=1;f.Session.SetDisposition([0],WarpDisposition.Auto);
        string model=Field<string>(f.Session,"_modelPath");f.Manager.Remove(f.Identity);File.Delete(model);
        var restored=f.Manager.GetOrCreate(f.Identity);
        Assert.False(restored.HasModel);Assert.Equal(1,restored.Model);
        Assert.Equal(WarpStage.None,restored.Movies[0].Stale);Assert.Equal(0,restored.OlderModelMovies);
    }
    [Fact]
    public async Task ForcedCheckpointDoesNotCaptureTheCallingCircuitContext()
    {
        using var f = new Fixture();
        var gate = Field<SemaphoreSlim>(f.Session, "_saveGate");
        await gate.WaitAsync();
        var callbacks = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var originalContext = SynchronizationContext.Current;
        Task task;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new QueuedContext(callbacks));
            task = (Task)Invoke(f.Session, "SaveAsync")!;
        }
        finally { SynchronizationContext.SetSynchronizationContext(originalContext); }
        gate.Release();
        bool completed = await Task.WhenAny(task, Task.Delay(5000)) == task;
        // Drain only to clean up a failing regression without leaving a blocked thread behind.
        if (!completed) while (callbacks.TryDequeue(out var callback)) callback();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed, "Checkpoint continuation captured a blocked browser circuit context.");
        Assert.Empty(callbacks);
    }
    private sealed class QueuedContext(System.Collections.Concurrent.ConcurrentQueue<Action> callbacks) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callbacks.Enqueue(() => callback(state));
    }
    [Fact]
    public void CorrectedProcessingSettingsRetryFailuresWithoutDiscardingSuccessfulStages()
    {
        using var f = new Fixture();
        var movie = f.AddMovie();
        movie.Failed = true; movie.Error = "Picking failed";
        f.Session.Apply(f.Session.Settings with { Recursive = true });
        Assert.True(movie.Failed);
        ulong movement = movie.Completed["motion"];
        f.Session.Apply(f.Session.Settings with { Diameter = 180 });
        Assert.False(movie.Failed);
        Assert.Empty(movie.Error);
        Assert.Equal(movement, movie.Completed["motion"]);
        Assert.False(movie.Stale.HasFlag(WarpStage.Motion));
        Assert.True(movie.Stale.HasFlag(WarpStage.Pick));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InitialStagesShareOneRawReadAndSumCtfLoadsOnlyTheSmallAverage(bool useSum)
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{UseMovieSum=useSum});var movie=f.AddMovie();
        movie.Completed.Clear();movie.Done=0;movie.AveragePath=Path.Combine(f.Root,"average.mrc");
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=TaskItem.FromJson(File.ReadAllText(Assert.Single(Directory.GetFiles(layout.Pending,"*.json"))));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.LoadStack&&(string)c.Content[0]==movie.InputPath));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieProcessMovement));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieExportMovie));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieProcessCTF));
        Assert.Equal(useSum?2:1,task.Main.Count(c=>c.Name==WorkerCommandNames.LoadStack));
        if(useSum)
        {
            int average=Array.FindIndex(task.Main,c=>c.Name==WorkerCommandNames.MovieExportMovie);
            int load=Array.FindIndex(task.Main,c=>c.Name==WorkerCommandNames.LoadStack&&(string)c.Content[0]==movie.AveragePath);
            Assert.True(load>average);Assert.False((bool)task.Main[load].Content[3]);
        }
        Assert.DoesNotContain(task.Main,c=>c.Name==WorkerCommandNames.MovieExportParticlesFromStar);
    }
    [Fact]
    public void CtfOnlyEditDoesNotRepeatMovementOrAveraging()
    {
        using var f=new Fixture();f.AddMovie();f.Session.Apply(f.Session.Settings with{CtfMax=3});
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=TaskItem.FromJson(File.ReadAllText(Assert.Single(Directory.GetFiles(layout.Pending,"*.json"))));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieProcessCTF));
        Assert.DoesNotContain(task.Main,c=>c.Name==WorkerCommandNames.MovieProcessMovement||c.Name==WorkerCommandNames.MovieExportMovie);
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.LoadStack));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovieTaskRecordsEveryFinishedStageEvenWhenALaterOneFails(bool failsAfterCtf)
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        File.WriteAllText(movie.InputPath,"raw");var source=new FileInfo(movie.InputPath);movie.SourceLength=source.Length;movie.SourceWriteTicks=source.LastWriteTimeUtc.Ticks;
        movie.ProcessingPath=Path.Combine(f.Root,"processed.eer");
        var metadata=new Movie(movie.ProcessingPath,f.Root,[]){OptionsMovement=new(),OptionsCTF=new(),MeanFrameMovement=1.2m,CTFResolutionEstimate=3.1m};metadata.SaveMeta();
        movie.MetadataPath=metadata.XMLPath;movie.AveragePath=metadata.AveragePath;movie.DeconvolvedPath=metadata.DeconvolvedPath;
        foreach(string path in new[]{movie.AveragePath,movie.DeconvolvedPath}){Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,"image");}
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=TaskItem.FromJson(File.ReadAllText(Assert.Single(Directory.GetFiles(layout.Pending,"*.json"))));
        if(failsAfterCtf)
        {
            // The worker got through CTF (and its trailing wait) before deconvolution failed.
            task.CompletedCommands=Array.FindIndex(task.Main,c=>c.Name==WorkerCommandNames.MoviePrepareDenoising);task.Error="Deconvolution failed";
            File.WriteAllText(Path.Combine(layout.Poisoned,task.TaskId+".json"),task.ToJson());
        }
        else File.WriteAllText(Path.Combine(layout.Done,task.TaskId+".json"),task.ToJson());
        Invoke(f.Session,"CollectCompletions",layout,CancellationToken.None);
        var keys=Field<Dictionary<string,ulong>>(f.Session,"_stageKeys");
        string[] finished=failsAfterCtf?["motion","average","ctf"]:["motion","average","ctf","prepare"];
        foreach(string stage in finished)Assert.Equal(keys[stage],movie.Completed[stage]);
        Assert.Equal(finished.Length,movie.Completed.Count);Assert.Equal(WarpStage.Motion|WarpStage.Ctf,movie.Done);Assert.Equal(1,movie.MotionRevision);Assert.Equal(1,movie.CtfRevision);
        Assert.Equal(failsAfterCtf,movie.Failed);if(failsAfterCtf)Assert.Contains("Deconvolution failed",movie.Error);
        Assert.Equal(1.2,movie.Motion,6);Assert.Equal(3.1,movie.Ctf,6);
    }
    private static void InstallModel(WarpSession session,string root,int version)
    {
        string model=Path.Combine(root,$"model-{version}.pt");File.WriteAllText(model,"model "+version);
        SetField(session,"_modelPath",model);SetField(session,"<Model>k__BackingField",version);
        Invoke(session,"RecomputeStageKeys");SetField(session,"_modelPreparationKey",Field<Dictionary<string,ulong>>(session,"_stageKeys")["prepare"]);
        Invoke(session,"RefreshStates");Invoke(session,"Publish");
    }
    private static TaskItem[] PendingTasks(QueueLayout layout)=>Directory.GetFiles(layout.Pending,"*.json").Select(p=>TaskItem.FromJson(File.ReadAllText(p))).ToArray();
    [Fact]
    public void NewerModelLeavesDenoisedMoviesCurrent()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,1);var movie=f.AddMovie();movie.DenoiseModel=1;f.Session.SetDisposition([0],WarpDisposition.Auto);
        InstallModel(f.Session,f.Root,2);
        Assert.Equal(WarpStage.None,f.Session.Movies[0].Stale);
        Assert.Equal(1,f.Session.OlderModelMovies);
    }
    [Fact]
    public void RedoWithCurrentModelOutdatesOnlyOlderDenoisingAndSurvivesRestart()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,2);
        var older=f.AddMovie();older.DenoiseModel=1;var current=f.AddMovie();current.DenoiseModel=2;f.Session.SetDisposition([0,1],WarpDisposition.Auto);
        Assert.Equal(1,f.Session.OlderModelMovies);
        f.Session.RedoDenoising();
        Assert.Equal(WarpStage.Enhance|WarpStage.Pick|WarpStage.Extract,f.Session.Movies[0].Stale);Assert.Equal(WarpStage.None,f.Session.Movies[1].Stale);
        Assert.Equal(0,f.Session.OlderModelMovies);
        f.Manager.Remove(f.Identity);var restored=f.Manager.GetOrCreate(f.Identity);
        Assert.Equal(WarpStage.Enhance|WarpStage.Pick|WarpStage.Extract,restored.Movies[0].Stale);Assert.Equal(WarpStage.None,restored.Movies[1].Stale);
    }
    [Fact]
    public void WithModelOneTaskRunsTheWholeMovieFromOneRawRead()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,1);var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=Assert.Single(PendingTasks(layout));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.LoadStack));
        foreach(string name in new[]{WorkerCommandNames.MovieProcessMovement,WorkerCommandNames.MovieExportMovie,WorkerCommandNames.MovieProcessCTF,
            WorkerCommandNames.MoviePrepareDenoising,WorkerCommandNames.MovieDenoise,WorkerCommandNames.MicrographPickBlobs,WorkerCommandNames.MovieExportParticlesFromStar})
            Assert.Single(task.Main.Where(c=>c.Name==name));
    }
    [Fact]
    public void MovieTaskWritesPreviewsOfTheAverageAndTheDenoisedImage()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,1);var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        movie.ProcessingPath=Path.Combine(f.Root,"movie.eer");movie.AveragePath=Path.Combine(f.Root,"average","movie.mrc");
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var previews=Assert.Single(PendingTasks(layout)).Main.Where(c=>c.Name==WorkerCommandNames.MicrographCreatePreview).Select(c=>((string)c.Content[0],(string)c.Content[1])).ToArray();
        string denoised=new Movie(movie.ProcessingPath).AverageDenoisedPath;
        Assert.Equal([(movie.AveragePath,WarpSession.PreviewPath(movie.AveragePath)),(denoised,WarpSession.PreviewPath(denoised))],previews);
        Assert.EndsWith(".png",WarpSession.PreviewPath(movie.AveragePath));
    }
    [Fact]
    public void WithoutModelTheMovieStopsAfterDeconvolution()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=Assert.Single(PendingTasks(layout));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MoviePrepareDenoising));
        Assert.DoesNotContain(task.Main,c=>c.Name is WorkerCommandNames.MovieDenoise or WorkerCommandNames.MicrographPickBlobs or WorkerCommandNames.MovieExportParticlesFromStar);
    }
    [Fact]
    public void TrainingHalvesCoverTheFirstSampleThenEveryHundredthMovie()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{TrainCount=2});
        var rows=Field<List<WarpMovie>>(f.Session,"_movies");
        for(int i=0;i<101;i++){var m=f.AddMovie();m.Completed.Clear();m.Done=0;m.ProcessingPath=Path.Combine(f.Root,$"processing-{i}.eer");}
        // Only the first and the hundredth-and-first are dispatched beyond the sample: everything else is held back.
        foreach(var m in rows.Skip(3).Take(97))m.InputReady=false;
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        bool Halves(int index)=>PendingTasks(layout).Where(t=>t.Main.Any(c=>c.Name==WorkerCommandNames.MovieExportMovie&&(string)c.Content[0]==rows[index].ProcessingPath))
            .Select(t=>((ProcessingOptionsMovieExport)t.Main.First(c=>c.Name==WorkerCommandNames.MovieExportMovie).Content[1]).DoDenoise).Single();
        Assert.True(Halves(0));Assert.True(Halves(1));Assert.False(Halves(2));Assert.True(Halves(100));
    }
    [Fact]
    public void FineTuningCountsProcessedMoviesAndTrainsWithPriority()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{TrainCount=2,FineTune=true,FineTuneEvery=2});InstallModel(f.Session,f.Root,1);
        for(int i=0;i<3;i++){var m=f.AddMovie();m.TrainingPairReady=i<2;m.DenoiseModel=1;}
        SetField(f.Session,"_lastTrainingCount",1);SetField(f.Session,"<Running>k__BackingField",true);
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var train=Assert.Single(PendingTasks(layout));
        Assert.Equal("train",train.Stage);Assert.True(train.Priority);Assert.StartsWith(TaskQueue.PriorityPrefix,train.TaskId);
    }
    [Theory]
    [InlineData(false,false,"")]
    [InlineData(true,false,"start")]
    [InlineData(true,true,"current")]
    public void TrainingFineTunesTheStartingModelUntilTheSessionHasItsOwn(bool startingModel,bool sessionModel,string expected)
    {
        using var f=new Fixture();string start=Path.Combine(f.Root,"corpus.pt");File.WriteAllText(start,"weights");
        f.Session.Apply(f.Session.Settings with{TrainCount=1,DenoiserStart=startingModel?start:""});
        if(sessionModel)InstallModel(f.Session,f.Root,1);
        var movie=f.AddMovie();movie.TrainingPairReady=true;movie.DenoiseModel=sessionModel?1:0;
        SetField(f.Session,"<Running>k__BackingField",true);f.Session.Train();
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var train=Assert.Single(PendingTasks(layout),t=>t.Stage=="train");
        string previous=(string)train.Main.Single(c=>c.Name==WorkerCommandNames.TrainMovieDenoiser).Content[2];
        Assert.Equal(expected switch{"start"=>start,"current"=>Field<string>(f.Session,"_modelPath"),_=>""},previous);
    }
    [Fact]
    public void MissingStartingModelIsRejected()
    {
        using var f=new Fixture();
        var error=Assert.Throws<ArgumentException>(()=>f.Session.Apply(f.Session.Settings with{DenoiserStart=Path.Combine(f.Root,"missing.pt")}));
        Assert.Contains("starting model",error.Message,StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void NewModelTakesBackWaitingTasksSoTheyAreReissuedWithDenoising()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{TrainCount=1});var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();var queue=new TaskQueue(layout);
        Invoke(f.Session,"Dispatch",queue,CancellationToken.None);
        Assert.DoesNotContain(Assert.Single(PendingTasks(layout)).Main,c=>c.Name==WorkerCommandNames.MovieDenoise);
        string model=Path.Combine(f.Root,"trained.pt");File.WriteAllText(model,"model");
        var inflight=Field<System.Collections.IDictionary>(f.Session,"_inflight");
        var trainType=inflight.GetType().GetGenericArguments()[1];
        object pendingTrain=Activator.CreateInstance(trainType,-1,"train",Field<Dictionary<string,ulong>>(f.Session,"_stageKeys")["prepare"],model,0L,0L,null,0,false)!;
        inflight["train-task"]=pendingTrain;File.WriteAllText(Path.Combine(layout.Done,"train-task.json"),"{}");
        Invoke(f.Session,"CollectCompletions",layout,CancellationToken.None);
        Assert.Empty(PendingTasks(layout));Assert.Empty(inflight);Assert.Equal("",movie.ActiveStage);Assert.Equal(1,f.Session.Model);
        Invoke(f.Session,"Dispatch",queue,CancellationToken.None);
        Assert.Single(Assert.Single(PendingTasks(layout)).Main.Where(c=>c.Name==WorkerCommandNames.MovieDenoise));
    }
    private static T Field<T>(object obj,string name)=>(T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj)!;
    private static void SetField(object obj,string name,object value)=>obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(obj,value);
    private static object? Invoke(object obj,string name,params object[] args)=>obj.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(obj,args);
    private sealed class Fixture:IDisposable
    {
        public string Root{get;}=Path.Combine(Path.GetTempPath(),"warp-service-"+Guid.NewGuid().ToString("N"));
        public WarpSessionManager Manager{get;}=new();
        public WarpSessionIdentity Identity{get;}
        public WarpSession Session{get;}
        public Fixture(){Directory.CreateDirectory(Root);Identity=new(1,2,3,Root,Path.Combine(Root,"tasks"));Session=Manager.GetOrCreate(Identity);Session.Apply(new(){InputPath=Root});}
        public WarpMovie AddMovie(double power=1)
        {
            var movies=Field<List<WarpMovie>>(Session,"_movies");int index=movies.Count;
            string star=Path.Combine(Root,$"{index}.star");File.WriteAllText(star,FormattableString.Invariant($"data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnAutopickFigureOfMerit #3\n_wrpAutopickPower #4\n10 20 0.5 {power}\n30 40 0.9 {power/2}\n"));
            var row=new WarpMovie{Index=index,Id=index.ToString(),Name=index.ToString(),InputPath=Path.Combine(Root,$"{index}.eer"),ParticleStarPath=star,Done=WarpStage.All,PickingRevision=1,Completed=new(Field<Dictionary<string,ulong>>(Session,"_stageKeys"))};
            movies.Add(row);Session.SetDisposition([index],WarpDisposition.Auto);return row;
        }
        public void Dispose(){Manager.Dispose();if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
}
