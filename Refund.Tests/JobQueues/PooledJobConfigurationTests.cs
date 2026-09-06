using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobQueues;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;
using EtomoJob = Refund.Jobs.Ts.Alignment.AlignEtomo.AlignEtomo;
using RefineJob = Refund.Jobs.M.Refine.Refine;
using MissAlignmentJob = Refund.Jobs.Ts.Alignment.AlignMiss.AlignMiss;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;

namespace Refund.Tests.JobQueues;

[Collection("JobRegistry")]
public class PooledJobConfigurationTests
{
    private static void EnsurePopulated() => JobRegistry.EnsurePopulated();

    // MotionAndCTF2D.ComposeCommandArguments resolves paths against the Space,
    // so it needs a Space with a non-empty RootDirectory to run.
    private static MotionAndCTF2D MakeJobWithSpace()
    {
        EnsurePopulated();
        return new MotionAndCTF2D { Space = new Space { RootDirectory = "/tmp/relay-test" } };
    }

    [Fact]
    public void WarpJobGpu_ImplementsIPooledJob()
    {
        var job = new MotionAndCTF2D();
        Assert.IsAssignableFrom<IPooledJob>(job);
    }

    [Fact]
    public void MissAlignment_IsNotPooled_ButStillGpu()
    {
        // MissAlignment runs a single GPU command outside the WarpTools per-item worker-pool model,
        // so it must NOT inherit pool support (which lives on WarpJobGpu) — while remaining a GPU job.
        var job = new MissAlignmentJob();
        Assert.IsNotAssignableFrom<IPooledJob>(job);
        Assert.Equal(JobQueueType.GPU, job.QueueType);
    }

    [Fact]
    public void MissAlignment_ComposeCommandArguments_MatchesRealCli()
    {
        EnsurePopulated();

        var job = new MissAlignmentJob
        {
            Space = new Space { RootDirectory = "/tmp/relay-test" },
            NGpus = 4,
            PerDevice = 3,
            NWorkers = 4,
        };

        var args = job.ComposeCommandArguments();

        // Runs the `train` subcommand and emits miss-alignment's real device-list options. Training
        // runs only on device 0; every other GPU is dedicated to reconstruction (PerDevice workers
        // each).
        Assert.Equal("miss-alignment train", job.CommandName);
        Assert.Equal("0", args["training-devices"]);
        Assert.Equal("1,1,1,2,2,2,3,3,3", args["reconstruction-devices"]);   // GPUs 1-3, PerDevice workers each
        Assert.Equal("4", args["dataloaders-per-trainer"]);
        Assert.True(args.ContainsKey("config-file"));
        Assert.True(args.ContainsKey("prepare-stacks"));

        // Flags miss-alignment does not accept must not be emitted (Typer would error on them).
        Assert.False(args.ContainsKey("perdevice"));
        Assert.False(args.ContainsKey("n-workers"));
        Assert.False(args.ContainsKey("delete_intermediate"));
        Assert.False(args.ContainsKey("strict"));
    }

    [Fact]
    public void MissAlignment_WorkerPool_EmitsNClusterWorkers_OnlyWhenEnabled()
    {
        EnsurePopulated();

        // Off: no --n-cluster-workers, so MissAlignment runs without spawning its cluster pool.
        var off = new MissAlignmentJob
        {
            Space = new Space { RootDirectory = "/tmp/relay-test" },
            UseWorkerPool = false,
            PoolSize = 6,
        };
        Assert.False(off.ComposeCommandArguments().ContainsKey("n-cluster-workers"));

        // On: --n-cluster-workers <PoolSize> activates the tool's internal cluster worker pool.
        var on = new MissAlignmentJob
        {
            Space = new Space { RootDirectory = "/tmp/relay-test" },
            UseWorkerPool = true,
            PoolSize = 6,
        };
        Assert.Equal("6", on.ComposeCommandArguments()["n-cluster-workers"]);
    }

    [Theory]
    // 1 GPU: training and reconstruction share device 0.
    [InlineData(1, 5, "0", "0,0,0,0,0")]
    // 2 GPUs: device 0 trains, device 1 reconstructs (the fast config).
    [InlineData(2, 5, "0", "1,1,1,1,1")]
    // 4 GPUs: device 0 trains, all remaining GPUs reconstruct (PerDevice workers each).
    [InlineData(4, 2, "0", "1,1,2,2,3,3")]
    public void MissAlignment_DeviceSplit_SeparatesTrainingAndReconstruction(
        int nGpus, int perDevice, string expectedTraining, string expectedReconstruction)
    {
        EnsurePopulated();

        var job = new MissAlignmentJob
        {
            Space = new Space { RootDirectory = "/tmp/relay-test" },
            NGpus = nGpus,
            PerDevice = perDevice,
        };

        var args = job.ComposeCommandArguments();

        Assert.Equal(expectedTraining, args["training-devices"]);
        Assert.Equal(expectedReconstruction, args["reconstruction-devices"]);
    }

