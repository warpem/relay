using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;

namespace Refund.DataModel;

public abstract class JobQueue : RelayBase
{
    private static readonly ConditionalWeakTable<JobQueue, ReadOnlyJobQueue> ReadOnlyCache = new();
    private Func<IReadOnlyList<Job>> _jobs = () => Array.Empty<Job>();

    [RelayProperty]
    public int Id { get; set; } = -1;

    [RelayProperty]
    public string Alias { get; set; }

    public string QualifiedName => $"Q{Id}: {Alias}";

    [RelayProperty]
    public JobQueueType QueueType { get; set; }

    public ReadOnlyCollection<Job> QueuedJobs =>
        new((_jobs() ?? Array.Empty<Job>()).ToList());

    public bool IsEmpty => QueuedJobs.Count == 0;

    protected JobQueue()
    {
    }

    internal void SetJobsProvider(Func<IReadOnlyList<Job>> jobs)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
    }

    public virtual ReadOnlyJobQueue AsReadOnly() =>
        ReadOnlyCache.GetValue(this, queue => new ReadOnlyJobQueue(queue));

    public override void ReadFromJson(JsonNode reader) =>
        base.ReadFromJson(reader);
}

[Flags]
public enum JobQueueType
{
    Local = 1 << 0,
    CPU = 1 << 1,
    GPU = 1 << 2,
    Mixed = (1 << 1) | (1 << 2)
}

public enum ClusterScheduler
{
    Slurm = 0,
    Lsf = 1,
    Pbs = 2,
    Sge = 3,
    Flux = 4,
    Custom = 5,
    Managed = 6
}
