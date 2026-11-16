using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Refund.Services.Warp;
using Warp;
using Warp.Tools;
using Warp.Headers;
using Warp.Workers;

namespace Refund.Tests.Warp;

// Exercises the real session, scheduler, worker executable, transport and result readers.
// Only the child worker's explicit --mock command handlers replace GPU computation.
public sealed class MockWorkerEndToEndTests
{
    [Fact]
    public async Task RealDiskWorkerProcessesExportsRestoresAndSelectivelyReprocesses()
    {
        await using var fixture=new Fixture();
        fixture.WriteMovie("first/exposure.mrc");fixture.WriteMovie("second/exposure.mrc");
        await fixture.StartAsync();
        await fixture.UntilAsync(()=>fixture.Session.Movies.Count==2&&fixture.Session.Complete==2&&fixture.Session.GetParticleHistogram().Total==fixture.Session.Movies.Sum(m=>(long)m.Picks)&&fixture.Session.GetParticleHistogram().Total>0);
        var initial=fixture.Session.Movies.ToArray();
        Assert.Equal(2,initial.Select(m=>m.ProcessingPath).Distinct().Count());
        Assert.All(initial,m=>
        {
            Assert.Equal(WarpStage.All,m.Done);Assert.Equal(WarpStage.None,m.Stale);Assert.False(m.Failed,m.Error);
            Assert.True(File.Exists(m.MetadataPath));Assert.True(File.Exists(m.ThumbnailPath));Assert.True(File.Exists(WarpSession.PreviewPath(m.AveragePath)));Assert.True(File.Exists(WarpSession.PreviewPath(m.EnhancedPath)));Assert.True(File.Exists(new Movie(m.ProcessingPath).AverageOddPath));Assert.True(File.Exists(new Movie(m.ProcessingPath).AverageEvenPath));Assert.False(File.Exists(new Movie(m.ProcessingPath).DeconvolvedPath));
            var average=MapHeader.ReadFromFile(m.AveragePath);var enhanced=MapHeader.ReadFromFile(m.EnhancedPath);var stack=MapHeader.ReadFromFile(m.ParticleStackPath);
            Assert.Equal(102,average.Dimensions.X);Assert.Equal(102,average.Dimensions.Y);Assert.Equal(1,average.Dimensions.Z);
            Assert.Equal(22,enhanced.Dimensions.X);Assert.Equal(22,enhanced.Dimensions.Y);
            Assert.Equal((float)fixture.Session.Settings.TargetPixelSize,average.PixelSize.X,5);
            Assert.Equal(MovieDenoising.Sampling((float)fixture.Session.Settings.TargetPixelSize),enhanced.PixelSize.X,5);
            // The default bounds keep every pick; the extracted list is in the stack's order.
            Assert.Equal(m.Picks,m.Particles);Assert.Equal(m.Particles,stack.Dimensions.Z);Assert.Equal(m.Particles,new Star(m.ExtractedStarPath).RowCount);
            Assert.Equal(fixture.Session.Settings.Box,stack.Dimensions.X);
            Assert.Equal((float)fixture.Session.Settings.TargetPixelSize,stack.PixelSize.X,5);
            Assert.True(double.IsFinite(m.Motion)&&double.IsFinite(m.Ctf));
            var metadata=new Movie(m.ProcessingPath);Assert.NotEmpty(metadata.PS1D);Assert.NotEmpty(metadata.Simulated1D);
            // Workers keep the metadata's data folder as discovery wrote it: relative to the space, leading to the raw movie's link.
            Assert.False(Path.IsPathRooted(metadata.DataDirectoryName));Assert.True(File.Exists(Path.Combine(fixture.Root,metadata.DataPath)));
            using var spectrum=Image.FromFile(metadata.PowerSpectrumPath);Assert.True(spectrum.Dims.X>0);Assert.True(spectrum.GetHost(Intent.Read)[0].All(float.IsFinite));
        });
        Assert.True(fixture.Session.Model>0);Assert.All(initial,m=>Assert.Equal(fixture.Session.Model,m.DenoiseModel));Assert.Equal(0,fixture.Session.OlderModelMovies);
        var model=Assert.Single(Directory.GetFiles(Path.Combine(fixture.Identity.SessionDirectory,"models")));
        using(var marker=JsonDocument.Parse(File.ReadAllText(model)))
        {
            Assert.Equal("warpworker2-mock-denoiser",marker.RootElement.GetProperty("format").GetString());Assert.True(marker.RootElement.GetProperty("synthetic").GetBoolean());
            Assert.Equal(2,marker.RootElement.GetProperty("trainingMovies").GetArrayLength());Assert.Equal(3,marker.RootElement.GetProperty("epochs").GetInt32());
        }
        long total=initial.Sum(m=>(long)m.Picks);Assert.Equal(total,fixture.Session.GetParticleHistogram().Total);Assert.Equal(total,fixture.Session.ParticleCount);
        var export=await fixture.Session.ExportAsync(true,fixture.ExportDirectory("particles"),fixture.Root);
        Assert.Equal(total,export.ParticleCount);Assert.Equal(total,new Star(export.StarPath,"particles").RowCount);
        Assert.All(new Star(export.StarPath,"particles").GetColumn("rlnImageName"),value=>Assert.True(File.Exists(Path.Combine(fixture.Root,value[(value.IndexOf('@')+1)..]))));
        var micrographs=await fixture.Session.ExportAsync(false,fixture.ExportDirectory("micrographs"),fixture.Root);Assert.Equal(2,micrographs.MovieCount);
        Assert.Equal(2,JsonDocument.Parse(File.ReadAllText(micrographs.ProcessedItemsJson)).RootElement.GetArrayLength());
        await fixture.StopAsync();
        var averageWrites=initial.ToDictionary(m=>m.Id,m=>File.GetLastWriteTimeUtc(m.AveragePath));
        fixture.Manager.Remove(fixture.Identity);
        // A valid global checkpoint restores without opening any per-movie particle STAR.
        foreach(var row in initial)File.Move(row.ParticleStarPath,row.ParticleStarPath+".held");
        try { fixture.Reload();Assert.Equal(total,fixture.Session.GetParticleHistogram().Total);Assert.Equal(2,fixture.Session.Movies.Count); }
        finally { foreach(var row in initial)File.Move(row.ParticleStarPath+".held",row.ParticleStarPath); }

        // New score bounds and rescaling redo extraction alone: the picks, CTF and averages stay, and so does the histogram.
        fixture.Session.Apply(fixture.Session.Settings with{ParticleBounds=new(.45,.85,0,null),RescaleParticles=true,RescaledBox=32});
        Assert.All(fixture.Session.Movies,m=>Assert.Equal(WarpStage.Extract,m.Stale));Assert.Equal(total,fixture.Session.GetParticleHistogram().Total);
        long expected=initial.Sum(m=>ParticleScores(m.ParticleStarPath).LongCount(c=>c>=.45&&c<=.85));
        Assert.InRange(expected,1,total-1);
        await fixture.StartAsync();
        await fixture.UntilAsync(()=>fixture.Session.Complete==2&&fixture.Session.Movies.All(m=>m.ExtractionRevision>initial[m.Index].ExtractionRevision));
        Assert.All(fixture.Session.Movies,m=>
        {
            Assert.Equal(initial[m.Index].PickingRevision,m.PickingRevision);Assert.Equal(initial[m.Index].CtfRevision,m.CtfRevision);
            Assert.Equal(ParticleScores(m.ParticleStarPath).Count(c=>c>=.45&&c<=.85),m.Particles);
            Assert.All(ParticleScores(m.ExtractedStarPath),c=>Assert.InRange(c,.45,.85));
            if(m.Particles>0)
            {
                var stack=MapHeader.ReadFromFile(m.ParticleStackPath);
                Assert.Equal(m.Particles,stack.Dimensions.Z);Assert.Equal(32,stack.Dimensions.X);
                Assert.Equal((float)fixture.Session.Settings.TargetPixelSize*2,stack.PixelSize.X,4);
            }
            else Assert.False(File.Exists(m.ParticleStackPath));
        });
        Assert.Equal(expected,fixture.Session.ParticleCount);
        var bounded=await fixture.Session.ExportAsync(true,fixture.ExportDirectory("bounded"),fixture.Root);
        Assert.Equal(expected,bounded.ParticleCount);Assert.Equal(expected,new Star(bounded.StarPath,"particles").RowCount);
        // Rescaled particles are at twice the pixel size; their coordinates stay in micrograph pixels.
        var optics=new Star(bounded.StarPath,"optics");
        Assert.Equal(2.5m,decimal.Parse(Assert.Single(optics.GetColumn("rlnImagePixelSize")),CultureInfo.InvariantCulture));
        Assert.Equal(1.25m,decimal.Parse(Assert.Single(optics.GetColumn("rlnMicrographPixelSize")),CultureInfo.InvariantCulture));
        Assert.Equal("32",Assert.Single(optics.GetColumn("rlnImageSize")));
        // Particles name their movie in the processing folder, relative to the space.
        Assert.All(new Star(bounded.StarPath,"particles").GetColumn("rlnMicrographName"),value=>Assert.Contains(Path.Combine(fixture.Root,value),fixture.Session.Movies.Select(m=>m.ProcessingPath)));
        Assert.All(new Star(bounded.StarPath,"particles").GetColumn("rlnImageName"),value=>Assert.True(File.Exists(Path.Combine(fixture.Root,value[(value.IndexOf('@')+1)..]))));
        await fixture.StopAsync();
        initial=fixture.Session.Movies.ToArray();

        fixture.Session.Apply(fixture.Session.Settings with{CtfMax=3});
        Assert.All(fixture.Session.Movies,m=>{Assert.False(m.Stale.HasFlag(WarpStage.Motion));Assert.True(m.Stale.HasFlag(WarpStage.Ctf));});
        await fixture.StartAsync();
        await fixture.UntilAsync(()=>fixture.Session.Complete==2&&fixture.Session.Movies.All(m=>m.CtfRevision>initial[m.Index].CtfRevision));
        Assert.All(fixture.Session.Movies,m=>
        {
            Assert.Equal(initial[m.Index].MotionRevision,m.MotionRevision);Assert.Equal(averageWrites[m.Id],File.GetLastWriteTimeUtc(m.AveragePath));
            Assert.Equal(fixture.Session.Settings.TargetPixelSize*2/3m,new Movie(m.ProcessingPath).OptionsCTF.RangeMax);
        });
        Assert.Equal(total,new Star(export.StarPath,"particles").RowCount);
    }

