using System.Text.RegularExpressions;
using Refund.DataModel;
using Refund.JobQueues;
using Refine3DJob = Refund.Jobs.Refinement.Refinement3D.Refine3D.Refine3D;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class Refine3DPoolTests
{
    private static Refine3DJob NewJob() =>
        new() { Space = new Space { RootDirectory = "/tmp/relay-test" } };

    // A CPU-worker pooled job (explicit, so it doesn't depend on the UseGpuWorkers default).
    private static Refine3DJob NewPooledJob()
    {
        var job = NewJob();
        job.UseWorkerPool = true;
        job.UseGpuWorkers = false;
        job.PoolQueueId = 1;
        job.CoresPerWorker = 8;
        job.MemoryPerWorker = 12;
        return job;
    }

    private static Refine3DJob NewGpuPooledJob()
    {
        var job = NewPooledJob();
        job.UseGpuWorkers = true;
        return job;
    }

    [Fact]
    public void PoolFields_HaveExpectedDefaults()
    {
        var job = new Refine3DJob();
        Assert.False(job.UseWorkerPool);
        Assert.Equal(-1, job.PoolQueueId);
        Assert.Equal(2, job.CoresPerWorker);
        Assert.Equal(4, job.NWorkers);
        Assert.Equal(128, job.ParticlesPerTask);
        Assert.True(job.UseGpuWorkers);
        Assert.Equal(0, job.PoolWorkersAlive);
    }

    [Fact]
    public void CommandName_SwitchesToRelionRefinePoolWhenPooled()
    {
        Assert.Equal("relion_refine_pool", new Refine3DJob { UseWorkerPool = true }.CommandName);
        Assert.Equal("mpirun -n 5 relion_refine_mpi",
            new Refine3DJob { UseWorkerPool = false, NProcesses = 5 }.CommandName);
    }

    [Fact]
    public void RequiredModules_PooledIsCpuPlusRelionPool()
    {
        var pooled = new Refine3DJob { UseWorkerPool = true }.RequiredModules;
        Assert.Contains("cpu", pooled);
        Assert.Contains("relion-pool", pooled);
        Assert.DoesNotContain("relion", pooled);
        Assert.DoesNotContain("gpu", pooled);
    }

    [Fact]
    public void PooledResourcesAreCpuOnly()
    {
        var job = NewPooledJob();
        Assert.Equal(0, job.GpuCount);
        Assert.Equal(JobQueueType.CPU, job.QueueType);
        Assert.Equal(16, job.CoreCount);           // fixed manager budget
        Assert.Equal(1, job.ProcessCount);
        Assert.Equal(12, job.MemoryGb);            // manager = one MemoryPerWorker
    }

    [Fact]
    public void ApplyPoolArguments_SetsManagerThreadsPoolDirBatch_AndStrips()
    {
        var job = NewJob();
        job.CoresPerWorker = 8;
        job.ParticlesPerTask = 256;
        var args = new Dictionary<string, string> { ["gpu"] = "", ["scratch_dir"] = "/fast", ["j"] = "2" };

        job.ApplyPoolArguments(args);

        Assert.Equal("16", args["j"]);
        Assert.Equal("256", args["pool_batch"]);
        Assert.True(args.ContainsKey("pool_dir"));
        Assert.False(args.ContainsKey("gpu"));
        Assert.False(args.ContainsKey("scratch_dir"));
    }

    [Fact]
    public void Refine3D_ImplementsIPooledJob() => Assert.IsAssignableFrom<IPooledJob>(new Refine3DJob());

    [Fact]
    public void PoolQueueId_ActiveOnlyWhenUseWorkerPool()
    {
        Assert.Equal(-1, ((IPooledJob)new Refine3DJob { UseWorkerPool = false, PoolQueueId = 5 }).PoolQueueId);
        Assert.Equal(5, ((IPooledJob)new Refine3DJob { UseWorkerPool = true, PoolQueueId = 5 }).PoolQueueId);
    }

    [Fact]
    public void PoolSize_EqualsNWorkers() =>
        Assert.Equal(6, ((IPooledJob)new Refine3DJob { NWorkers = 6 }).PoolSize);

    [Fact]
    public void WorkerResourceValues_AccountForBothHalves_Cpu()
    {
        var w = ((IPooledJob)NewPooledJob()).GetWorkerResourceValues("/tmp/worker-logs");
        Assert.Equal("0", w["n_gpus"]);
        Assert.Equal("16", w["n_cores"]);      // 2 halves * CoresPerWorker(8)
        Assert.Equal("24", w["memory_gb"]);    // 2 halves * MemoryPerWorker(12)
        Assert.Equal("1", w["n_processes"]);
    }

    [Fact]
    public void WorkerResourceValues_RequestOneGpu_WhenGpuWorkers()
    {
        var w = ((IPooledJob)NewGpuPooledJob()).GetWorkerResourceValues("/tmp/worker-logs");
        Assert.Equal("1", w["n_gpus"]);        // one GPU shared by both halves
        Assert.Equal("16", w["n_cores"]);
    }

    [Fact]
    public void WorkerRequiredModules_MatchWorkerHardware()
    {
        Assert.Equal(new[] { "cpu", "relion-pool" }, ((IPooledJob)NewPooledJob()).WorkerRequiredModules);
        Assert.Equal(new[] { "gpu", "relion-pool" }, ((IPooledJob)NewGpuPooledJob()).WorkerRequiredModules);
    }

    [Fact]
    public void CpuWorkerCommand_RunsBothHalves_NoGpu()
    {
        var cmd = NewPooledJob().ComposeWorkerCommand(new Dictionary<string, string> { ["o"] = "run", ["j"] = "16" });

        Assert.StartsWith("cd ", cmd);
        Assert.Contains("--worker --half 1", cmd);
        Assert.Contains("--worker --half 2", cmd);
        Assert.Equal(2, Regex.Matches(cmd, "relion_refine_pool ").Count);
        Assert.Equal(2, Regex.Matches(cmd, " &").Count);
        Assert.Contains("\nwait", cmd);
        Assert.DoesNotContain("--gpu", cmd);
        Assert.Contains("--j 8", cmd);          // worker threads (not the manager's 16)
        Assert.DoesNotContain("--j 16", cmd);
    }

    [Fact]
    public void GpuWorkerCommand_RunsBothHalvesSharingOneGpu()
    {
        var cmd = NewGpuPooledJob().ComposeWorkerCommand(new Dictionary<string, string> { ["o"] = "run" });

        Assert.Equal(2, Regex.Matches(cmd, "relion_refine_pool ").Count);
        Assert.Contains("--gpu \"\" --gpu_shares 2 --worker --half 1", cmd);
        Assert.Contains("--gpu \"\" --gpu_shares 2 --worker --half 2", cmd);
        Assert.Contains("\nwait", cmd);
    }

    [Fact]
    public void PoolStatus_MirroredOntoReadOnlyWrapper()
    {
        if (Job.Types.Count == 0)
            Job.PopulateStatic();

        var job = NewPooledJob();
        job.PoolWorkersAlive = 4;
        Assert.IsAssignableFrom<IPoolStatus>(job);
        var ro = Assert.IsAssignableFrom<IPoolStatus>(job.AsReadOnly());
        Assert.True(ro.IsPooled);
        Assert.Equal(4, ro.PoolWorkersAlive);
    }
}
