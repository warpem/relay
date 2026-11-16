using System.Reflection;
using Refund.Services.Warp;

namespace Refund.Tests.Warp;

/// <summary>Sessions stay loaded while something uses them, unload once idle, and cards read summaries without loading.</summary>
public sealed class SessionResidencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "warp-residency-" + Guid.NewGuid().ToString("N"));
    private readonly WarpSessionManager _manager = new(idleUnload: TimeSpan.FromMinutes(10));
    private WarpSessionIdentity Identity(int job = 1) => new(1, 1, job, Path.Combine(_root, job.ToString()), Path.Combine(_root, job.ToString(), "tasks"));

    private WarpSession Populate(WarpSession session, int movies)
    {
        session.Apply(new() { InputPath = _root });
        var list = (List<WarpMovie>)typeof(WarpSession).GetField("_movies", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        var keys = (Dictionary<string, ulong>)typeof(WarpSession).GetField("_stageKeys", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        for (int i = 0; i < movies; i++)
            list.Add(new WarpMovie { Index = i, Id = i.ToString(), Name = $"{i}.eer", InputPath = Path.Combine(_root, $"{i}.eer"), Ctf = 3, Motion = 1, Done = WarpStage.Motion | WarpStage.Ctf, Completed = new(keys) });
        session.SetDisposition(Enumerable.Range(0, movies), WarpDisposition.Auto);
        return session;
    }

    private static void WaitUntil(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500 && !condition(); i++) Thread.Sleep(10);
        Assert.True(condition(), what);
    }

    [Fact]
    public void LeasedSessionsStayAndIdleOnesUnloadWithTheirResults()
    {
        var identity = Identity();
        var lease = _manager.Acquire(identity);
        Populate(lease.Session, 3).FilterMetric("ctf-max", 3.5);
        var now = DateTime.UtcNow;
        Assert.Equal(0, _manager.UnloadIdle(now + TimeSpan.FromDays(1)));
        Assert.Equal(1, Assert.Single(_manager.LoadedSessions).Leases);
        lease.Dispose(); lease.Dispose(); // Disposing twice releases once.
        Assert.Equal(0, Assert.Single(_manager.LoadedSessions).Leases);
        Assert.Equal(0, _manager.UnloadIdle(DateTime.UtcNow + TimeSpan.FromMinutes(5)));
        Assert.Equal(1, _manager.UnloadIdle(DateTime.UtcNow + TimeSpan.FromMinutes(11)));
        // Loading again waits for the unload to save, and restores what the session had.
        var restored = _manager.GetOrCreate(identity);
        Assert.NotSame(lease.Session, restored);
        Assert.Equal(3, restored.Movies.Count);
        Assert.Equal(3.5, restored.MaxCtf);
        Assert.False(_manager.IsCurrent(lease.Session)); Assert.True(_manager.IsCurrent(restored));
    }

    [Fact]
    public void ExportingSessionsStayLoaded()
    {
        var session = Populate(_manager.GetOrCreate(Identity()), 1);
        var operation = session.BeginExportOperation();
        Assert.Equal(0, _manager.UnloadIdle(DateTime.UtcNow + TimeSpan.FromDays(1)));
        operation.Dispose();
        Assert.Equal(1, _manager.UnloadIdle(DateTime.UtcNow + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void WithoutATimeoutNothingUnloads()
    {
        using var manager = new WarpSessionManager();
        manager.GetOrCreate(Identity());
        Assert.Equal(0, manager.UnloadIdle(DateTime.UtcNow + TimeSpan.FromDays(1)));
        Assert.Single(manager.LoadedSessions);
    }

    [Fact]
    public void SummariesAreLiveWhileLoadedAndReadFromFileWithoutLoading()
    {
        var identity = Identity();
        var session = Populate(_manager.GetOrCreate(identity), 4);
        var live = _manager.GetSummary(identity)!;
        Assert.Equal(4, live.Received);
        Assert.Equal(session.Movies.Count(m => session.Status(m) == "Ready" || session.Status(m) == "Partial"),
            live.StatusCounts[Array.IndexOf(WarpSession.StatusNames, "Ready")] + live.StatusCounts[Array.IndexOf(WarpSession.StatusNames, "Partial")]);
        Assert.Equal(4, live.StatusCounts.Sum());
        Assert.Equal(1, _manager.UnloadIdle(DateTime.UtcNow + TimeSpan.FromDays(1)));
        WaitUntil(() => File.Exists(Path.Combine(identity.SessionDirectory, WarpSession.SummaryFile)) && _manager.LoadedSessions.Count == 0, "the unload saved a summary");
        var saved = _manager.GetSummary(identity)!;
        Assert.Equal(live.StatusCounts, saved.StatusCounts);
        Assert.Equal(live.Received, saved.Received);
        Assert.Empty(_manager.LoadedSessions); // Reading the summary loaded nothing.
    }

    [Fact]
    public void SessionsWithoutASummaryGetOneInTheBackground()
    {
        // A session saved before summaries existed, by an earlier run of Relay (another manager).
        var identity = Identity();
        using (var earlier = new WarpSessionManager()) Populate(earlier.GetOrCreate(identity), 2);
        string summary = Path.Combine(identity.SessionDirectory, WarpSession.SummaryFile);
        File.Delete(summary);
        var changed = new ManualResetEventSlim();
        _manager.SummaryChanged += key => { if (key == identity.Key) changed.Set(); };
        Assert.Null(_manager.GetSummary(identity));
        Assert.True(changed.Wait(TimeSpan.FromSeconds(10)), "the summary pass finished");
        WaitUntil(() => _manager.LoadedSessions.Count == 0, "the summarized session was unloaded again");
        Assert.Equal(2, _manager.GetSummary(identity)!.Received);
    }

    public void Dispose()
    {
        _manager.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
