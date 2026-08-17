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
        var live = new List<ResourceAllocation> { Alloc(1, 1, 0, 1) };
        live.Clear();

        Assert.True(ResourceLedger.TryFit(Host, live, new ResourceRequest(1, 1, 1), out var got));
        Assert.Equal(new[] { 0 }, got.GpuIndices);
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