    [Fact]
    public void ItemProgress_ReadOnlyWrapper_ImplementsInterface_ForWarpAndMissAlignmentJobs()
    {
        EnsurePopulated();

        // Both a WarpTools job (WarpJob-derived) and MissAlignment (standalone Job) report item
        // counts, so their generated read-only wrappers must expose IItemProgress. This is what lets
        // the job card gate the item-count display on the capability + non-null counts, not on a
        // concrete job type. Also proves the ReadOnly source generator replicated the interface.
        foreach (Job job in new Job[] { new MotionAndCTF2D(), new MissAlignmentJob() })
        {
            Assert.IsAssignableFrom<IItemProgress>(job);                 // mutable side
            Assert.IsAssignableFrom<IItemProgress>(job.AsReadOnly());    // generated read-only side
            Assert.Null(((IItemProgress)job).NItemsTotal);              // nullable, defaults to null
        }
    }

    [Fact]
    public void ItemProgress_ReadOnlyWrapper_NotImplemented_ForNonItemJob()
    {
        EnsurePopulated();

        // A job that does not report item counts (RELION mask creation) must not gain IItemProgress,
        // so the card shows nothing for it. Guards the generator's read-contract heuristic against
        // over-replicating interfaces.
        var job = new MaskJob();
        Assert.IsNotAssignableFrom<IItemProgress>(job);
        Assert.IsNotAssignableFrom<IItemProgress>(job.AsReadOnly());
    }

    [Fact]
    public void WarpJobGpu_PoolQueueId_DefaultsToMinusOne()
    {
        var job = new MotionAndCTF2D();
        Assert.Equal(-1, ((IPooledJob)job).PoolQueueId);
    }

    [Fact]
    public void WarpJobGpu_PoolSubmissionCap_IsTwicePoolSize()
    {
        var job = new MotionAndCTF2D();
        var pooled = (IPooledJob)job;
        Assert.Equal(pooled.PoolSize * 100, pooled.PoolSubmissionCap);
    }

    [Fact]
    public void WarpJobGpu_PoolSize_IsPositive()
    {
        var job = new MotionAndCTF2D();
        Assert.True(((IPooledJob)job).PoolSize > 0);
    }

    [Fact]
    public void WarpJobGpu_PoolFields_RoundTripJson()
    {
        EnsurePopulated();

        // Pool config is [RelayProperty] ints handled by RelayBase.WriteToJson /
        // ReadFromJson via reflection; a bare instance round-trips them fine. Pool size
        // is derived from NGpus (one worker per GPU), so NGpus is the persisted source.
        var job = new MotionAndCTF2D { UseWorkerPool = true, PoolQueueId = 3, NGpus = 16 };
        var node = new JsonObject();
        job.WriteToJson(node);

        // RelayBase.ReadFromJson(JsonNode) deserializes the [RelayProperty] fields.
        // (Job adds an extra ReadFromJson(JsonNode, users) overload for UpdatedBy/Events,
        // but the pool fields live on the base reflection path.)
        var job2 = new MotionAndCTF2D();
        job2.ReadFromJson(node);

        Assert.True(job2.UseWorkerPool);
        Assert.Equal(3, job2.PoolQueueId);
        Assert.Equal(16, job2.NGpus);
        Assert.Equal(16, ((IPooledJob)job2).PoolSize);   // derived from NGpus
    }

    [Fact]
    public void WarpJobGpu_PoolWorkersAlive_DefaultsToZero()
    {
        var job = new MotionAndCTF2D();
        Assert.Equal(0, job.PoolWorkersAlive);
    }

    [Fact]
    public void WarpJobGpu_GetWorkerCommand_FormatsCommandWithDeviceIndex()
    {
        // GetWorkerCommand cd's to RunDirectory (Space.RootDirectory), so a Space is required.
        var job = MakeJobWithSpace();
        var cmd = ((IPooledJob)job).GetWorkerCommand(2);
        Assert.Contains("WarpWorker2", cmd);
        Assert.Contains("--queue-dir ", cmd);   // WarpWorker2's flag (NOT the Manager's --task_dir)
        Assert.Contains("--device 2", cmd);
        Assert.Contains("--log-dir ", cmd);
        Assert.Contains("--persistent", cmd);   // keep polling instead of exiting when the queue drains
        Assert.Contains("cd ", cmd);            // runs from the job's working directory, like the Manager
    }

