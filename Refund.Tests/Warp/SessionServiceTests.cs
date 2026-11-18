using System.Globalization;
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
        Assert.InRange(f.Session.GetParticleHistogram().CountWithin(new(.6,1,0,null)),0,4);
        f.Session.Apply(f.Session.Settings with{InputPattern="*.mrc"});Assert.Equal(4,f.Session.GetParticleHistogram().Total);
        // New score bounds redo extraction, not picking, so the histogram of every pick stays.
        f.Session.Apply(f.Session.Settings with{ParticleBounds=new(.8,1,0,null)});Assert.Equal(4,f.Session.GetParticleHistogram().Total);
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
    public void CheckpointSavesTheSessionsFilesRelativeAndFollowsAMovedSession()
    {
        string space=Path.Combine(Path.GetTempPath(),"warp-move-"+Guid.NewGuid().ToString("N")),moved=space+"-moved",raw=space+"-raw";
        string session=Path.Combine(space,"J3");Directory.CreateDirectory(session);Directory.CreateDirectory(raw);
        try
        {
            var identity=new WarpSessionIdentity(1,2,3,session,Path.Combine(session,"tasks"),space);
            using(var manager=new WarpSessionManager())
            {
                var s=manager.GetOrCreate(identity);s.Apply(new(){InputPath=raw,PickingEnabled=false});
                Directory.CreateDirectory(Path.Combine(session,"models"));InstallModel(s,Path.Combine(session,"models"),1);
                string processing=Path.Combine(session,"processing");
                var movies=Field<List<WarpMovie>>(s,"_movies");
                movies.Add(new WarpMovie{Index=0,Id="0",Name="a.eer",InputPath=Path.Combine(raw,"a.eer"),ProcessingPath=Path.Combine(processing,"x_a.eer"),
                    MetadataPath=Path.Combine(processing,"x_a.xml"),AveragePath=Path.Combine(processing,"average","x_a.mrc"),Done=WarpStage.Motion,
                    Completed=new(Field<Dictionary<string,ulong>>(s,"_stageKeys"))});
                s.SetDisposition([0],WarpDisposition.Auto);
            }
            // The session's own files are saved relative to its folder; the raw movie stays absolute.
            var saved=System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(session,"warp-mini.json")))!;
            var row=saved["Movies"]![0]!;
            Assert.Equal(Path.Combine("processing","x_a.eer"),(string)row["ProcessingPath"]!);
            Assert.Equal(Path.Combine("processing","average","x_a.mrc"),(string)row["AveragePath"]!);
            Assert.Equal(Path.Combine(raw,"a.eer"),(string)row["InputPath"]!);
            Assert.Equal("processing",(string)saved["Settings"]!["OutputPath"]!);
            Assert.Equal(Path.Combine("models","model-1.pt"),(string)saved["ModelPath"]!);

            // Once the space moves, they move with it.
            Directory.Move(space,moved);
            string movedSession=Path.Combine(moved,"J3");
            using(var manager=new WarpSessionManager())
            {
                var restored=manager.GetOrCreate(new WarpSessionIdentity(1,2,3,movedSession,Path.Combine(movedSession,"tasks"),moved));
                var movie=restored.Movies[0];
                Assert.Equal(Path.Combine(movedSession,"processing","x_a.eer"),movie.ProcessingPath);
                Assert.Equal(Path.Combine(movedSession,"processing","average","x_a.mrc"),movie.AveragePath);
                Assert.Equal(Path.Combine(raw,"a.eer"),movie.InputPath);
                Assert.Equal(Path.Combine(movedSession,"processing"),restored.Settings.OutputPath);
                Assert.True(restored.HasModel);
                Assert.Equal(WarpStage.None,movie.Stale);
            }
        }
        finally{foreach(string d in new[]{space,moved,raw})if(Directory.Exists(d))Directory.Delete(d,true);}
    }
    [Fact]
    public void PathsInsideTheRootBecomeRelativeAndOthersStayAbsolute()
    {
        string root=Path.Combine(Path.GetTempPath(),"space");
        Assert.Equal(Path.Combine("J1","processing","a.eer"),WarpPaths.Relative(root,Path.Combine(root,"J1","processing","a.eer")));
        Assert.Equal(".",WarpPaths.Relative(root,root+Path.DirectorySeparatorChar));
        string outside=Path.Combine(Path.GetTempPath(),"spaces","raw.eer"),sibling=Path.Combine(Path.GetTempPath(),"space2","a.eer");
        Assert.Equal(outside,WarpPaths.Relative(root,outside));
        Assert.Equal(sibling,WarpPaths.Relative(root,sibling));
        Assert.True(Path.IsPathRooted(WarpPaths.Relative(root,Path.Combine(root,"..","other.eer"))));
        Assert.Equal(Path.Combine(root,"J1","a.eer"),WarpPaths.Resolve(root,Path.Combine("J1","a.eer")));
        Assert.Equal(outside,WarpPaths.Resolve(root,outside));
        Assert.Equal("",WarpPaths.Relative(root,""));
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
    [Fact]
    public void MotionEditsRedoCtfBecauseSpectraComeFromAlignedFrameGroups()
    {
        using var f=new Fixture();var movie=f.AddMovie();
        f.Session.Apply(f.Session.Settings with{MotionMax=f.Session.Settings.MotionMax+1});
        Assert.True(movie.Stale.HasFlag(WarpStage.Motion));
        Assert.True(movie.Stale.HasFlag(WarpStage.Ctf));
    }
    [Fact]
    public void MoviesWithoutCtfSignalAreFilteredUntilTheThresholdIsLowered()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Ctf=3;movie.Motion=1;
        Assert.Equal(global::Warp.Tools.CtfSignal.DefaultThreshold,f.Session.MinCtfSignal);
        movie.CtfSignal=12;Assert.True(f.Session.PassesFilters(movie));
        movie.CtfSignal=2;Assert.False(f.Session.PassesFilters(movie));
        // A CTF fitted before scores existed is not judged by one.
        movie.CtfSignal=double.NaN;Assert.True(f.Session.PassesFilters(movie));
        movie.CtfSignal=2;f.Session.FilterMetric("ctf-signal-min",1.5);Assert.True(f.Session.PassesFilters(movie));
    }
    [Fact]
    public void AnUpperCtfSignalLimitSelectsPoorFits()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Ctf=3;movie.Motion=1;
        Assert.Null(f.Session.MaxCtfSignal);
        f.Session.FilterMetric("ctf-signal-min",0);f.Session.FilterCtfSignalMaximum(4);
        movie.CtfSignal=12;Assert.False(f.Session.PassesFilters(movie));
        movie.CtfSignal=2;Assert.True(f.Session.PassesFilters(movie));
        // The limits cannot cross, and clearing the maximum removes it.
        f.Session.FilterMetric("ctf-signal-min",6);Assert.Equal(4,f.Session.MinCtfSignal);
        f.Session.FilterCtfSignalMaximum(1);Assert.Equal(4,f.Session.MaxCtfSignal);
        f.Session.FilterCtfSignalMaximum(null);movie.CtfSignal=12;Assert.True(f.Session.PassesFilters(movie));
    }
    [Fact]
    public void ProcessedAndAcceptedCountOnlyFullyProcessedMovies()
    {
        using var f=new Fixture();var done=f.AddMovie();var partial=f.AddMovie();var outdated=f.AddMovie();
        foreach(var m in new[]{done,partial,outdated}){m.Ctf=3;m.Motion=1;m.CtfSignal=12;}
        partial.Done=WarpStage.Motion|WarpStage.Ctf;outdated.Completed["ctf"]^=1;
        Invoke(f.Session,"RefreshStates");
        Assert.Equal(1,f.Session.Complete);
        Assert.Equal(1,f.Session.Accepted);
    }
    [Fact]
    public void ProcessingRateCountsTheLastTenMinutesOrTheRunSoFar()
    {
        var now=new DateTime(2026,11,13,12,0,0,DateTimeKind.Utc);
        Queue<DateTime> Ago(params double[] minutes)=>new(minutes.OrderByDescending(m=>m).Select(m=>now.AddMinutes(-m)));
        Assert.Null(WarpSession.Rate(Ago(1,2),now,null));
        Assert.Null(WarpSession.Rate(Ago(.2),now,now.AddSeconds(-30)));
        // Five minutes into a run, ten movies are 120 an hour.
        Assert.Equal(120,WarpSession.Rate(Ago(Enumerable.Range(0,10).Select(i=>i*.4).ToArray()),now,now.AddMinutes(-5))!.Value,6);
        // An hour in, only the last ten minutes count, and older completions are dropped.
        var queue=Ago(30,20,9,5,1);
        Assert.Equal(18,WarpSession.Rate(queue,now,now.AddHours(-1))!.Value,6);Assert.Equal(3,queue.Count);
    }
    [Fact]
    public void OnlyMoviesFinishingDuringARunCountTowardsTheRate()
    {
        using var f=new Fixture();f.AddMovie();var redone=f.AddMovie();redone.Completed["ctf"]^=1;
        Invoke(f.Session,"RefreshStates");
        SetField(f.Session,"<Running>k__BackingField",true);
        var completions=Field<Queue<DateTime>>(f.Session,"_completions");
        Invoke(f.Session,"RefreshStates");Assert.Empty(completions);
        redone.Completed["ctf"]^=1;
        Invoke(f.Session,"RefreshStates");Invoke(f.Session,"RefreshStates");
        Assert.Single(completions);
    }
    [Fact]
    public void ParticleLimitsDoNotApplyWithoutPicking()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Ctf=3;movie.Motion=1;movie.Picks=0;
        f.Session.FilterMetric("particles-min",10);Assert.False(f.Session.PassesFilters(movie));
        f.Session.Apply(f.Session.Settings with{PickingEnabled=false});
        Assert.True(f.Session.PassesFilters(movie));
    }
    [Fact]
    public void FiltersSavedBeforeCtfSignalUseTheDefaultThreshold()
    {
        var filters=System.Text.Json.JsonSerializer.Deserialize<WarpFilters>("{\"MinCtf\":1,\"MaxCtf\":4}")!;
        Assert.Equal(global::Warp.Tools.CtfSignal.DefaultThreshold,filters.MinCtfSignal);
        Assert.Null(filters.MaxCtfSignal);
        Assert.Equal(4,filters.MaxCtf);
    }
    [Fact]
    public void ScoringCtfSignalRedoesCtfAloneAndKeepsLaterResults()
    {
        // A movie whose CTF result predates the current score revision: only that stage's key differs.
        using var f=new Fixture();var movie=f.AddMovie();movie.Completed["ctf"]^=1;
        Invoke(f.Session,"RefreshStates");
        Assert.Equal(WarpStage.Ctf,movie.Stale);
        Assert.Equal(["ctf"],(string[])Invoke(f.Session,"PendingStages",movie)!);
    }
    [Fact]
    public void DoseRedoesCtfEvenOnAFixedGridBecauseItSizesTheFrameGroups()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{CtfGridAuto=false});var movie=f.AddMovie();
        f.Session.Apply(f.Session.Settings with{TotalDose=f.Session.Settings.TotalDose+10});
        Assert.True(movie.Stale.HasFlag(WarpStage.Ctf));
    }
    [Fact]
    public void InitialStagesShareOneRawRead()
    {
        using var f=new Fixture();var movie=f.AddMovie();
        movie.Completed.Clear();movie.Done=0;movie.AveragePath=Path.Combine(f.Root,"average.mrc");
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=TaskItem.FromJson(File.ReadAllText(Assert.Single(Directory.GetFiles(layout.Pending,"*.json"))));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.LoadStack&&(string)c.Content[0]==movie.InputPath));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieProcessMovement));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieExportMovie));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieProcessCTF));
        Assert.Equal(1,task.Main.Count(c=>c.Name==WorkerCommandNames.LoadStack));
        // CTF aligns frame groups with the motion fitted earlier in the same task.
        Assert.True(Array.FindIndex(task.Main,c=>c.Name==WorkerCommandNames.MovieProcessCTF)>Array.FindIndex(task.Main,c=>c.Name==WorkerCommandNames.MovieProcessMovement));
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
    public void MovieTaskRecordsEveryFinishedStageEvenWhenALaterOneFails(bool failsInCtf)
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        File.WriteAllText(movie.InputPath,"raw");var source=new FileInfo(movie.InputPath);movie.SourceLength=source.Length;movie.SourceWriteTicks=source.LastWriteTimeUtc.Ticks;
        movie.ProcessingPath=Path.Combine(f.Root,"processed.eer");
        var metadata=new Movie(movie.ProcessingPath,f.Root,[]){OptionsMovement=new(),OptionsCTF=new(),MeanFrameMovement=1.2m,CTFResolutionEstimate=3.1m};metadata.SaveMeta();
        movie.MetadataPath=metadata.XMLPath;movie.AveragePath=metadata.AveragePath;
        Directory.CreateDirectory(Path.GetDirectoryName(movie.AveragePath)!);File.WriteAllText(movie.AveragePath,"image");
        MovieJunk.WriteMask(metadata.MaskPath,new bool[16*16],16,16);
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=TaskItem.FromJson(File.ReadAllText(Assert.Single(Directory.GetFiles(layout.Pending,"*.json"))));
        if(failsInCtf)
        {
            // The worker got through motion, averaging and junk detection before CTF estimation failed.
            task.CompletedCommands=Array.FindIndex(task.Main,c=>c.Name==WorkerCommandNames.MovieProcessCTF);task.Error="CTF failed";
            File.WriteAllText(Path.Combine(layout.Poisoned,task.TaskId+".json"),task.ToJson());
        }
        else File.WriteAllText(Path.Combine(layout.Done,task.TaskId+".json"),task.ToJson());
        Invoke(f.Session,"CollectCompletions",layout,CancellationToken.None);
        var keys=Field<Dictionary<string,ulong>>(f.Session,"_stageKeys");
        // Without a model, a movie's task ends after CTF estimation: denoising waits for the first model.
        // Junk detection sets no result flag of its own: the micrograph is not enhanced yet.
        string[] finished=failsInCtf?["motion","average","junk"]:["motion","average","junk","ctf"];
        foreach(string stage in finished)Assert.Equal(keys[stage],movie.Completed[stage]);
        Assert.Equal(finished.Length,movie.Completed.Count);Assert.Equal(failsInCtf?WarpStage.Motion:WarpStage.Motion|WarpStage.Ctf,movie.Done);
        Assert.Equal(1,movie.MotionRevision);Assert.Equal(failsInCtf?0:1,movie.CtfRevision);
        Assert.Equal(failsInCtf,movie.Failed);if(failsInCtf)Assert.Contains("CTF failed",movie.Error);
        Assert.Equal(1.2,movie.Motion,6);if(!failsInCtf)Assert.Equal(3.1,movie.Ctf,6);
    }
    [Fact]
    public void NewParticleBoundsRedoOnlyExtractionAndTravelWithItsCommand()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Picks=2;movie.Particles=2;movie.EnhancedPixelSize=5;
        movie.ProcessingPath=Path.Combine(f.Root,"0.eer");movie.ParticleStackPath=Path.Combine(f.Root,"particles","0_warp.mrcs");
        var bounds=new WarpParticleBounds(.6,1,0,null);
        Assert.Equal("Redo extraction for 1 micrograph. Picking is not redone.",f.Session.GetParticleBoundsImpact(bounds));
        Assert.Equal("",f.Session.GetParticleBoundsImpact(f.Session.Settings.ParticleBounds));
        f.Session.Apply(f.Session.Settings with{ParticleBounds=bounds});
        Assert.Equal(WarpStage.Extract,movie.Stale);
        Assert.True(f.Session.HasCurrentParticleMetrics(movie));Assert.False(f.Session.HasCurrentExtraction(movie));
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=Assert.Single(PendingTasks(layout));
        var export=Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieExportParticlesFromStar));
        Assert.Equal(new object[]{.6,1.0,0.0,double.MaxValue,movie.ExtractedStarPath},export.Content[4..]);
        Assert.EndsWith("0_warp.star",movie.ExtractedStarPath);
        Assert.DoesNotContain(task.Main,c=>c.Name is WorkerCommandNames.MicrographPickBlobs or WorkerCommandNames.MovieDenoise or WorkerCommandNames.MovieProcessCTF);
    }
    [Fact]
    public void BoundsThatKeepNoPicksFinishExtractionWithoutAWorkerAndRemoveTheOldStack()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Picks=2;movie.Particles=2;
        movie.ParticleStackPath=Path.Combine(f.Root,"0_warp.mrcs");File.WriteAllText(movie.ParticleStackPath,"stack");File.WriteAllText(movie.ExtractedStarPath,"list");
        f.Session.Apply(f.Session.Settings with{ParticleBounds=new(.95,1,0,null)});
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        Assert.Empty(PendingTasks(layout));
        Invoke(f.Session,"RefreshStates");
        Assert.Equal(WarpStage.None,movie.Stale);Assert.Equal(0,movie.Particles);Assert.True(f.Session.HasCurrentExtraction(movie));
        Assert.False(File.Exists(movie.ParticleStackPath));Assert.False(File.Exists(movie.ExtractedStarPath));
        Assert.Null(f.Session.GetExtractedParticleStar(movie.Index));
    }
    [Fact]
    public void ParticleTotalsAndLimitsCountExtractedParticlesNotPicks()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Ctf=3;movie.Motion=1;movie.CtfSignal=12;movie.Picks=40;movie.Particles=12;
        Invoke(f.Session,"RefreshStates");Assert.Equal(12,f.Session.ParticleCount);
        f.Session.FilterMetric("particles-min",20);Assert.False(f.Session.PassesFilters(movie));
        f.Session.FilterMetric("particles-min",10);Assert.True(f.Session.PassesFilters(movie));
    }
    [Fact]
    public void TriedBoundsShowTheirPicksWhileTheMonitorShowsWhatWasExtracted()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Picks=2;movie.Particles=1;movie.EnhancedPixelSize=5;
        movie.ParticleStackPath=Path.Combine(f.Root,"0_warp.mrcs");
        File.WriteAllText(movie.ExtractedStarPath,"data_particles\n\nloop_\n_rlnCoordinateX #1\n_rlnCoordinateY #2\n_rlnAutopickFigureOfMerit #3\n_wrpAutopickPower #4\n30 40 0.9 0.5\n");
        var tried=new WarpParticleBounds(0,.6,0,null);
        Assert.Equal(1,f.Session.CountPicksWithin(0,tried));Assert.Equal(2,f.Session.CountPicksWithin(0,new()));
        // Overlays are in the displayed image's pixels: 5 Å picking pixels shown at 2.5 Å double the coordinates.
        Assert.Equal([(20.0,40.0)],Coordinates(f.Session.GetPicksWithinStar(0,tried,2.5)!));
        Assert.Equal([(60.0,80.0)],Coordinates(f.Session.GetExtractedParticleStar(0,2.5)!));
        // Once new bounds outdate the extraction, it is no longer shown; the picks can still be tried.
        f.Session.Apply(f.Session.Settings with{ParticleBounds=tried});
        Assert.Null(f.Session.GetExtractedParticleStar(0,2.5));Assert.NotNull(f.Session.GetPicksWithinStar(0,tried));
    }
    [Fact]
    public void SavedBoundsHoldOnlyTheLimits()
    {
        string json=System.Text.Json.JsonSerializer.Serialize(new WarpParticleBounds(.2,.9,0,null));
        Assert.DoesNotContain("Valid",json);
        Assert.Equal(new WarpParticleBounds(.2,.9,0,null),System.Text.Json.JsonSerializer.Deserialize<WarpParticleBounds>(json));
        Assert.Equal(new WarpParticleBounds(),System.Text.Json.JsonSerializer.Deserialize<WarpSettings>("{\"Box\":128}")!.ParticleBounds);
    }
    private static (double X,double Y)[] Coordinates(string path)
    {
        var table=new Star(path,"particles");
        return table.GetColumn("rlnCoordinateX").Zip(table.GetColumn("rlnCoordinateY"),(x,y)=>(double.Parse(x,CultureInfo.InvariantCulture),double.Parse(y,CultureInfo.InvariantCulture))).ToArray();
    }
    [Theory]
    [InlineData(.5,.4,0,null)]
    [InlineData(-.1,1,0,null)]
    [InlineData(0,1.2,0,null)]
    [InlineData(0,1,2,1.0)]
    public void InvalidParticleBoundsAreRejected(double minCorrelation,double maxCorrelation,double minPower,double? maxPower)
    {
        var settings=new WarpSettings{InputPath="/data"};
        Assert.Null(WarpSession.ValidateSettings(settings with{ParticleBounds=new(.2,.9,.1,4)}));
        Assert.NotNull(WarpSession.ValidateSettings(settings with{ParticleBounds=new(minCorrelation,maxCorrelation,minPower,maxPower)}));
        Assert.True(new WarpParticleBounds(minCorrelation,maxCorrelation,minPower,maxPower).Clamped().Valid);
    }
    private static void InstallModel(WarpSession session,string root,int version)
    {
        string model=Path.Combine(root,$"model-{version}.pt");File.WriteAllText(model,"model "+version);
        SetField(session,"_modelPath",model);SetField(session,"<Model>k__BackingField",version);
        Invoke(session,"RecomputeStageKeys");SetField(session,"_modelPreparationKey",ModelKeyOf(session));
        Invoke(session,"RefreshStates");Invoke(session,"Publish");
    }
    [Fact]
    public void CtfChangesKeepTheDenoiserAndRedoDeconvolutionAndDenoising()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,2);var movie=f.AddMovie();movie.DenoiseModel=2;
        string model=Field<string>(f.Session,"_modelPath");
        Assert.DoesNotContain("denoiser",f.Session.GetSettingsImpact(f.Session.Settings with{CtfMax=f.Session.Settings.CtfMax+1}));
        f.Session.Apply(f.Session.Settings with{CtfMax=f.Session.Settings.CtfMax+1});
        Assert.Equal(2,f.Session.Model);Assert.Equal(model,Field<string>(f.Session,"_modelPath"));
        Assert.True(movie.Stale.HasFlag(WarpStage.Ctf));Assert.True(movie.Stale.HasFlag(WarpStage.Enhance));
    }
    [Fact]
    public void DeconvolutionIsNoise2MicsAndRemovedSettingsAreIgnored()
    {
        // The keys are written with decimals; the worker gets Noise2Mic's floats. Both must be the same values.
        Assert.Equal(Noise2MicTorch.DeconvolutionStrength,(float)WarpSession.DeconvolutionStrength);
        Assert.Equal(Noise2MicTorch.DeconvolutionFalloff,(float)WarpSession.DeconvolutionFalloff);
        var saved=System.Text.Json.JsonSerializer.Deserialize<WarpSettings>("{\"Box\":128,\"ExportStack\":true,\"StackGroup\":4,\"DeconvolutionStrength\":2.5,\"DeconvolutionFalloff\":3}")!;
        Assert.Equal(new WarpSettings{Box=128},saved);
    }
    [Fact]
    public void ProcessedEndIsAfterTheLastMovieWithAnyResult()
    {
        using var f=new Fixture();
        Assert.Equal(0,f.Session.ProcessedEnd);
        var first=f.AddMovie();var second=f.AddMovie();var third=f.AddMovie();
        second.Done=WarpStage.Motion;second.Stale=WarpStage.Motion; // Partial and outdated still counts.
        third.Done=WarpStage.None;
        f.Session.SetDisposition([0,1,2],WarpDisposition.Auto); // Republishes the edited rows.
        Assert.Equal(2,f.Session.ProcessedEnd);
    }
    [Fact]
    public void MicrographsStayExportableWhileOnlyExtractionIsOutdated()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Picks=2;movie.Particles=2;movie.Ctf=3;movie.Motion=1;
        Assert.Equal((1,2L),f.Session.ExportCounts(true));Assert.Equal(1,f.Session.ExportCounts(false).Movies);
        f.Session.Apply(f.Session.Settings with{RescaleParticles=true,RescaledBox=f.Session.Settings.Box/2});
        Assert.Equal(0,f.Session.Accepted); // Accepted needs every stage; a micrograph export needs motion and CTF.
        Assert.Equal(1,f.Session.ExportCounts(false).Movies);Assert.Equal((0,0L),f.Session.ExportCounts(true));
    }
    [Fact]
    public void RescalingRedoesOnlyExtractionAndTravelsWithItsCommand()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Picks=2;movie.Particles=2;movie.EnhancedPixelSize=5;
        movie.ProcessingPath=Path.Combine(f.Root,"0.eer");movie.ParticleStackPath=Path.Combine(f.Root,"particles","0_warp.mrcs");
        var rescaled=f.Session.Settings with{RescaleParticles=true,RescaledBox=f.Session.Settings.Box/4};
        Assert.Equal("Redo extraction.",f.Session.GetSettingsImpact(rescaled));
        Assert.Equal(f.Session.Settings.Box/4,rescaled.ParticleBox);Assert.Equal(f.Session.Settings.TargetPixelSize*4,rescaled.ParticlePixelSize);
        f.Session.Apply(rescaled);
        Assert.Equal(WarpStage.Extract,movie.Stale);
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var export=Assert.Single(Assert.Single(PendingTasks(layout)).Main.Where(c=>c.Name==WorkerCommandNames.MovieExportParticlesFromStar));
        var options=(ProcessingOptionsParticleExport)export.Content[1];
        Assert.Equal(f.Session.Settings.Box,options.BoxSize);Assert.Equal(f.Session.Settings.Box/4,options.BoxSizeResample);
        // Off again, extraction made without rescaling is current: the option joins the key only when on.
        f.Session.Apply(rescaled with{RescaleParticles=false,RescaledBox=96});
        Assert.Equal(WarpStage.None,movie.Stale);
    }
    [Theory]
    [InlineData(127)]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(0)]
    public void RescaledBoxMustBeEvenAndSmallerThanTheExtractionBox(int box)
    {
        var settings=new WarpSettings{InputPath="/data",Box=256,RescaleParticles=true};
        Assert.Null(WarpSession.ValidateSettings(settings with{RescaledBox=128}));
        Assert.NotNull(WarpSession.ValidateSettings(settings with{RescaledBox=box}));
        Assert.Null(WarpSession.ValidateSettings(settings with{RescaledBox=box,RescaleParticles=false}));
    }
    [Fact]
    public void TrainingMoviesBeingDeconvolvedAgainGoFirstUntilTheFirstModel()
    {
        using var f=new Fixture();var training=f.AddMovie();var other=f.AddMovie();
        training.TrainingPairReady=true;
        foreach(var m in new[]{training,other}){m.Completed["ctf"]^=1;m.Completed.Remove("denoise");m.Completed.Remove("pick");m.Completed.Remove("extract");m.Done=WarpStage.Motion;}
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var tasks=PendingTasks(layout);
        Assert.True(Assert.Single(tasks,t=>t.TaskId.Contains($"-{training.Index:D8}-")).Priority);
        Assert.False(Assert.Single(tasks,t=>t.TaskId.Contains($"-{other.Index:D8}-")).Priority);
    }
    private static ulong ModelKeyOf(WarpSession session)=>(ulong)typeof(WarpSession).GetProperty("ModelKey",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(session)!;
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
            WorkerCommandNames.MovieDenoise,WorkerCommandNames.MicrographPickBlobs,WorkerCommandNames.MovieExportParticlesFromStar})
            Assert.Single(task.Main.Where(c=>c.Name==name));
    }
    [Fact]
    public void MovieTaskWritesPreviewsOfTheAverageAndTheDenoisedImage()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,1);var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        movie.ProcessingPath=Path.Combine(f.Root,"movie.eer");movie.AveragePath=Path.Combine(f.Root,"average","movie.mrc");
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var previews=Assert.Single(PendingTasks(layout)).Main.Where(c=>c.Name==WorkerCommandNames.MicrographCreatePreview)
            .Select(c=>((string)c.Content[0],(string)c.Content[1],(string)c.Content[3])).ToArray();
        var metadata=new Movie(movie.ProcessingPath);string denoised=metadata.AverageDenoisedPath,average=WarpSession.PreviewPath(movie.AveragePath);
        // Once junk is found, the average's preview is drawn again and the denoised one is drawn with the junk left out of their contrast.
        Assert.Equal([(movie.AveragePath,average,""),(movie.AveragePath,average,metadata.MaskPath),(denoised,WarpSession.PreviewPath(denoised),metadata.MaskPath)],previews);
        Assert.EndsWith(".png",average);
    }
    [Fact]
    public void WithoutJunkDetectionPreviewsStretchTheWholeCentre()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{DetectJunk=false});InstallModel(f.Session,f.Root,1);
        var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;movie.ProcessingPath=Path.Combine(f.Root,"movie.eer");
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var previews=Assert.Single(PendingTasks(layout)).Main.Where(c=>c.Name==WorkerCommandNames.MicrographCreatePreview).ToArray();
        Assert.Equal(2,previews.Length);
        Assert.All(previews,c=>Assert.Equal("",c.Content[3]));
    }
    [Fact]
    public void WithoutModelTheMovieStopsAfterCtf()
    {
        using var f=new Fixture();var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var task=Assert.Single(PendingTasks(layout));
        Assert.Single(task.Main.Where(c=>c.Name==WorkerCommandNames.MovieProcessCTF));
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
    public void BeforeTheFirstModelTrainingSampleMoviesRunFirst()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{TrainCount=2});
        var rows=Field<List<WarpMovie>>(f.Session,"_movies");
        for(int i=0;i<4;i++){var m=f.AddMovie();m.Completed.Clear();m.Done=0;m.ProcessingPath=Path.Combine(f.Root,$"processing-{i}.eer");}
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        bool Priority(int index)=>PendingTasks(layout).Single(t=>t.Main.Any(c=>c.Name==WorkerCommandNames.MovieExportMovie&&(string)c.Content[0]==rows[index].ProcessingPath)).Priority;
        Assert.True(Priority(0));Assert.True(Priority(1));Assert.False(Priority(2));Assert.False(Priority(3));
    }
    [Fact]
    public void WithAModelTrainingHalvesDoNotJumpTheQueue()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{TrainCount=2});InstallModel(f.Session,f.Root,1);
        var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=6;movie.ProcessingPath=Path.Combine(f.Root,"processing.eer");
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        Assert.False(Assert.Single(PendingTasks(layout)).Priority);
    }
    [Fact]
    public void TrainingProgressComesFromTheWorkersProgressFile()
    {
        using var f=new Fixture();string output=Path.Combine(f.Root,"models","next.pt");Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        SetField(f.Session,"<Training>k__BackingField",true);SetField(f.Session,"_trainingOutput",output);
        new global::Warp.Tools.TrainingProgress(1280,3200,4,8,.4f).Write(global::Warp.Tools.TrainingProgress.PathFor(output));
        Invoke(f.Session,"UpdateTrainingProgress");
        Assert.Equal(40,f.Session.TrainingPercent);Assert.Equal(4,f.Session.TrainingProgress!.Epoch);
    }
    [Fact]
    public void FirstTrainingHasTheGpuToItself()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{TrainCount=1});
        var sample=f.AddMovie();sample.TrainingPairReady=true;sample.ProcessingPath=Path.Combine(f.Root,"sample.eer");
        var waiting=f.AddMovie();waiting.Completed.Clear();waiting.Done=0;waiting.ProcessingPath=Path.Combine(f.Root,"waiting.eer");
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();var queue=new TaskQueue(layout);
        // A movie task already waiting when training is queued is taken back; none are issued while it trains.
        var earlier=new TaskItem{TaskId="earlier-movie",Stage="motion"};queue.Enqueue(earlier);
        var inflight=Field<System.Collections.IDictionary>(f.Session,"_inflight");var type=inflight.GetType().GetGenericArguments()[1];
        inflight["earlier-movie"]=Activator.CreateInstance(type,waiting.Index,"motion",0UL,"",0L,0L,null,0,false)!;waiting.ActiveStage="motion";
        Invoke(f.Session,"Dispatch",queue,CancellationToken.None);
        Assert.Equal("train",Assert.Single(PendingTasks(layout)).Stage);Assert.Equal("",waiting.ActiveStage);
        Invoke(f.Session,"Dispatch",queue,CancellationToken.None);
        Assert.Single(PendingTasks(layout));
    }
    [Fact]
    public void FineTuningLeavesProcessingRunning()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{TrainCount=1});InstallModel(f.Session,f.Root,1);
        var sample=f.AddMovie();sample.TrainingPairReady=true;sample.DenoiseModel=1;
        var waiting=f.AddMovie();waiting.Completed.Clear();waiting.Done=0;waiting.EnhancedPixelSize=6;waiting.ProcessingPath=Path.Combine(f.Root,"waiting.eer");
        SetField(f.Session,"<Running>k__BackingField",true);f.Session.Train();
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        Assert.Equal(["motion","train"],PendingTasks(layout).Select(t=>t.Stage).Order());
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
        Assert.Equal("train",train.Stage);Assert.True(train.Priority);Assert.StartsWith(TaskQueue.PriorityPrefix,train.TaskId);Assert.True(train.Exclusive);
    }
    [Theory]
    [InlineData("pretrained",false,"pretrained")]
    [InlineData("scratch",false,"")]
    [InlineData("file",false,"start")]
    [InlineData("pretrained",true,"current")]
    [InlineData("file",true,"current")]
    public void TrainingFineTunesTheStartingModelUntilTheSessionHasItsOwn(string startingModel,bool sessionModel,string expected)
    {
        using var f=new Fixture();string start=Path.Combine(f.Root,"corpus.pt");File.WriteAllText(start,"weights");
        f.Session.Apply(f.Session.Settings with{TrainCount=1,PretrainedDenoiser=startingModel=="pretrained",DenoiserStart=startingModel=="file"?start:""});
        if(sessionModel)InstallModel(f.Session,f.Root,1);
        var movie=f.AddMovie();movie.TrainingPairReady=true;movie.DenoiseModel=sessionModel?1:0;
        SetField(f.Session,"<Running>k__BackingField",true);f.Session.Train();
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var train=Assert.Single(PendingTasks(layout),t=>t.Stage=="train");
        string previous=(string)train.Main.Single(c=>c.Name==WorkerCommandNames.TrainMovieDenoiser).Content[2];
        Assert.Equal(expected switch{"start"=>start,"current"=>Field<string>(f.Session,"_modelPath"),"pretrained"=>Noise2MicTorch.PretrainedModel,_=>""},previous);
    }
    [Fact]
    public void TurningObjectErasureOffRedoesDenoisingAndTrainsANewModel()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,2);var movie=f.AddMovie();movie.DenoiseModel=2;
        var next=f.Session.Settings with{EraseObjects=false};
        Assert.Equal("Redo denoising, picking, extraction. Train a new denoiser.",f.Session.GetSettingsImpact(next));
        f.Session.Apply(next);
        // The model learned from erased inputs; the next one is trained without erasure, as version 3.
        Assert.Equal(2,f.Session.Model);Assert.False(f.Session.HasModel);
        Assert.Equal(WarpStage.Enhance|WarpStage.Pick|WarpStage.Extract,movie.Stale);
        f.Manager.Remove(f.Identity);var restored=f.Manager.GetOrCreate(f.Identity);
        Assert.False(restored.Settings.EraseObjects);Assert.False(restored.HasModel);
        Assert.Equal(WarpStage.Enhance|WarpStage.Pick|WarpStage.Extract,restored.Movies[0].Stale);
    }
    [Fact]
    public void ChangingTheStartingModelTrainsFromItAndRedoesWhatTheOldModelDenoised()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{PretrainedDenoiser=false,TrainCount=1});InstallModel(f.Session,f.Root,2);
        var movie=f.AddMovie();movie.DenoiseModel=2;
        var next=f.Session.Settings with{PretrainedDenoiser=true};
        Assert.Equal("Train a new denoiser from the new starting model, then redo denoising, picking and extraction with it.",f.Session.GetSettingsImpact(next));
        f.Session.Apply(next);
        Assert.False(f.Session.HasModel);Assert.Equal(2,f.Session.Model);
        Assert.Equal(WarpStage.Enhance|WarpStage.Pick|WarpStage.Extract,movie.Stale);
        // The next model is version 3, so movie 0's denoising by version 2 stays outdated until it is redone.
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();var queue=new TaskQueue(layout);
        var trainer=f.AddMovie();trainer.TrainingPairReady=true;
        SetField(f.Session,"<Running>k__BackingField",true);f.Session.Train();
        Invoke(f.Session,"Dispatch",queue,CancellationToken.None);
        var train=Assert.Single(PendingTasks(layout),t=>t.Stage=="train");
        Assert.Equal(Noise2MicTorch.PretrainedModel,train.Main.Single(c=>c.Name==WorkerCommandNames.TrainMovieDenoiser).Content[2]);
        string model=Path.Combine(f.Root,"trained.pt");File.WriteAllText(model,"model");
        var inflight=Field<System.Collections.IDictionary>(f.Session,"_inflight");
        object pendingTrain=Activator.CreateInstance(inflight.GetType().GetGenericArguments()[1],-1,"train",ModelKeyOf(f.Session),model,0L,0L,null,0,false)!;
        inflight.Clear();inflight["train-task"]=pendingTrain;File.WriteAllText(Path.Combine(layout.Done,"train-task.json"),"{}");
        Invoke(f.Session,"CollectCompletions",layout,CancellationToken.None);Invoke(f.Session,"RefreshStates");
        Assert.Equal(3,f.Session.Model);Assert.True(f.Session.HasModel);
        Assert.Equal(WarpStage.Enhance|WarpStage.Pick|WarpStage.Extract,movie.Stale);
    }
    [Fact]
    public void ThePretrainedModelIsTheDefaultStart()
    {
        Assert.True(new WarpSettings().PretrainedDenoiser);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<WarpSettings>("{\"Box\":128,\"DenoiserStart\":\"\"}")!.PretrainedDenoiser);
    }
    [Fact]
    public void ObjectErasureStaysOnForSessionsSavedBeforeTheOption()
    {
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<WarpSettings>("{\"Box\":128}")!.EraseObjects);
        // A model trained on inputs prepared as they are now is kept after loading.
        using var f=new Fixture();InstallModel(f.Session,f.Root,2);f.Session.Apply(f.Session.Settings with{CtfMax=f.Session.Settings.CtfMax+1});
        f.Manager.Remove(f.Identity);Assert.Equal(2,f.Manager.GetOrCreate(f.Identity).Model);
    }
    [Fact]
    public void ModelsTrainedOnTheOldObjectErasureAreRetrainedAfterLoading()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,2);var movie=f.AddMovie();movie.DenoiseModel=2;
        // The key a model trained with the earlier detector's erasure recorded.
        SetField(f.Session,"_modelPreparationKey",WarpSession.Fingerprint(new{WarpSession.DeconvolutionStrength,WarpSession.DeconvolutionFalloff}));
        f.Session.Apply(f.Session.Settings with{CtfMax=f.Session.Settings.CtfMax+1});
        f.Manager.Remove(f.Identity);var restored=f.Manager.GetOrCreate(f.Identity);
        Assert.False(restored.HasModel);
    }
    [Fact]
    public void JunkIsSegmentedAfterAveragingThenErasedAndKeptAwayFromPicks()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{JunkDistance=50});InstallModel(f.Session,f.Root,1);
        var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var commands=Assert.Single(PendingTasks(layout)).Main;
        int Index(string name)=>Array.FindIndex(commands,c=>c.Name==name);
        // After the average is written, before CTF estimation (whose worker erases junk ahead of denoising).
        Assert.True(Index(WorkerCommandNames.MovieExportMovie)<Index(WorkerCommandNames.MovieSegmentJunk));
        Assert.True(Index(WorkerCommandNames.MovieSegmentJunk)<Index(WorkerCommandNames.MovieProcessCTF));
        Assert.Equal(movie.ProcessingPath,commands[Index(WorkerCommandNames.MovieSegmentJunk)].Content[0]);
        // The denoiser erases junk; picks keep 50 Å (10 pixels) from junk.
        Assert.Equal(true,commands[Index(WorkerCommandNames.MovieDenoise)].Content[5]);
        var pick=commands[Index(WorkerCommandNames.MicrographPickBlobs)];
        Assert.Equal(new Movie(movie.ProcessingPath).MaskPath,pick.Content[4]);Assert.Equal(10f,pick.Content[5]);
    }
    [Fact]
    public void WithoutJunkDetectionNothingIsSegmentedErasedOrKeptAway()
    {
        using var f=new Fixture();f.Session.Apply(f.Session.Settings with{DetectJunk=false});InstallModel(f.Session,f.Root,1);
        var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();
        Invoke(f.Session,"Dispatch",new TaskQueue(layout),CancellationToken.None);
        var commands=Assert.Single(PendingTasks(layout)).Main;
        Assert.DoesNotContain(commands,c=>c.Name==WorkerCommandNames.MovieSegmentJunk);
        Assert.Equal(false,commands.Single(c=>c.Name==WorkerCommandNames.MovieDenoise).Content[5]);
        Assert.Equal("",commands.Single(c=>c.Name==WorkerCommandNames.MicrographPickBlobs).Content[4]);
    }
    [Fact]
    public void JunkSettingsRedoOnlyWhatDependsOnThem()
    {
        using var f=new Fixture();InstallModel(f.Session,f.Root,1);
        Assert.Equal("Redo picking, extraction.",f.Session.GetSettingsImpact(f.Session.Settings with{JunkDistance=20}));
        // Turning detection off has nothing to redo in junk detection itself.
        Assert.Equal("Redo denoising, picking, extraction. Train a new denoiser.",f.Session.GetSettingsImpact(f.Session.Settings with{DetectJunk=false}));
        // Applying it dropped the model trained on erased junk, so turning detection back on has no model to replace.
        f.Session.Apply(f.Session.Settings with{DetectJunk=false});Assert.False(f.Session.HasModel);
        Assert.Equal("Redo junk detection, denoising, picking, extraction.",f.Session.GetSettingsImpact(f.Session.Settings with{DetectJunk=true}));
        Assert.True(new WarpSettings().DetectJunk);Assert.Equal(0,new WarpSettings().JunkDistance);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<WarpSettings>("{\"Box\":128}")!.DetectJunk);
        Assert.Contains("junk",WarpSession.ValidateSettings(new WarpSettings{InputPath="/data",JunkDistance=-1}));
        Assert.Null(WarpSession.ValidateSettings(new WarpSettings{InputPath="/data",DetectJunk=false,JunkDistance=-1}));
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WorkersAreToldWhetherToEraseJunk(bool erase)
    {
        using var f=new Fixture();string start=Path.Combine(f.Root,"corpus.pt");File.WriteAllText(start,"weights");
        f.Session.Apply(f.Session.Settings with{EraseObjects=erase,TrainCount=1,DenoiserStart=start});InstallModel(f.Session,f.Root,1);
        var layout=new QueueLayout(f.Identity.QueueDirectory);layout.EnsureDirectories();var queue=new TaskQueue(layout);
        var movie=f.AddMovie();movie.Completed.Clear();movie.Done=0;movie.EnhancedPixelSize=5;
        Invoke(f.Session,"Dispatch",queue,CancellationToken.None);
        var denoise=Assert.Single(PendingTasks(layout)).Main.Single(c=>c.Name==WorkerCommandNames.MovieDenoise);
        var trainer=f.AddMovie();trainer.TrainingPairReady=true;
        SetField(f.Session,"<Running>k__BackingField",true);f.Session.Train();
        Invoke(f.Session,"Dispatch",queue,CancellationToken.None);
        var train=Assert.Single(PendingTasks(layout),t=>t.Stage=="train").Main.Single(c=>c.Name==WorkerCommandNames.TrainMovieDenoiser);
        Assert.Equal(erase,denoise.Content[5]);Assert.Equal(erase,train.Content[7]);
    }
    [Fact]
    public async Task ProcessingRidesOutAQueueFolderThatCannotBeWritten()
    {
        // Over NFS the queue folders can fail to read for a moment ("Stale file handle"); a heartbeat folder that is a file
        // stands in for that. The run must keep going, and resume its heartbeat once the folder is back.
        using var f=new Fixture();using var cancel=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var layout=new QueueLayout(f.Identity.QueueDirectory);
        var run=f.Manager.RunAsync(f.Identity,f.Session.Settings,cancel.Token);
        while(!Directory.Exists(layout.Heartbeat)||!Directory.EnumerateFiles(layout.Heartbeat,"tick-*").Any())await Task.Delay(100);
        Directory.Delete(layout.Heartbeat,true);File.WriteAllText(layout.Heartbeat,"");
        await Task.Delay(3500);
        Assert.False(run.IsCompleted);Assert.True(f.Session.Running);Assert.Equal("",Field<string>(f.Session,"_runProblem"));
        File.Delete(layout.Heartbeat);Directory.CreateDirectory(layout.Heartbeat);
        while(!Directory.EnumerateFiles(layout.Heartbeat,"tick-*").Any())await Task.Delay(100);
        cancel.Cancel();await run;
        Assert.False(f.Session.Running);Assert.Equal("",Field<string>(f.Session,"_runProblem"));
    }
    [Fact]
    public void MissingStartingModelIsRejected()
    {
        using var f=new Fixture();
        var error=Assert.Throws<ArgumentException>(()=>f.Session.Apply(f.Session.Settings with{PretrainedDenoiser=false,DenoiserStart=Path.Combine(f.Root,"missing.pt")}));
        // With the pretrained model on, the file is not used, so it is not checked either.
        f.Session.Apply(f.Session.Settings with{DenoiserStart=Path.Combine(f.Root,"missing.pt")});
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
        object pendingTrain=Activator.CreateInstance(trainType,-1,"train",ModelKeyOf(f.Session),model,0L,0L,null,0,false)!;
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
