using System.Diagnostics;
using Refund.Services.Storage;

namespace Refund.Tests.Storage;

public sealed class StorageSchedulerTests
{
    private static readonly DateTime Now = new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
    private static StorageTarget J(int id) => StorageTarget.Job(1, 1, id);

    private static StorageTarget Take(StorageScheduler s, DateTime now)
    {
        Assert.True(s.TryTake(now, out var t, out _, out _));
        s.Done(t, now);
        return t;
    }

    [Fact]
    public void ClassesGoInOrderAndChangesWaitForTheJobToSettle()
    {
        var s = new StorageScheduler();
        s.Enqueue(J(1), StorageClass.Stale, Now);
        s.SeedCatchUp([(J(2), "type", 0, Now)]);
        s.Enqueue(J(3), StorageClass.Changed, Now);
        s.Enqueue(J(4), StorageClass.Requested, Now);
        s.Enqueue(StorageTarget.Listing(1, 1), StorageClass.Background, Now);

        Assert.Equal(J(4), Take(s, Now));
        // The change waits 30 s; catch-up goes meanwhile.
        Assert.Equal(J(2), Take(s, Now));
        Assert.Equal(J(1), Take(s, Now));
        Assert.Equal(StorageTarget.Listing(1, 1), Take(s, Now));
        Assert.False(s.TryTake(Now, out _, out _, out var wait));
        Assert.True(wait <= StorageScheduler.ChangeDelay && wait > TimeSpan.FromSeconds(25));
        // Another change of the same job pushes it back.
        s.Enqueue(J(3), StorageClass.Changed, Now + TimeSpan.FromSeconds(20));
        Assert.False(s.TryTake(Now + TimeSpan.FromSeconds(31), out _, out _, out _));
        Assert.Equal(J(3), Take(s, Now + TimeSpan.FromSeconds(51)));
    }

    [Fact]
    public void CatchUpSamplesEveryTypeThenTakesTheBiggestTypeFirst()
    {
        var s = new StorageScheduler();
        var entries = new List<(StorageTarget, string, long, DateTime)>();
        for (int i = 0; i < 10; i++) entries.Add((J(100 + i), "small", 0, Now.AddDays(-i)));
        for (int i = 0; i < 10; i++) entries.Add((J(200 + i), "big", i, Now.AddDays(-i)));
        s.SeedCatchUp(entries);

        // Three of each type first.
        var first = Enumerable.Range(0, 6).Select(_ => Take(s, Now)).ToList();
        Assert.Equal(3, first.Count(t => t.JobId < 200));
        Assert.Equal(3, first.Count(t => t.JobId >= 200));
        // Within a type: most output items, then newest.
        Assert.Equal([209, 208, 207], first.Where(t => t.JobId >= 200).Select(t => t.JobId));
        Assert.Equal([100, 101, 102], first.Where(t => t.JobId < 200).Select(t => t.JobId));

        foreach (var t in first) s.Learn(t.JobId >= 200 ? "big" : "small", t.JobId >= 200 ? 1_000_000_000 : 1000, 10);
        // Then all of the big type before the rest of the small one.
        var next = Enumerable.Range(0, 7).Select(_ => Take(s, Now)).ToList();
        Assert.All(next, t => Assert.True(t.JobId >= 200));
        Assert.True(Take(s, Now).JobId < 200);
    }

    [Fact]
    public void AMoreUrgentRequestWinsAndRemovedTargetsAreSkipped()
    {
        var s = new StorageScheduler();
        s.SeedCatchUp([(J(1), "t", 0, Now), (J(2), "t", 0, Now)]);
        s.Enqueue(J(2), StorageClass.Requested, Now);
        s.Remove(J(1));

        Assert.Equal(J(2), Take(s, Now));
        Assert.False(s.TryTake(Now, out _, out _, out _));
    }

    [Fact]
    public void AChangeDuringAMeasurementMeasuresAgain()
    {
        var s = new StorageScheduler();
        s.Enqueue(J(1), StorageClass.Requested, Now);
        Assert.True(s.TryTake(Now, out var t, out _, out _));
        s.Enqueue(J(1), StorageClass.Changed, Now);
        Assert.True(s.Done(t, Now));
        Assert.Equal(J(1), Take(s, Now + StorageScheduler.ChangeDelay));
    }

    [Fact]
    public void RunningJobsAreRefreshedWhenDue()
    {
        var s = new StorageScheduler();
        s.RefreshRunning(J(1), Now + TimeSpan.FromHours(1));
        Assert.False(s.TryTake(Now, out _, out _, out _));
        Assert.Equal(J(1), Take(s, Now + TimeSpan.FromHours(1)));
        // Not again until the measurement schedules the next refresh.
        Assert.False(s.TryTake(Now + TimeSpan.FromHours(5), out _, out _, out _));
        s.StopRunning(J(1));
    }

    [Fact]
    public void SpacesOnOneFileSystemShareOneMeterThoughFreeSpaceMovesBetweenSamples()
    {
        static Refund.DataModel.ReadOnly.ReadOnlySpace Space(string root) => new Refund.DataModel.Space { RootDirectory = root }.AsReadOnly();
        StorageService.FileSystemSample Fs(ulong id, long total, long free, long files, long freeFiles) => new(id, total, free, files, freeFiles, true);
        var t0 = Now;
        var usage = StorageService.Group([
            (Space("/data/a/one"), t0, Fs(1, 220, 75, 23, 2)),
            (Space("/data/a/two"), t0.AddSeconds(40), Fs(1, 220, 74, 23, 1)),
            (Space("/data/b/three"), t0, Fs(1, 600, 101, 157, 70)),
            (Space("/data/b/four"), t0.AddSeconds(20), Fs(1, 600, 100, 157, 69)),
            (Space("/data/c"), t0, default),
        ]);

        Assert.Equal(2, usage.Count);
        var a = usage.Single(u => u.TotalBytes == 220);
        Assert.Equal(2, a.Spaces.Count);
        Assert.Equal(74, a.FreeBytes); // the newest sample
        Assert.Equal(1, a.FreeFiles);
        Assert.Equal("/data/a", a.Label);
        Assert.Equal(100, usage.Single(u => u.TotalBytes == 600).FreeBytes);
    }

    [Fact]
    public void TheThrottleCapsTheRateAndBacksOffWhenEntriesGetSlow()
    {
        long ticks = 0;
        var slept = TimeSpan.Zero;
        var throttle = new StorageThrottle(1000, () => ticks, d => { slept += d; ticks += (long)(d.TotalSeconds * Stopwatch.Frequency); });
        void Batch(double microsPerEntry)
        {
            ticks += (long)(64 * microsPerEntry / 1e6 * Stopwatch.Frequency);
            throttle.Pace(64, TimeSpan.FromMicroseconds(64 * microsPerEntry), CancellationToken.None);
        }

        // 64,000 entries at 1,000/s take about a minute however fast each entry is.
        for (int i = 0; i < 1000; i++) Batch(10);
        double seconds = ticks / (double)Stopwatch.Frequency;
        Assert.InRange(seconds, 60, 68);
        Assert.False(throttle.SlowedDown);

        // Entries take 10x longer: the cap halves, at most once per 5 s.
        for (int i = 0; i < 20; i++) Batch(100);
        Assert.True(throttle.SlowedDown);
        Assert.InRange(throttle.Rate, StorageThrottle.Floor, 500);

        // Back to normal: it recovers to the ceiling within a few dozen seconds.
        for (int i = 0; i < 3000; i++) Batch(10);
        Assert.False(throttle.SlowedDown);
        Assert.Equal(1000, throttle.Rate, 1);
    }
}
