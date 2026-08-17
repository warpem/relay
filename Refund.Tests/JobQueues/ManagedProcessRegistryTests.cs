using Refund.JobQueues;

namespace Refund.Tests.JobQueues;

/// <summary>
/// The leftover registry: what survives a crash, and the identity rules that decide what the next
/// startup is allowed to kill.
/// </summary>
public class ManagedProcessRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-registry-" + Guid.NewGuid());
    private string Path_ => System.IO.Path.Combine(_dir, "managed-processes.json");

    public ManagedProcessRegistryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    #region Persistence

    [Fact]
    public void RecordsSurviveAReload()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(JobId: 7, Pid: 111, Pgid: 111, StartTimeTicks: 999));

        var reloaded = new ManagedProcessRegistry(Path_).Load();

        var record = Assert.Single(reloaded);
        Assert.Equal(7, record.JobId);
        Assert.Equal(111, record.Pid);
        Assert.Equal(111, record.Pgid);
        Assert.Equal(999, record.StartTimeTicks);
    }

    [Fact]
    public void ANullPgidSurvivesTheRoundTrip_AsNullAndNotAsZero()
    {
        // A zero read back where null was written would be a pgid, and every downstream check is
        // "is this non-null" before it decides whether a group kill is allowed.
        new ManagedProcessRegistry(Path_).Record(new ManagedProcessRecord(1, 111, null, 999));

        Assert.Null(Assert.Single(new ManagedProcessRegistry(Path_).Load()).Pgid);
    }

    [Fact]
    public void RecordingTheSameJobAgain_ReplacesItsPreviousProcess()
    {
        // A relaunch, or a record upgraded once its process group resolved. Two records for one job
        // would put the earlier run's pid — long since recycled — in range of the next sweep.
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 111, null, 999));
        registry.Record(new ManagedProcessRecord(1, 111, 111, 999));

        var record = Assert.Single(registry.Load());
        Assert.Equal(111, record.Pgid);
    }

    [Fact]
    public void Forget_RemovesOnlyThatJob()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 111, 111, 5));
        registry.Record(new ManagedProcessRecord(2, 222, 222, 6));

        registry.Forget(1);

        Assert.Equal(2, Assert.Single(new ManagedProcessRegistry(Path_).Load()).JobId);
    }

    [Fact]
    public void Clear_EmptiesTheFile()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 111, 111, 5));

        registry.Clear();

        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }

    [Fact]
    public void CorruptFile_LoadsAsEmptyRatherThanThrowing()
    {
        // A half-written file after a crash must not stop Relay from starting.
        File.WriteAllText(Path_, "{ not json");
        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }

    [Fact]
    public void AMissingFileAndAMissingDirectory_AreBothHandled()
    {
        var nested = System.IO.Path.Combine(_dir, "does", "not", "exist", "managed-processes.json");
        var registry = new ManagedProcessRegistry(nested);

        Assert.Empty(registry.Load());

        registry.Record(new ManagedProcessRecord(1, 111, 111, 5));
        Assert.Single(new ManagedProcessRegistry(nested).Load());
    }

    #endregion

    #region The startup sweep

    [Fact]
    public void KillLeftovers_KillsARecordWhosePidAndStartTimeBothStillMatch()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(JobId: 1, Pid: 4242, Pgid: 4242, StartTimeTicks: 1000));

        var killed = new List<ManagedProcessRecord>();
        var count = ManagedProcessRegistry.KillLeftovers(
            Path_, startTimeOf: _ => new DateTime(1000), kill: killed.Add);

        Assert.Equal(1, count);
        Assert.Equal(4242, Assert.Single(killed).Pid);
    }

    [Fact]
    public void KillLeftovers_SkipsRecordsWhoseStartTimeNoLongerMatches()
    {
        // Pids are recycled. Killing on pid alone could terminate an unrelated process that
        // happened to inherit the number after a crash.
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(JobId: 1, Pid: 4242, Pgid: 4242, StartTimeTicks: 1000));

        var killed = new List<ManagedProcessRecord>();
        var count = ManagedProcessRegistry.KillLeftovers(
            Path_, startTimeOf: _ => new DateTime(9999),   // live, but a different process
            kill: killed.Add);

        Assert.Equal(0, count);
        Assert.Empty(killed);                              // and nothing was signalled at all
    }

    [Fact]
    public void KillLeftovers_SkipsRecordsWithNoLiveProcess()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 4242, 4242, 1000));

        var killed = new List<ManagedProcessRecord>();
        Assert.Equal(0, ManagedProcessRegistry.KillLeftovers(Path_, _ => null, killed.Add));
        Assert.Empty(killed);
    }

    [Fact]
    public void KillLeftovers_KillsOnlyTheMatchingRecordsOfAMixedFile()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(JobId: 1, Pid: 111, Pgid: 111, StartTimeTicks: 1000));
        registry.Record(new ManagedProcessRecord(JobId: 2, Pid: 222, Pgid: 222, StartTimeTicks: 2000));
        registry.Record(new ManagedProcessRecord(JobId: 3, Pid: 333, Pgid: 333, StartTimeTicks: 3000));

        var killed = new List<int>();
        var count = ManagedProcessRegistry.KillLeftovers(
            Path_,
            startTimeOf: pid => pid switch
            {
                111 => new DateTime(1000),   // ours, still running
                222 => null,                 // gone
                _   => new DateTime(9999),   // pid recycled into somebody else's process
            },
            kill: r => killed.Add(r.Pid));

        Assert.Equal(1, count);
        Assert.Equal(new[] { 111 }, killed);
    }

    [Fact]
    public void KillLeftovers_PassesANullPgidThrough_RatherThanSubstitutingThePid()
    {
        // The macOS interlock, at the layer that reconstructs a kill from a file. A null pgid means
        // the process was in *Relay's own* group; turning it into kill(-pgid) here would kill the
        // Relay that is starting up. There is no Process handle at this point, so nothing else
        // could catch a pgid invented on the way in.
        new ManagedProcessRegistry(Path_).Record(new ManagedProcessRecord(1, 4242, null, 1000));

        var killed = new List<ManagedProcessRecord>();
        ManagedProcessRegistry.KillLeftovers(Path_, _ => new DateTime(1000), killed.Add);

        Assert.Null(Assert.Single(killed).Pgid);
    }

    [Fact]
    public void KillLeftovers_ClearsTheFileAfterSweeping()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 4242, 4242, 1000));

        ManagedProcessRegistry.KillLeftovers(Path_, startTimeOf: _ => null);

        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }

    [Fact]
    public void KillLeftovers_ClearsTheFileEvenWhenItKilledSomething()
    {
        // Otherwise the same pids are swept again on the next boot, by which time they belong to
        // somebody else.
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 4242, 4242, 1000));

        ManagedProcessRegistry.KillLeftovers(Path_, _ => new DateTime(1000), _ => { });

        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }

    [Fact]
    public void KillLeftovers_OnAnAbsentFile_IsANoOp()
    {
        Assert.Equal(0, ManagedProcessRegistry.KillLeftovers(
            Path_, _ => throw new InvalidOperationException("nothing should be probed")));
    }

    [Fact]
    public void LiveProcessStartTime_AnswersForThisProcessAndNullForAnImpossiblePid()
    {
        Assert.NotNull(ManagedProcessRegistry.LiveProcessStartTime(Environment.ProcessId));
        Assert.Null(ManagedProcessRegistry.LiveProcessStartTime(-1));
    }

    #endregion
}
