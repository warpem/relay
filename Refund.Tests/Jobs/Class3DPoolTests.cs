using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobQueues;
using Class3DJob = Refund.Jobs.Refinement.Classes3D.Class3D.Class3D;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class Class3DPoolTests
{
    private static Class3DJob NewJob() =>
        new() { Space = new Space { RootDirectory = "/tmp/relay-test" } };

    private static Class3DJob NewPooledJob()
    {
        var job = NewJob();
        job.UseWorkerPool = true;
        job.PoolQueueId = 1;
        job.CoresPerWorker = 8;
        job.MemoryPerWorker = 12;
        return job;
    }

    private static Class3DJob NewGpuPooledJob()
    {
        var job = NewPooledJob();
        job.UseGpuWorkers = true;
        job.ProcessesPerGpu = 2;
        return job;
    }

    [Fact]
    public void PoolFields_HaveExpectedDefaults()
    {
        var job = new Class3DJob();
        Assert.False(job.UseWorkerPool);
        Assert.Equal(-1, job.PoolQueueId);
        Assert.Equal(8, job.CoresPerWorker);
        Assert.Equal(4, job.NWorkers);
        Assert.Equal(128, job.ParticlesPerTask);
        Assert.False(job.UseGpuWorkers);
        Assert.Equal(2, job.ProcessesPerGpu);
        Assert.Equal(0, job.PoolWorkersAlive);
    }

    [Fact]
    public void PoolFields_RoundTripJson()
    {
        var job = new Class3DJob { UseWorkerPool = true, PoolQueueId = 3, CoresPerWorker = 16, NWorkers = 10 };
        var node = new JsonObject();
        job.WriteToJson(node);

        var job2 = new Class3DJob();
        job2.ReadFromJson(node);

        Assert.True(job2.UseWorkerPool);
        Assert.Equal(3, job2.PoolQueueId);
        Assert.Equal(16, job2.CoresPerWorker);
        Assert.Equal(10, job2.NWorkers);
    }

    [Fact]
    public void CommandName_SwitchesToRelionRefinePoolWhenPooled()
    {
        Assert.Equal("relion_refine_pool", new Class3DJob { UseWorkerPool = true }.CommandName);
        Assert.Equal("relion_refine", new Class3DJob { UseWorkerPool = false, NProcesses = 1 }.CommandName);
        Assert.Equal("mpirun -n 4 relion_refine_mpi",
            new Class3DJob { UseWorkerPool = false, NProcesses = 4 }.CommandName);
    }

    [Fact]
    public void RequiredModules_PooledIsCpuPlusRelionPool()
    {
        var pooled = new Class3DJob { UseWorkerPool = true }.RequiredModules;
        Assert.Contains("cpu", pooled);            // CPU partition directives ({{cpu}} block)
        Assert.Contains("relion-pool", pooled);    // pool software module
        Assert.DoesNotContain("relion", pooled);   // replaced by relion-pool
        Assert.DoesNotContain("gpu", pooled);

        var gpu = new Class3DJob { UseWorkerPool = false, UseGpu = true }.RequiredModules;
        Assert.Contains("relion", gpu);
        Assert.Contains("gpu", gpu);
        Assert.DoesNotContain("relion-pool", gpu);
    }

    [Fact]
    public void SupportedModules_IncludesRelionPool()
    {
        Assert.Contains("relion-pool", new Class3DJob().SupportedModules);
    }

    [Fact]
    public void PooledResourcesAreCpuOnly()
    {
        var job = NewPooledJob();
        Assert.Equal(0, job.GpuCount);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
        Assert.Equal(16, job.CoreCount);           // fixed manager budget, decoupled from CoresPerWorker
        Assert.Equal(1, job.ProcessCount);
        Assert.Equal(12, job.MemoryGb);
    }

    [Fact]
    public void ApplyPoolArguments_SetsThreadsAndPoolDir_AndRemovesGpuAndScratch()
    {
        var job = NewJob();
        job.CoresPerWorker = 8;
        job.ParticlesPerTask = 256;
        var args = new Dictionary<string, string> { ["gpu"] = "", ["scratch_dir"] = "/fast", ["j"] = "2" };

        job.ApplyPoolArguments(args);

        Assert.Equal("16", args["j"]);             // manager thread count (fixed), not CoresPerWorker
        Assert.Equal("256", args["pool_batch"]);
        Assert.True(args.ContainsKey("pool_dir"));
        Assert.False(args.ContainsKey("gpu"));
        Assert.False(args.ContainsKey("scratch_dir"));
    }

    [Fact]
    public void Class3D_ImplementsIPooledJob()
    {
        Assert.IsAssignableFrom<IPooledJob>(new Class3DJob());
    }

    [Fact]
    public void PoolQueueId_ActiveOnlyWhenUseWorkerPool()
    {
        Assert.Equal(-1, ((IPooledJob)new Class3DJob { UseWorkerPool = false, PoolQueueId = 5 }).PoolQueueId);
        Assert.Equal(5, ((IPooledJob)new Class3DJob { UseWorkerPool = true, PoolQueueId = 5 }).PoolQueueId);
    }

    [Fact]
    public void PoolSize_EqualsNWorkers()
    {
        Assert.Equal(6, ((IPooledJob)new Class3DJob { NWorkers = 6 }).PoolSize);
    }

    [Fact]
    public void GetWorkerResourceValues_AreCpuOnly()
    {
        var w = ((IPooledJob)NewPooledJob()).GetWorkerResourceValues("/tmp/worker-logs");
        Assert.Equal("0", w["n_gpus"]);
        Assert.Equal("8", w["n_cores"]);
        Assert.Equal("12", w["memory_gb"]);
        Assert.Equal("1", w["n_processes"]);
        Assert.Contains("%j", w["std_out"]);
    }

    [Fact]
    public void WorkerRequiredModules_IsCpuPlusRelionPool()
    {
        Assert.Equal(new[] { "cpu", "relion-pool" }, ((IPooledJob)new Class3DJob()).WorkerRequiredModules);
    }

    [Fact]
    public void IsGpuPool_RequiresBothTogglesOn()
    {
        Assert.False(new Class3DJob { UseWorkerPool = true, UseGpuWorkers = false }.IsGpuPool);
        Assert.False(new Class3DJob { UseWorkerPool = false, UseGpuWorkers = true }.IsGpuPool);
        Assert.True(new Class3DJob { UseWorkerPool = true, UseGpuWorkers = true }.IsGpuPool);
    }

    [Fact]
    public void GpuWorkers_ManagerStaysCpuOnly()
    {
        // The manager orchestrates on CPU even when workers are GPU (RELION's GPU-pool design).
        var job = NewGpuPooledJob();
        Assert.Equal(0, job.GpuCount);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
        Assert.Equal(new[] { "cpu", "relion-pool" }, job.RequiredModules);
        Assert.Equal("relion_refine_pool", job.CommandName);   // no mpirun, no --gpu on the manager
    }

    [Fact]
    public void GpuWorkerResourceValues_RequestOneGpuAndScaledCpu()
    {
        var job = NewGpuPooledJob();   // CoresPerWorker=8, MemoryPerWorker=12, ProcessesPerGpu=2
        var w = ((IPooledJob)job).GetWorkerResourceValues("/tmp/worker-logs");

        Assert.Equal("1", w["n_gpus"]);
        Assert.Equal("16", w["n_cores"]);     // ProcessesPerGpu * CoresPerWorker
        Assert.Equal("24", w["memory_gb"]);   // ProcessesPerGpu * MemoryPerWorker
        Assert.Equal("1", w["n_processes"]);
    }

    [Fact]
    public void GpuWorkerRequiredModules_IsGpuPlusRelionPool()
    {
        Assert.Equal(new[] { "gpu", "relion-pool" },
            ((IPooledJob)NewGpuPooledJob()).WorkerRequiredModules);
    }

    [Fact]
    public void GpuWorkerCommand_LaunchesProcessesPerGpuOnOneGpu()
    {
        var job = NewGpuPooledJob();
        job.ProcessesPerGpu = 2;
        var cmd = job.ComposeWorkerCommand(new Dictionary<string, string> { ["o"] = "run", ["j"] = "8" });

        Assert.StartsWith("cd ", cmd);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(cmd, "relion_refine_pool ").Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(cmd, " &").Count);
        Assert.Contains("\nwait", cmd);
        // Explicit empty device list (bare --gpu would eat the next token) + gpu_shares = K workers/GPU.
        Assert.Contains("--gpu \"\" --gpu_shares 2 --worker --half 0", cmd);
    }

    [Fact]
    public void ComposeWorkerCommand_WrapsArgsWithRoleFlags()
    {
        var cmd = NewJob().ComposeWorkerCommand(new Dictionary<string, string>
        {
            ["o"] = "run", ["pool_dir"] = "pool", ["j"] = "8",
        });

        Assert.StartsWith("cd ", cmd);
        Assert.Contains("relion_refine_pool", cmd);
        Assert.Contains("--worker", cmd);
        Assert.Contains("--half 0", cmd);
        Assert.Contains("--pool_dir pool", cmd);
        Assert.Contains("--j 8", cmd);
    }

    [Fact]
    public void WorkerThreads_DecoupledFromManagerThreads()
    {
        // The manager gets a fixed 16-thread budget for its multithreaded CPU steps; each worker's
        // E-step uses CoresPerWorker threads. The worker command must override --j back down even
        // though it starts from the manager's argument set.
        var job = NewPooledJob();   // CoresPerWorker = 8
        var cmd = job.ComposeWorkerCommand(new Dictionary<string, string> { ["j"] = "16" });
        Assert.Contains("--j 8", cmd);
        Assert.DoesNotContain("--j 16", cmd);
    }

    [Fact]
    public void IPooledJob_ExposesPoolWorkerCounters()
    {
        var job = new Class3DJob();
        var pooled = (IPooledJob)job;
        pooled.PoolWorkersAlive = 3;
        pooled.PoolWorkersRunning = 2;
        pooled.PoolWorkersSubmitted = 5;
        Assert.Equal(3, job.PoolWorkersAlive);
        Assert.Equal(2, job.PoolWorkersRunning);
        Assert.Equal(5, job.PoolWorkersSubmitted);
    }

    [Fact]
    public void PoolStatus_MirroredOntoReadOnlyWrapper_ForCard()
    {
        // The queue job card gates pool display on IPoolStatus (a pure-read contract), so the ReadOnly
        // source generator must replicate it onto ReadOnlyClass3D — otherwise the card shows nothing
        // for a pooled RELION job even though the fleet is running.
        if (Job.Types.Count == 0)
            Job.PopulateStatic();   // AsReadOnly() resolves the wrapper via the type registry

        var job = NewPooledJob();
        job.PoolWorkersAlive = 5;
        job.PoolWorkersRunning = 3;

        Assert.IsAssignableFrom<IPoolStatus>(job);                 // mutable side
        var ro = Assert.IsAssignableFrom<IPoolStatus>(job.AsReadOnly());   // generated read-only side
        Assert.True(ro.IsPooled);
        Assert.Equal(5, ro.PoolWorkersAlive);
        Assert.Equal(3, ro.PoolWorkersRunning);
    }

    [Fact]
    public void JobModules_Registry_IncludesRelionPool()
    {
        if (Job.Types.Count == 0)
            Job.PopulateStatic();
        Assert.Contains("relion-pool", Job.Modules);
    }
}
