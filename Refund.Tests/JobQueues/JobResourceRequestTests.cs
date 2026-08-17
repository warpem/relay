using Warp.Tools;
using Refund.DataModel;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;
using ImportTsJob = Refund.Jobs.Ts.Import.ImportDataSetTs.ImportDataSetTs;
using Refund.Jobs.Refinement.Classes2D.Class2D;
using Class2DJob = Refund.Jobs.Refinement.Classes2D.Class2D.Class2D;

namespace Refund.Tests.JobQueues;

[Collection("JobRegistry")]
public class JobResourceRequestTests
{
    private static readonly object _populateLock = new();

    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    [Fact]
    public void CpuOnlyJobs_RequestNoGpus()
    {
        EnsurePopulated();
        Assert.Equal(0, new MaskJob().GpuCount);
        Assert.Equal(0, new ImportTsJob().GpuCount);
    }

    [Fact]
    public void Class2D_RequestsAGpuOnlyWhenItUsesOne()
    {
        EnsurePopulated();
        Assert.Equal(1, new Class2DJob { UseGpu = true }.GpuCount);
        Assert.Equal(0, new Class2DJob { UseGpu = false }.GpuCount);
    }

    [Fact]
    public void Class2D_MpiRun_ReportsItsRealCpuFootprint()
    {
        EnsurePopulated();

        // VDAM with 4 workers of 6 threads each is the branch that actually launches MPI.
        var job = new Class2DJob
        {
            Algorithm = Class2DAlgorithm.VDAM,
            NProcesses = 4,
            NThreads = 6,
            UseGpu = true,
        };

        Assert.Equal("mpirun -n 4 relion_refine_mpi", job.CommandName);
        Assert.Equal(4, job.ProcessCount);   // four ranks
        Assert.Equal(6, job.CoreCount);      // per rank, not 24 in total
        Assert.Equal(48, job.MemoryGb);      // 3 working ranks x 16 GB; rank 0 is the manager
        Assert.Equal(1, job.GpuCount);
    }

    [Fact]
    public void Class2D_EmRun_IsSingleProcessEvenWhenWorkersAreConfigured()
    {
        EnsurePopulated();

        // CommandName never uses MPI for EM, so NProcesses must not leak into the resource request.
        var job = new Class2DJob
        {
            Algorithm = Class2DAlgorithm.EM,
            NProcesses = 4,
            NThreads = 6,
        };

        Assert.Equal("relion_refine", job.CommandName);
        Assert.Equal(1, job.ProcessCount);
        Assert.Equal(6, job.CoreCount);
        Assert.Equal(16, job.MemoryGb);
    }

    [Fact]
    public void Class2D_Defaults_RequestWhatTheyAlwaysHave()
    {
        // The explicit overrides must not change what an untouched Class2D asks a cluster for.
        EnsurePopulated();

        var job = new Class2DJob();

        Assert.Equal(1, job.ProcessCount);
        Assert.Equal(1, job.CoreCount);
        Assert.Equal(16, job.MemoryGb);
    }

    [Fact]
    public void BaseGpuCountDefault_IsZero_MatchingItsDocumentedContract()
    {
        // Every concrete job type now states GpuCount explicitly, so the base default can only
        // be observed through a type that declines to override it — hence the stub below.
        Assert.Equal(0, new JobWithoutResourceOverrides().GpuCount);
    }

    [Fact]
    public void EveryRegisteredJobType_StatesItsGpuCountExplicitly()
    {
        // Admission decisions read GpuCount, so no job type may leave it to the base default —
        // in either direction. A GPU job that forgets would silently claim to need none.
        EnsurePopulated();

        var inheriting = Job.Types.Values
                            .Where(t => t.GetProperty(nameof(Job.GpuCount))!.DeclaringType == typeof(Job))
                            .Select(t => t.Name)
                            .OrderBy(n => n, StringComparer.Ordinal)
                            .ToArray();

        Assert.True(inheriting.Length == 0,
                    "These job types inherit Job.GpuCount instead of stating it: " +
                    string.Join(", ", inheriting));
    }

    /// <summary>
    /// A minimal Job that overrides only what the base declares abstract. It stands in for a
    /// job type someone adds later without thinking about GPUs. It lives in the test assembly,
    /// so Job.PopulateTypes() — which scans only the Refund assembly — never registers it.
    /// </summary>
    private sealed class JobWithoutResourceOverrides : Job
    {
        public override int2 CardSquareCount { get; set; } = new int2(1, 1);
        public override string TypeGuid => "00000000-0000-0000-0000-000000000000";
        public override string TypeCategory => "Test.Stub";
        public override string TypeName => "Stub";
        public override string TypeNameShort => "Stub";
        public override string TypeDescription => "Test stub with no resource overrides";
        public override JobQueueType QueueType => JobQueueType.CPU;
        public override Type ExpandedViewType => typeof(object);
    }
}
