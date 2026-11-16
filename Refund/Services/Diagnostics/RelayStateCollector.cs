using System.Diagnostics;
using System.Runtime;
using Refund.Services.Core.DataManager;
using Refund.Services.Warp;

namespace Refund.Services.Diagnostics;

/// <summary>What the State page shows: this process's memory, connections, loaded Warp sessions, execution and data.</summary>
public sealed record RelayState(
    DateTime Time, TimeSpan Uptime, long WorkingSet, long PrivateBytes, int Threads, int Handles,
    long GcHeap, long GcCommitted, long GcFragmented, long[] GenerationSizes, double GcPausePercent, int[] Collections, long TotalAllocated,
    int PoolThreads, long PendingWorkItems,
    CircuitStats.Snapshot Circuits,
    IReadOnlyList<WarpSessionManager.LoadedSession> WarpSessions, TimeSpan? WarpIdleUnload,
    IReadOnlyList<(string Queue, string Phase, int Count)> Attempts,
    int Projects, int Spaces, int Jobs, int Users, IReadOnlyList<(string Status, int Count)> JobsByStatus)
{
    /// <summary>Generation names, in <see cref="GenerationSizes"/> order.</summary>
    public static readonly string[] Generations = ["Gen 0", "Gen 1", "Gen 2", "Large objects", "Pinned objects"];
    /// <summary>Rough memory a loaded Warp session holds per movie (measured: ~50 MB for 5,000 movies).</summary>
    public const long WarpBytesPerMovie = 10_000;
}

public sealed class RelayStateCollector(DataManager dataManager, CircuitStats circuits)
{
    public RelayState Collect()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var gc = GC.GetGCMemoryInfo(GCKind.Any);
        long[] generations = Enumerable.Range(0, Math.Min(gc.GenerationInfo.Length, RelayState.Generations.Length))
            .Select(i => gc.GenerationInfo[i].SizeAfterBytes).ToArray();
        var queues = new Dictionary<int, string> { [dataManager.LocalQueue.Id] = "Local" };
        foreach (var queue in dataManager.ClusterQueues) queues[queue.Id] = string.IsNullOrWhiteSpace(queue.Alias) ? $"Queue {queue.Id}" : queue.Alias;
        var attempts = dataManager.ExecutionAttempts
            .GroupBy(a => (Queue: queues.GetValueOrDefault(a.QueueId, $"Queue {a.QueueId}"), Phase: a.Phase.ToString()))
            .Select(g => (g.Key.Queue, g.Key.Phase, g.Count())).OrderBy(a => a.Queue).ThenBy(a => a.Phase).ToList();
        var projects = dataManager.Projects;
        var jobs = projects.SelectMany(p => p.Spaces).SelectMany(s => s.Jobs).ToList();
        // Before the first collection the GC report is empty; the live estimate stands in for the heap.
        long heap = gc.HeapSizeBytes > 0 ? gc.HeapSizeBytes : GC.GetTotalMemory(false);
        return new(DateTime.UtcNow, DateTime.Now - process.StartTime, process.WorkingSet64, process.PrivateMemorySize64, process.Threads.Count, process.HandleCount,
            heap, gc.TotalCommittedBytes, gc.FragmentedBytes, generations, gc.PauseTimePercentage,
            [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)], GC.GetTotalAllocatedBytes(),
            ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount,
            circuits.Current(),
            dataManager.WarpSessions.LoadedSessions.OrderByDescending(s => s.LastUsed).ToList(), dataManager.WarpSessions.IdleUnload,
            attempts,
            projects.Count, projects.Sum(p => p.Spaces.Count), jobs.Count, dataManager.Users.Count,
            jobs.GroupBy(j => j.Status.ToString()).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2).ToList());
    }

    /// <summary>A blocking, compacting full collection (large objects included), to see what memory is really retained.</summary>
    public static void CollectGarbage()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }
}
