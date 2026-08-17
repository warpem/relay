using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

public class ResourceLedgerTests
{
    private static readonly ResourceTotals Host = new(Cores: 16, MemoryGb: 64, Gpus: 4);

    private static ResourceAllocation Alloc(int cores, int mem, params int[] gpus) =>
        new(cores, mem, gpus);

    [Fact]
    public void Compute_WithNoAllocations_ReportsEverythingFree()
    {
        var snap = ResourceLedger.Compute(Host, Array.Empty<ResourceAllocation>());

        Assert.Equal(16, snap.FreeCores);
        Assert.Equal(64, snap.FreeMemoryGb);
        Assert.Equal(new[] { 0, 1, 2, 3 }, snap.FreeGpuIndices);
    }

    [Fact]
    public void Compute_SubtractsLiveAllocations()
    {
        var snap = ResourceLedger.Compute(Host, new[] { Alloc(4, 16, 0), Alloc(2, 8, 2) });

        Assert.Equal(10, snap.FreeCores);
        Assert.Equal(40, snap.FreeMemoryGb);
        Assert.Equal(new[] { 1, 3 }, snap.FreeGpuIndices);
    }

    [Fact]
    public void DroppingAnEntryFreesItsResources_WithoutAnyReleaseCall()
    {
        // The invariant the whole design rests on: "release" is not an operation, it is what has
        // already happened once an entry is no longer in the live set.
        var live = new List<ResourceAllocation> { Alloc(16, 64, 0, 1, 2, 3) };
        Assert.False(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 1), out _));

        live.Clear();

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 1), out var got));
        Assert.Equal(new[] { 0 }, got.GpuIndices);
    }

    [Theory]
    [InlineData(17, 1, 0)]   // cores
    [InlineData(1, 65, 0)]   // memory
    [InlineData(1, 1, 5)]    // gpus
    public void TryFit_RefusesRequestsLargerThanFree(int cores, int mem, int gpus)
    {
        Assert.False(ResourceLedger.TryFit(
            Host, Array.Empty<ResourceAllocation>(), new ResourceRequest(cores, mem, gpus), out _));
    }

    [Fact]
    public void TryFit_AssignsLowestFreeGpuIndices_AndTheyAreDisjoint()
    {
        var live = new List<ResourceAllocation>();

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 2), out var first));
        live.Add(first);
        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 2), out var second));

        Assert.Equal(new[] { 0, 1 }, first.GpuIndices);
        Assert.Equal(new[] { 2, 3 }, second.GpuIndices);
        Assert.Empty(first.GpuIndices.Intersect(second.GpuIndices));
    }

    [Fact]
    public void TryFit_ReusesIndicesFreedByADroppedEntry()
    {
        // The entry must actually be live for a while, or this proves nothing beyond the
        // empty-list case: hold {0,1}, watch the next request get pushed to {2,3}, then drop the
        // holder and watch {0,1} become available again.
        var live = new List<ResourceAllocation>();

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 2), out var holder));
        live.Add(holder);
        Assert.Equal(new[] { 0, 1 }, holder.GpuIndices);

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 2), out var pushedAside));
        Assert.Equal(new[] { 2, 3 }, pushedAside.GpuIndices);

        live.Remove(holder);

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 2), out var reused));
        Assert.Equal(new[] { 0, 1 }, reused.GpuIndices);
    }

    [Fact]
    public void TryFit_RecordsTheCoresAndMemoryItHandedOut()
    {
        // Without this the ledger can report every allocation as costing nothing: Compute would
        // subtract zero, and the host would be oversubscribed without limit.
        Assert.True(ResourceLedger.TryFit(
            Host, Array.Empty<ResourceAllocation>(), new ResourceRequest(6, 24, 1), out var got));

        Assert.Equal(6, got.Cores);
        Assert.Equal(24, got.MemoryGb);

        // And those numbers must be the ones Compute charges against the host.
        var snap = ResourceLedger.Compute(Host, new[] { got });
        Assert.Equal(10, snap.FreeCores);
        Assert.Equal(40, snap.FreeMemoryGb);
    }

    [Fact]
    public void TryFit_AcceptsARequestSizedExactlyToTheHost()
    {
        // A boundary that is off by one in the unsafe direction would leave a job that needs the
        // whole host permanently unstartable, with nothing to show for it.
        Assert.True(ResourceLedger.TryFit(
            Host, Array.Empty<ResourceAllocation>(), new ResourceRequest(16, 64, 4), out var got));

        Assert.Equal(16, got.Cores);
        Assert.Equal(64, got.MemoryGb);
        Assert.Equal(new[] { 0, 1, 2, 3 }, got.GpuIndices);
    }

    [Fact]
    public void TryFit_AcceptsARequestSizedExactlyToWhatIsLeft()
    {
        // Same boundary, but against free rather than total.
        var live = new[] { Alloc(6, 24, 0) };

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(10, 40, 3), out var got));
        Assert.Equal(new[] { 1, 2, 3 }, got.GpuIndices);
    }

    [Theory]
    [InlineData(-1, 64, 4)]    // negative cores
    [InlineData(16, -1, 4)]    // negative memory
    [InlineData(16, 64, -1)]   // negative gpus — Enumerable.Range would throw on this
    public void NegativeTotals_YieldNoCapacityInsteadOfThrowing(int cores, int mem, int gpus)
    {
        // This runs on the admission path. HandleWaitingState's catch logs without changing job
        // status, so a throw here is a job stuck in Waiting re-logging on every daemon tick —
        // the exact pathology AdmissionResult was introduced to avoid. Nonsense totals must mean
        // "nothing fits", never "the queue is wedged".
        var totals = new ResourceTotals(cores, mem, gpus);
        var request = new ResourceRequest(1, 1, 1);

        var snap = ResourceLedger.Compute(totals, Array.Empty<ResourceAllocation>());
        Assert.NotNull(snap.FreeGpuIndices);

        Assert.False(ResourceLedger.TryFit(totals, Array.Empty<ResourceAllocation>(), request, out _));
        Assert.False(ResourceLedger.CanEverFit(totals, request));
    }

    [Fact]
    public void ReturnedGpuIndices_CannotBeWrittenThrough()
    {
        // Disjointness is the one guarantee this type makes; a mutable List behind an
        // IReadOnlyList would let a caller silently break it for everyone else.
        Assert.True(ResourceLedger.TryFit(
            Host, Array.Empty<ResourceAllocation>(), new ResourceRequest(1, 1, 2), out var got));

        Assert.Throws<NotSupportedException>(() => ((IList<int>)got.GpuIndices)[0] = 99);

        var snap = ResourceLedger.Compute(Host, Array.Empty<ResourceAllocation>());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)snap.FreeGpuIndices)[0] = 99);
    }

    [Fact]
    public void TryFit_RequestingZeroGpus_AssignsNone()
    {
        Assert.True(ResourceLedger.TryFit(
            Host, Array.Empty<ResourceAllocation>(), new ResourceRequest(2, 4, 0), out var got));
        Assert.Empty(got.GpuIndices);
    }

    [Fact]
    public void CanEverFit_IsAboutTotals_NotCurrentUsage()
    {
        var full = new[] { Alloc(16, 64, 0, 1, 2, 3) };

        // Busy, but possible later.
        Assert.True(ResourceLedger.CanEverFit(Host, new ResourceRequest(16, 64, 4)));
        Assert.False(ResourceLedger.TryFit(Host, full, new ResourceRequest(16, 64, 4), out _));

        // Impossible on an empty host — must be rejected, never queued.
        Assert.False(ResourceLedger.CanEverFit(Host, new ResourceRequest(1, 1, 5)));
    }
}