    [Fact]
    public void WarpJobGpu_GetWorkerCommand_LaunchesPerDeviceProcesses()
    {
        var job = MakeJobWithSpace();
        job.PerDevice = 3;                       // 3 worker processes per GPU

        var cmd = ((IPooledJob)job).GetWorkerCommand(0);

        // One WarpWorker2 invocation per worker process, each backgrounded, then a single wait.
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cmd, "WarpWorker2 ").Count);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(cmd, " &").Count);
        Assert.Contains("\nwait", cmd);

        // Each process gets a distinct, globally-unique worker id (…-<device>-<index>).
        Assert.Contains("-0-0\"", cmd);
        Assert.Contains("-0-1\"", cmd);
        Assert.Contains("-0-2\"", cmd);
    }

    [Fact]
    public void WarpJobGpu_GetWorkerResourceValues_ScalesCoresAndMemoryWithPerDevice()
    {
        var job = MakeJobWithSpace();
        job.PerDevice = 4;

        var worker = ((IPooledJob)job).GetWorkerResourceValues("/tmp/worker-logs");

        Assert.Equal((4 * 2).ToString(), worker["n_cores"]);                  // ~2 cores per process
        Assert.Equal((4 * job.MemoryPerWorker).ToString(), worker["memory_gb"]);
        Assert.Equal("1", worker["n_gpus"]);                                  // still one GPU
    }

    [Fact]
    public void WarpJobGpu_GetWorkerResourceValues_CoversManagerKeysWithWorkerOverrides()
    {
        var job = MakeJobWithSpace();

        // Anti-drift: the worker must carry every variable the Manager's template expects, so it
        // can't silently miss one (which would leave an empty #SBATCH directive).
        var managerKeys = job.GetResourceValues().Keys;
        var worker = ((IPooledJob)job).GetWorkerResourceValues("/tmp/worker-logs");

        foreach (var key in managerKeys)
            Assert.Contains(key, worker.Keys);

        // ...plus job_id, with the worker-specific overrides applied.
        Assert.Contains("worker", worker["job_id"]);
        Assert.Equal("1", worker["n_gpus"]);
        Assert.Equal("1", worker["n_processes"]);
        Assert.Contains("%j", worker["std_out"]);
        Assert.Contains("%j", worker["std_err"]);
    }

    [Fact]
    public void WarpJobGpu_ComposeCommandArguments_OmitsExternalProvisionerByDefault()
    {
        var job = MakeJobWithSpace();   // PoolQueueId defaults to -1
        var args = job.ComposeCommandArguments();
        Assert.False(args.ContainsKey("external_provisioner"));
    }

    [Fact]
    public void WarpJobGpu_ComposeCommandArguments_AddsExternalProvisionerWhenPooled()
    {
        var job = MakeJobWithSpace();
        job.UseWorkerPool = true;
        job.PoolQueueId = 1;
        var args = job.ComposeCommandArguments();
        Assert.True(args.ContainsKey("external_provisioner"));
    }

    [Fact]
    public void WarpJobGpu_IsPooled_RequiresUseWorkerPoolToggle_NotJustQueue()
    {
        // The explicit pool toggle controls pooling independently of the selected queue.
        var job = MakeJobWithSpace();
        job.PoolQueueId = 1;
        Assert.False(job.IsPooled);
        Assert.Equal(-1, ((IPooledJob)job).PoolQueueId);            // gated off while toggle is off
        Assert.False(job.ComposeCommandArguments().ContainsKey("external_provisioner"));

        job.UseWorkerPool = true;
        Assert.True(job.IsPooled);
        Assert.Equal(1, ((IPooledJob)job).PoolQueueId);
    }

    [Fact]
    public void WarpJobGpu_WorkerRequiredModules_SwapsManagerCpuForWorkerGpu()
    {
        // Pooled Manager runs CPU-only; the worker does the GPU work. The worker module set must
        // request "gpu", never the Manager's "cpu".
        var job = MakeJobWithSpace();
        job.UseWorkerPool = true;
        job.PoolQueueId = 1;   // pooled → this job's RequiredModules carries "cpu"

        var workerModules = ((IPooledJob)job).WorkerRequiredModules;

        Assert.Contains("gpu", workerModules);
        Assert.DoesNotContain("cpu", workerModules);
    }

    [Fact]
    public void Refine_PooledResourceRequests_AreManagerProfileIndependentOfPoolSize()
    {
        // MCore's CoreCount/MemoryGb scale with NGpus*PerDevice for the non-pooled (single
        // multi-GPU job) path. When pooled, NGpus IS the pool size, so the CPU-only Manager
        // must fall back to the fixed manager profile instead of requesting worker-scaled
        // resources. Two pools of very different sizes must request identical Manager resources.
        var small = new RefineJob { UseWorkerPool = true, NGpus = 4,  PerDevice = 2, MemoryPerWorker = 10, PoolQueueId = 1 };
        var large = new RefineJob { UseWorkerPool = true, NGpus = 64, PerDevice = 2, MemoryPerWorker = 10, PoolQueueId = 1 };

        Assert.Equal(small.CoreCount, large.CoreCount);
        Assert.Equal(small.MemoryGb,  large.MemoryGb);

        // And the non-pooled path still scales with the GPU count.
        var local = new RefineJob { NGpus = 64, PerDevice = 2, MemoryPerWorker = 10 };
        Assert.True(local.CoreCount > large.CoreCount);
        Assert.True(local.MemoryGb  > large.MemoryGb);
    }

    [Fact]
    public void WarpJobGpu_WorkerRequiredModules_CarriesLeafToolModules()
    {
        var job = new EtomoJob { UseWorkerPool = true, PoolQueueId = 1 };

        var workerModules = ((IPooledJob)job).WorkerRequiredModules;

        Assert.Contains("imod", workerModules);   // the worker is what actually runs etomo
        Assert.Contains("gpu", workerModules);
        Assert.DoesNotContain("cpu", workerModules);
    }
}
