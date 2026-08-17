using System.Diagnostics;
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
        registry.Record(new ManagedProcessRecord(ProjectId: 1, SpaceId: 1, JobId: 7, Pid: 111, Pgid: 111, StartTimeTicks: 999));

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
        new ManagedProcessRegistry(Path_).Record(new ManagedProcessRecord(1, 1, 1, 111, null, 999));

        Assert.Null(Assert.Single(new ManagedProcessRegistry(Path_).Load()).Pgid);
    }

    [Fact]
    public void RecordingTheSameJobAgain_ReplacesItsPreviousProcess()
    {
        // A relaunch, or a record upgraded once its process group resolved. Two records for one job
        // would put the earlier run's pid — long since recycled — in range of the next sweep.
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 1, 1, 111, null, 999));
        registry.Record(new ManagedProcessRecord(1, 1, 1, 111, 111, 999));

        var record = Assert.Single(registry.Load());
        Assert.Equal(111, record.Pgid);
    }

    [Fact]
    public void Forget_RemovesOnlyThatJob()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 1, 1, 111, 111, 5));
        registry.Record(new ManagedProcessRecord(1, 1, 2, 222, 222, 6));

        registry.Forget(1, 1, 1);

        Assert.Equal(2, Assert.Single(new ManagedProcessRegistry(Path_).Load()).JobId);
    }

    [Fact]
    public void TwoSpacesCanEachHaveAJobFive_WithoutEitherErasingTheOther()
    {
        // Job.Id is allocated per space (Space.cs:190), so a host routinely runs two jobs with the
        // same id. Keyed on Job.Id alone, the second launch drops the first's record — and, far
        // worse, the first job settling calls Forget and *un-registers the other space's running
        // process*, in an ordinary non-crashing shutdown, leaving it unkillable after a crash.
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(ProjectId: 1, SpaceId: 1, JobId: 5,
                                                 Pid: 111, Pgid: 111, StartTimeTicks: 5));
        registry.Record(new ManagedProcessRecord(ProjectId: 1, SpaceId: 2, JobId: 5,
                                                 Pid: 222, Pgid: 222, StartTimeTicks: 6));

        Assert.Equal(2, registry.Load().Count);      // the launch did not clobber

        registry.Forget(1, 1, 5);                    // space 1's job 5 settles

        var survivor = Assert.Single(registry.Load());
        Assert.Equal(2, survivor.SpaceId);            // space 2's is still registered
        Assert.Equal(222, survivor.Pid);
    }

    [Fact]
    public void TwoProjectsCanEachHaveASpaceOneJobFive()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 1, 5, 111, 111, 5));
        registry.Record(new ManagedProcessRecord(2, 1, 5, 222, 222, 6));

        registry.Forget(2, 1, 5);

        Assert.Equal(111, Assert.Single(registry.Load()).Pid);
    }

    [Fact]
    public void Clear_EmptiesTheFile()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 1, 1, 111, 111, 5));

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
    public void AnUnreadableFile_IsPreservedRatherThanOverwritten()
    {
        // Failing open is fine — Relay has to start. Failing open *and then overwriting* is not:
        // those bytes may name a live orphan holding a GPU, and once the sweep has replaced them
        // with an empty list nothing can ever find that process again. An absent file means "no
        // leftovers"; an unreadable one means "we do not know", and the two must not be confused.
        File.WriteAllText(Path_, "{ not json");

        ManagedProcessRegistry.KillLeftovers(Path_, _ => null,
                                             _ => Assert.Fail("there is nothing to kill"),
                                             confirmWait: TimeSpan.Zero);

        Assert.Equal("{ not json", File.ReadAllText(Path_));

        // Nor does an ordinary launch or settle overwrite it on the way past.
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(new ManagedProcessRecord(1, 1, 1, 111, 111, 5));
        registry.Forget(1, 1, 1);
        registry.Clear();

        Assert.Equal("{ not json", File.ReadAllText(Path_));
    }

    [Fact]
    public void AMissingFileAndAMissingDirectory_AreBothHandled()
    {
        var nested = System.IO.Path.Combine(_dir, "does", "not", "exist", "managed-processes.json");
        var registry = new ManagedProcessRegistry(nested);

        Assert.Empty(registry.Load());

        registry.Record(new ManagedProcessRecord(1, 1, 1, 111, 111, 5));
        Assert.Single(new ManagedProcessRegistry(nested).Load());
    }

    #endregion

    #region The startup sweep

    /// <summary>When the crashed Relay recorded its jobs as having started.</summary>
    private static readonly DateTime Launched = new(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);

    private static ManagedProcessRecord At(DateTime startTime, int pid = 4242, int? pgid = 4242,
                                           int jobId = 1) =>
        new(ProjectId: 1, SpaceId: 1, JobId: jobId, Pid: pid, Pgid: pgid,
            StartTimeTicks: startTime.Ticks);

    /// <summary>
    /// A host whose processes actually die when they are signalled — unless told otherwise. The
    /// sweep re-probes after killing and only counts, and only forgets, a process it has confirmed
    /// gone, so a kill callback that quietly does nothing is a <em>failed</em> kill.
    /// </summary>
    private sealed class FakeHost
    {
        private readonly Dictionary<int, DateTime> _live = new();

        /// <summary>Pids that survive being signalled, however often.</summary>
        private readonly HashSet<int> _unkillable = new();

        public List<ManagedProcessRecord> Signalled { get; } = new();

        public FakeHost Alive(int pid, DateTime startTime)
        {
            _live[pid] = startTime;
            return this;
        }

        public FakeHost Unkillable(int pid, DateTime startTime)
        {
            _unkillable.Add(pid);
            return Alive(pid, startTime);
        }

        public DateTime? StartTimeOf(int pid) => _live.TryGetValue(pid, out var t) ? t : null;

        public void Kill(ManagedProcessRecord record)
        {
            Signalled.Add(record);

            if (!_unkillable.Contains(record.Pid))
                _live.Remove(record.Pid);
        }

        /// <summary>The process finally exits — an operator killed it, or it simply finished.</summary>
        public void Dies(int pid) { _unkillable.Remove(pid); _live.Remove(pid); }
    }

    /// <summary>Zero confirm wait: one re-probe, no sleeping, since the fake host is synchronous.</summary>
    private LeftoverSweepResult Sweep(FakeHost host) =>
        ManagedProcessRegistry.KillLeftovers(Path_, host.StartTimeOf, host.Kill,
                                             confirmWait: TimeSpan.Zero);

    [Fact]
    public void KillLeftovers_KillsARecordWhosePidAndStartTimeBothStillMatch()
    {
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var host = new FakeHost().Alive(4242, Launched);
        var result = Sweep(host);

        Assert.Equal(1, result.Killed);
        Assert.Equal(4242, Assert.Single(host.Signalled).Pid);
    }

    [Fact]
    public void KillLeftovers_StillKillsWhenTheTwoReadingsDisagreeByMilliseconds()
    {
        // THE Linux test, for the records that have no exact token. The recorded time is read by
        // the Relay that launched the job; the probed one by the Relay sweeping after it crashed.
        // On Linux .NET derives Process.StartTime from a per-process cached BootTime
        // (CLOCK_REALTIME_COARSE - CLOCK_BOOTTIME), and the coarse clock is quantised to a kernel
        // tick, so two independent samples of one process differ by 1-4 ms. Under exact equality
        // the sweep would skip every record and report 0: every orphan keeps its GPU, silently.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var host = new FakeHost().Alive(4242, Launched.AddMilliseconds(4));

        Assert.Equal(1, Sweep(host).Killed);
        Assert.Single(host.Signalled);
    }

    [Theory]
    [InlineData(-4)]      // the probe can land either side: the two clocks are independent
    [InlineData(4)]
    [InlineData(-200)]    // measured drift is 1-4 ms; 200 ms is already far past realistic slew
    [InlineData(200)]
    public void KillLeftovers_ToleratesADisagreementOnEitherSide(int millisecondsOff)
    {
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        Assert.Equal(1, Sweep(new FakeHost().Alive(4242, Launched.AddMilliseconds(millisecondsOff)))
                            .Killed);
    }

    [Fact]
    public void KillLeftovers_SkipsRecordsWhoseStartTimeNoLongerMatches()
    {
        // Pids are recycled. Killing on pid alone could terminate an unrelated process that
        // happened to inherit the number after a crash.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var host = new FakeHost().Alive(4242, Launched.AddMinutes(1));   // live, different process
        var result = Sweep(host);

        Assert.Equal(0, result.Killed);
        Assert.Empty(host.Signalled);                          // nothing was signalled at all
        Assert.Empty(result.Unconfirmed);                      // and it is not held against us
    }

    [Fact]
    public void KillLeftovers_SkipsAProcessStartedJustOutsideTheTolerance()
    {
        // Pins the boundary rather than only a comfortably-distant time, so the tolerance cannot be
        // widened to "any live pid" without a test noticing.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var justOutside = Launched + ManagedProcessRegistry.StartTimeTolerance +
                          TimeSpan.FromMilliseconds(1);

        var host = new FakeHost().Alive(4242, justOutside);

        Assert.Equal(0, Sweep(host).Killed);
        Assert.Empty(host.Signalled);
    }

    [Fact]
    public void KillLeftovers_SkipsRecordsWithNoLiveProcess()
    {
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var host = new FakeHost();                             // nothing alive at all
        var result = Sweep(host);

        Assert.Equal(0, result.Killed);
        Assert.Empty(host.Signalled);
        Assert.Empty(result.Unconfirmed);
    }

    [Fact]
    public void KillLeftovers_KillsOnlyTheMatchingRecordsOfAMixedFile()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(At(Launched, pid: 111, pgid: 111, jobId: 1));
        registry.Record(At(Launched, pid: 222, pgid: 222, jobId: 2));
        registry.Record(At(Launched, pid: 333, pgid: 333, jobId: 3));

        var host = new FakeHost()
            .Alive(111, Launched.AddMilliseconds(2))           // ours, read by a different process
            .Alive(333, Launched.AddHours(3));                 // pid recycled into somebody else's
        // 222 is simply gone.

        var result = Sweep(host);

        Assert.Equal(1, result.Killed);
        Assert.Equal(new[] { 111 }, host.Signalled.Select(r => r.Pid));
    }

    [Fact]
    public void KillLeftovers_PassesANullPgidThrough_RatherThanSubstitutingThePid()
    {
        // The macOS interlock, at the layer that reconstructs a kill from a file. A null pgid means
        // the process was in *Relay's own* group; turning it into kill(-pgid) here would kill the
        // Relay that is starting up. There is no Process handle at this point, so nothing else
        // could catch a pgid invented on the way in.
        new ManagedProcessRegistry(Path_).Record(At(Launched, pgid: null));

        var host = new FakeHost().Alive(4242, Launched);
        Sweep(host);

        Assert.Null(Assert.Single(host.Signalled).Pgid);
    }

    [Fact]
    public void KillLeftovers_ForgetsARecordWhoseProcessIsConfirmedGone()
    {
        // Both shapes: never there in the first place, and killed and confirmed. Keeping either
        // would put a pid the kernel has since recycled in range of the next sweep.
        new ManagedProcessRegistry(Path_).Record(At(Launched, pid: 111, pgid: 111, jobId: 1));
        new ManagedProcessRegistry(Path_).Record(At(Launched, pid: 222, pgid: 222, jobId: 2));

        var result = Sweep(new FakeHost().Alive(222, Launched));

        Assert.Equal(1, result.Killed);
        Assert.Empty(result.Unconfirmed);
        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }

    [Fact]
    public void KillLeftovers_RetainsAProcessItCouldNotConfirmDead_RatherThanCountingItKilled()
    {
        // The defect. Invoking the kill callback was treated as success: the production kill path
        // suppresses both the group-signal error and the fallback's, killed was incremented
        // regardless, and the file was then cleared unconditionally. Relay would go on to admit a
        // job onto the GPU the survivor is still using, with nothing left on disk to find it by.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var host = new FakeHost().Unkillable(4242, Launched);
        var result = Sweep(host);

        Assert.Single(host.Signalled);                         // it really was signalled
        Assert.Equal(0, result.Killed);                        // and it really did not die
        Assert.Equal(4242, Assert.Single(result.Unconfirmed).Pid);

        // Retained on disk, so the next startup has something to sweep.
        Assert.Equal(4242, Assert.Single(new ManagedProcessRegistry(Path_).Load()).Pid);
    }

    [Fact]
    public void KillLeftovers_RetainsOnlyTheSurvivors_NotTheWholeFile()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(At(Launched, pid: 111, pgid: 111, jobId: 1));
        registry.Record(At(Launched, pid: 222, pgid: 222, jobId: 2));

        var host = new FakeHost().Alive(111, Launched).Unkillable(222, Launched);
        var result = Sweep(host);

        Assert.Equal(1, result.Killed);
        Assert.Equal(222, Assert.Single(result.Unconfirmed).Pid);
        Assert.Equal(222, Assert.Single(new ManagedProcessRegistry(Path_).Load()).Pid);
    }

    [Fact]
    public void TryContain_ConfirmsASurvivorOnceItFinallyDies()
    {
        // The self-heal. A survivor that resisted the startup sweep and then exited — the job
        // finished, an operator killed it — must be confirmable without restarting Relay, or one
        // failed kill wedges the host permanently.
        var record = At(Launched);
        var host = new FakeHost().Unkillable(4242, Launched);

        Assert.False(ManagedProcessRegistry.TryContain(record, host.StartTimeOf, _ => null,
                                                       host.Kill, TimeSpan.Zero));

        host.Dies(4242);

        Assert.True(ManagedProcessRegistry.TryContain(record, host.StartTimeOf, _ => null,
                                                      host.Kill, TimeSpan.Zero));
    }

    [Fact]
    public void TryContain_IsNotConfirmedWhileAnythingIsStillInTheGroup()
    {
        // The leader going is not the tree going. A submission script's bash exits while the mpirun
        // ranks it started are still in the group setsid gave us, and a descendant in
        // uninterruptible sleep outlives the leader by definition. Confirming on the leader's
        // identity alone dropped the record and reopened managed admission onto a GPU that was
        // still occupied.
        var host = new FakeHost().Alive(4242, Launched);
        var record = At(Launched);                       // pid 4242, pgid 4242

        bool groupEmpty = false;

        Assert.False(ManagedProcessRegistry.TryContain(record, host.StartTimeOf, _ => null,
                                                       host.Kill, TimeSpan.Zero,
                                                       groupIsEmpty: _ => groupEmpty));

        // The kill landed and the leader really is gone — and it is still not containment.
        Assert.Single(host.Signalled);
        Assert.Null(host.StartTimeOf(4242));

        groupEmpty = true;                               // the last descendant finally exits

        Assert.True(ManagedProcessRegistry.TryContain(record, host.StartTimeOf, _ => null,
                                                      host.Kill, TimeSpan.Zero,
                                                      groupIsEmpty: _ => groupEmpty));

        // Nothing was signalled the second time: the pid is not ours any more, and it may since
        // have been recycled into somebody else's.
        Assert.Single(host.Signalled);
    }

    [Fact]
    public void TryContain_OnARecordWhosePidWasRecycled_SignalsNothingAtAll()
    {
        // The daemon's retry (RetryContainment) calls TryContain once per reap tick, for as long
        // as a survivor is retained, and it has no identity check of its own — the sweep's gate at
        // the top of KillLeftovers never runs on that path. So a survivor that exits between two
        // ticks, with its pid recycled into somebody else's process, arrives here still matching
        // nothing. Signalling before probing meant kill(-pgid) on a group that is gone, ESRCH,
        // and then — KillRecord passes hasExited: () => false — .NET's tree walk SIGKILLing that
        // stranger *and its entire tree*. Probing first is the whole fix, so the assertion that
        // matters is not the verdict but that no signal was issued.
        var record = At(Launched);
        var host = new FakeHost().Alive(4242, Launched.AddHours(1));   // recycled: not ours

        Assert.True(ManagedProcessRegistry.TryContain(record, host.StartTimeOf, _ => null,
                                                      host.Kill, TimeSpan.Zero));

        Assert.Empty(host.Signalled);
    }

    [Fact]
    public void TryContain_OnARecordWithAnExactToken_AlsoProbesBeforeSignalling()
    {
        // The Linux identity, same rule. One jiffy apart is a different process.
        var record = new ManagedProcessRecord(1, 1, 1, 4242, 4242, Launched.Ticks,
                                              StartToken: "boot-a:900");
        var host = new FakeHost().Alive(4242, Launched);

        Assert.True(ManagedProcessRegistry.TryContain(
            record,
            startTimeOf: _ => throw new InvalidOperationException(
                "a record with an exact token must not consult the start-time fallback"),
            startTokenOf: _ => "boot-a:901",
            kill: host.Kill,
            confirmWait: TimeSpan.Zero));

        Assert.Empty(host.Signalled);
    }

    [Fact]
    public void TryContain_StillSignalsAProcessThatIsStillOurs()
    {
        // The other half: probing first must not turn containment into a no-op for a real leftover.
        var record = At(Launched);
        var host = new FakeHost().Alive(4242, Launched);

        Assert.True(ManagedProcessRegistry.TryContain(record, host.StartTimeOf, _ => null,
                                                      host.Kill, TimeSpan.Zero));

        Assert.Equal(4242, Assert.Single(host.Signalled).Pid);
    }

    [Fact]
    public void KillLeftovers_OnAnAbsentFile_IsANoOp()
    {
        Assert.Equal(0, ManagedProcessRegistry.KillLeftovers(
            Path_, _ => throw new InvalidOperationException("nothing should be probed")).Killed);
    }

    #endregion

    #region Start-time identity across processes

    [Fact]
    public void LiveProcessStartTime_AnswersForThisProcessAndNullForAnImpossiblePid()
    {
        Assert.NotNull(ManagedProcessRegistry.LiveProcessStartTime(Environment.ProcessId));
        Assert.Null(ManagedProcessRegistry.LiveProcessStartTime(-1));
    }

    [Fact]
    public void LiveProcessStartTime_IsUtc_NotLocalTicksMislabelled()
    {
        // Process.StartTime is DateTimeKind.Local. Storing its raw ticks and probing UTC ticks (or
        // the reverse) is a whole-timezone-offset error that no same-machine, same-day test would
        // catch unless it is asserted directly — and a DST change between crash and restart would
        // introduce it even on a machine that is UTC today.
        var utc = ManagedProcessRegistry.LiveProcessStartTime(Environment.ProcessId);

        Assert.NotNull(utc);
        Assert.Equal(DateTimeKind.Utc, utc.Value.Kind);
        Assert.Equal(utc.Value.Ticks,
                     ManagedProcessRegistry.UtcTicksOf(
                         Process.GetProcessById(Environment.ProcessId).StartTime));

        // And the value really is this process's start, not an epoch or a zero.
        Assert.InRange(utc.Value, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
    }

    [Fact]
    public void OnLinux_TheStartTokenIsExact_AndAnUnrelatedProgramReadsTheSameOne()
    {
        // The deployment platform's identity, and the reason it needs no tolerance at all: field 22
        // of /proc/<pid>/stat is an integer the kernel stores and hands back verbatim, not a value
        // derived from .NET's per-process cached BootTime. `cat` is a genuinely separate program
        // asking the kernel independently, and it reads the identical number.
        if (!OperatingSystem.IsLinux())
            return;                         // /proc is the mechanism; there is nothing to check here

        using var child = StartSleeper();

        try
        {
            var token = ManagedProcessRegistry.StartTokenOf(child.Id);
            Assert.False(string.IsNullOrEmpty(token));

            Assert.Equal(token, StartTokenAccordingToCat(child.Id));

            // And the sweep believes it even when the *fallback* identity is hours out — which is
            // the point: the token decides, so no tolerance is consulted.
            new ManagedProcessRegistry(Path_).Record(
                new ManagedProcessRecord(1, 1, 1, child.Id, null,
                                         StartTimeTicks: Launched.Ticks, StartToken: token));

            // A real kill, and the sweep confirms it before reporting it: the token stops
            // resolving the moment the process is reaped.
            Assert.Equal(1, ManagedProcessRegistry.KillLeftovers(
                Path_, _ => throw new InvalidOperationException(
                    "the exact token must not fall back to start times"),
                kill: r => { try { Process.GetProcessById(r.Pid).Kill(true); } catch { } }).Killed);
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { }
        }
    }

    [Fact]
    public void AStartTokenThatDoesNotMatch_IsNotOurProcess_HoweverCloseTheStartTimes()
    {
        // A recycled pid whose new process started in the same millisecond used to be accepted, and
        // since the stored pgid equals the pid the sweep then SIGKILLed that stranger's whole
        // process group. With an exact token there is no window to hit.
        new ManagedProcessRegistry(Path_).Record(
            new ManagedProcessRecord(1, 1, 1, 4242, 4242, Launched.Ticks, StartToken: "boot-a:900"));

        Assert.Equal(0, ManagedProcessRegistry.KillLeftovers(
            Path_,
            startTimeOf: _ => Launched,                        // identical to the recorded time
            kill: _ => Assert.Fail("signalled a process whose exact identity did not match"),
            startTokenOf: _ => "boot-a:901").Killed);          // one jiffy later: somebody else
    }

    [Fact]
    public void ARecordWithNoStartToken_StillUsesTheStartTimeFallback()
    {
        // Records written by an older Relay, and every non-Linux host.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var host = new FakeHost().Alive(4242, Launched.AddMilliseconds(3));

        Assert.Equal(1, ManagedProcessRegistry.KillLeftovers(
            Path_, host.StartTimeOf, host.Kill,
            startTokenOf: _ => throw new InvalidOperationException(
                "a record with no token has no exact identity to check"),
            confirmWait: TimeSpan.Zero).Killed);
    }

    [Fact]
    public void TheStartTimeFallbackTolerance_IsSizedToMeasuredDrift_NotToPsOutput()
    {
        // Measured Linux cross-process drift is a kernel tick, 1-4 ms, plus realtime slew. The old
        // five seconds was sized to a test that read whole-second timestamps out of `ps`; that test
        // is gone, and the tolerance now only has to cover the drift it was always about.
        Assert.InRange(ManagedProcessRegistry.StartTimeTolerance,
                       TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public void AStartTimeRecordedHere_StillMatchesWhenADifferentProcessReadsIt()
    {
        // The fallback path's cross-process check, for the hosts that have no /proc. .NET's own
        // reading is what both sides use there, and on macOS — the only such host in practice — the
        // kernel hands back an absolute p_starttime, so two reads agree exactly.
        using var child = StartSleeper();

        try
        {
            var recorded = ManagedProcessRegistry.UtcTicksOf(child.StartTime);
            var reread = ManagedProcessRegistry.LiveProcessStartTime(child.Id);
            Assert.NotNull(reread);

            var drift = TimeSpan.FromTicks(Math.Abs(reread.Value.Ticks - recorded));

            Assert.True(drift <= ManagedProcessRegistry.StartTimeTolerance,
                        $"a re-read of this start time was {drift.TotalMilliseconds:F0} ms away " +
                        $"from ours, outside the {ManagedProcessRegistry.StartTimeTolerance
                            .TotalMilliseconds:F0} ms tolerance");

            // And the sweep, driven by a reading offset by realistic drift, actually kills.
            new ManagedProcessRegistry(Path_).Record(
                new ManagedProcessRecord(1, 1, 1, child.Id, null, recorded));

            var host = new FakeHost().Alive(child.Id, reread.Value.AddMilliseconds(4));

            Assert.Equal(1, ManagedProcessRegistry.KillLeftovers(
                Path_, host.StartTimeOf, host.Kill, confirmWait: TimeSpan.Zero).Killed);
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static Process StartSleeper()
    {
        var child = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", "sleep 20" },
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        });
        Assert.NotNull(child);
        return child;
    }

    /// <summary>The child's start token as an unrelated program reads it, built from the same two
    /// /proc files but with the per-pid one fetched by `cat` rather than by us.</summary>
    private static string StartTokenAccordingToCat(int pid)
    {
        using var cat = Process.Start(new ProcessStartInfo("/bin/cat", $"/proc/{pid}/stat")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        });
        Assert.NotNull(cat);

        var stat = cat.StandardOutput.ReadToEnd();
        cat.WaitForExit();

        // comm is parenthesised and may contain spaces, so split after the last ')'; field 22
        // (starttime) is then index 19.
        var fields = stat[(stat.LastIndexOf(')') + 1)..]
            .Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

        var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();

        return $"{bootId}:{fields[19]}";
    }

    #endregion
}