    [Fact]
    public async Task FailedWorkerTaskIsIsolatedAndCanResumeAfterRetry()
    {
        await using var fixture=new Fixture();fixture.WriteMovie("good.mrc");fixture.WriteMovie("failure.mrc");
        await fixture.StartAsync(WorkerCommandNames.MicrographPickBlobs,"failure");
        await fixture.UntilAsync(()=>fixture.Session.Movies.Count==2&&fixture.Session.Complete==1&&fixture.Session.Failed==1&&fixture.Session.GetParticleHistogram().Total>0);
        var failed=Assert.Single(fixture.Session.Movies.Where(m=>m.Failed));
        Assert.Contains("mock",failed.Error,StringComparison.OrdinalIgnoreCase);
        Assert.True(failed.Done.HasFlag(WarpStage.Ctf));Assert.False(failed.Done.HasFlag(WarpStage.Pick));
        var good=Assert.Single(fixture.Session.Movies.Where(m=>!m.Failed));Assert.Equal((long)good.Picks,fixture.Session.GetParticleHistogram().Total);
        long motion=failed.MotionRevision,ctf=failed.CtfRevision;
        await fixture.StopAsync();fixture.Session.Retry([failed.Index]);await fixture.StartAsync();
        await fixture.UntilAsync(()=>fixture.Session.Complete==2&&fixture.Session.Failed==0);
        var repaired=fixture.Session.Movies[failed.Index];Assert.Equal(motion,repaired.MotionRevision);Assert.Equal(ctf,repaired.CtfRevision);
        Assert.True(repaired.Done.HasFlag(WarpStage.Extract));Assert.True(File.Exists(repaired.ParticleStackPath));
    }

    static double[] ParticleScores(string path)=>new Star(path).GetColumn("rlnAutopickFigureOfMerit").Select(s=>double.Parse(s,CultureInfo.InvariantCulture)).ToArray();

    sealed class Fixture:IAsyncDisposable
    {
        public string Root{get;}=Path.Combine(Path.GetTempPath(),"Warp session mock's "+Guid.NewGuid().ToString("N"));
        public WarpSessionManager Manager{get;}=new();
        public WarpSessionIdentity Identity{get;}
        public WarpSession Session{get;private set;}
        /// <summary>Where an export job named <paramref name="name"/> would write.</summary>
        public string ExportDirectory(string name)=>Path.Combine(Root,"exports",name);
        Process? _worker;Task? _run;CancellationTokenSource? _cancel;Task<string>? _stdout,_stderr;
        public Fixture()
        {
            string input=Path.Combine(Root,"input"),job=Path.Combine(Root,"session");Directory.CreateDirectory(input);
            Identity=new(1,1,1,job,Path.Combine(job,"tasks"),Root);Session=Manager.GetOrCreate(Identity);
            Session.Apply(new(){InputPath=input,InputPattern="*.mrc",Recursive=true,Pixel=1,TargetPixelSize=1.25m,TrainCount=2,TrainingEpochs=3,Diameter=20,Box=64});
            Session.FilterMetric("ctf-max",100);Session.FilterMetric("motion-max",100);
        }
        public void WriteMovie(string relative)
        {
            string path=Path.Combine(Root,"input",relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var slices=Enumerable.Range(0,4).Select(z=>Enumerable.Range(0,128*128).Select(i=>(float)(Math.Sin(i*.09+z)+Math.Cos(i*.017))).ToArray()).ToArray();
            using var image=new Image(slices,new int3(128,128,4));image.WriteMRC(path,1,true);
        }
        public Task StartAsync(string? failCommand=null,string? failPath=null)
        {
            // Mirrors the local job's Stage: only terminal attempts have their task directory cleared.
            if(Directory.Exists(Identity.QueueDirectory))Directory.Delete(Identity.QueueDirectory,true);
            _cancel=new();_run=Manager.RunAsync(Identity,Session.Settings,_cancel.Token);
            string worker=typeof(MockWorkerEndToEndTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a=>a.Key=="WarpWorkerAssemblyPath").Value!;
            Assert.True(File.Exists(worker),"The build-only WarpWorker2 project dependency must produce "+worker);
            var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string arg in new[]{worker,"--device","0","--queue-dir",Identity.QueueDirectory,"--persistent","--mock","--mock-delay-ms","0"})start.ArgumentList.Add(arg);
            if(failCommand!=null){start.ArgumentList.Add("--mock-fail-command");start.ArgumentList.Add(failCommand);start.ArgumentList.Add("--mock-fail-path");start.ArgumentList.Add(failPath!);}
            _worker=Process.Start(start)!;_stdout=_worker.StandardOutput.ReadToEndAsync();_stderr=_worker.StandardError.ReadToEndAsync();return Task.CompletedTask;
        }
        public async Task UntilAsync(Func<bool> condition)
        {
            var elapsed=Stopwatch.StartNew();
            while(!condition()&&elapsed.Elapsed<TimeSpan.FromSeconds(55))
            {
                if(_run!.IsFaulted)await _run;
                if(_worker!.HasExited)throw new InvalidOperationException($"Mock worker exited {_worker.ExitCode}: {await _stdout!} {await _stderr!}");
                await Task.Delay(100);
            }
            Assert.True(condition(),$"Timed out: {Session.Problem}; ready={Session.Complete} failed={Session.Failed}; "+string.Join(" | ",Session.Movies.Select(m=>$"{m.Name}:{m.Done}/{m.Stale}/{m.ActiveStage}/{m.Error}")));
        }
        public void Reload()=>Session=Manager.GetOrCreate(Identity);
        public async Task StopAsync()
        {
            _cancel?.Cancel();
            try{if(_run!=null)await _run.WaitAsync(TimeSpan.FromSeconds(10));}
            finally
            {
                if(_worker!=null){if(!_worker.HasExited)_worker.Kill(true);await _worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));_worker.Dispose();_worker=null;}
                _cancel?.Dispose();_cancel=null;_run=null;
            }
        }
        public async ValueTask DisposeAsync(){try{await StopAsync();}finally{Manager.Dispose();if(Directory.Exists(Root))Directory.Delete(Root,true);}}
    }
}
