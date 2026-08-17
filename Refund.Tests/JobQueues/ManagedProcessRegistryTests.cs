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

    [Fact]
    public void KillLeftovers_KillsARecordWhosePidAndStartTimeBothStillMatch()
    {
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var killed = new List<ManagedProcessRecord>();
        var count = ManagedProcessRegistry.KillLeftovers(
            Path_, startTimeOf: _ => Launched, kill: killed.Add);

        Assert.Equal(1, count);
        Assert.Equal(4242, Assert.Single(killed).Pid);
    }

    [Fact]
    public void KillLeftovers_StillKillsWhenTheTwoReadingsDisagreeByMilliseconds()
    {
        // THE Linux test. The recorded time is read by the Relay that launched the job; the probed
        // one by the Relay sweeping after it crashed. On Linux .NET derives Process.StartTime from
        // a per-process cached BootTime (CLOCK_REALTIME_COARSE - CLOCK_BOOTTIME), and the coarse
        // clock is quantised to a kernel tick — so two independent samples of one process differ by
        // 1-4 ms, i.e. 10,000-40,000 ticks. Under exact equality the sweep skips every record,
        // clears the file and reports 0: every orphan keeps its GPU, silently, on the deployment
        // platform only. No in-process test can see this, because in-process reads share the cache.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var killed = new List<ManagedProcessRecord>();
        var count = ManagedProcessRegistry.KillLeftovers(
            Path_, startTimeOf: _ => Launched.AddMilliseconds(4), kill: killed.Add);

        Assert.Equal(1, count);
        Assert.Single(killed);
    }

    [Theory]
    [InlineData(-4)]      // the probe can land either side: the two clocks are independent
    [InlineData(4)]
    [InlineData(-200)]    // measured drift is 1-4 ms; 200 ms is already far past realistic slew
    [InlineData(200)]
    public void KillLeftovers_ToleratesADisagreementOnEitherSide(int millisecondsOff)
    {
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        Assert.Equal(1, ManagedProcessRegistry.KillLeftovers(
            Path_, _ => Launched.AddMilliseconds(millisecondsOff), _ => { }));
    }

    [Fact]
    public void KillLeftovers_SkipsRecordsWhoseStartTimeNoLongerMatches()
    {
        // Pids are recycled. Killing on pid alone could terminate an unrelated process that
        // happened to inherit the number after a crash. The tolerance above is loose enough to
        // absorb two clock readings and nothing more: a minute out is a different process.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var killed = new List<ManagedProcessRecord>();
        var count = ManagedProcessRegistry.KillLeftovers(
            Path_, startTimeOf: _ => Launched.AddMinutes(1),   // live, but a different process
            kill: killed.Add);

        Assert.Equal(0, count);
        Assert.Empty(killed);                                  // nothing was signalled at all
    }

    [Fact]
    public void KillLeftovers_SkipsAProcessStartedJustOutsideTheTolerance()
    {
        // Pins the boundary rather than only a comfortably-distant time, so the tolerance cannot be
        // widened to "any live pid" without a test noticing.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var justOutside = Launched + ManagedProcessRegistry.StartTimeTolerance +
                          TimeSpan.FromMilliseconds(1);

        Assert.Equal(0, ManagedProcessRegistry.KillLeftovers(
            Path_, _ => justOutside, _ => Assert.Fail("signalled a process outside the tolerance")));
    }

    [Fact]
    public void KillLeftovers_SkipsRecordsWithNoLiveProcess()
    {
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        var killed = new List<ManagedProcessRecord>();
        Assert.Equal(0, ManagedProcessRegistry.KillLeftovers(Path_, _ => null, killed.Add));
        Assert.Empty(killed);
    }

    [Fact]
    public void KillLeftovers_KillsOnlyTheMatchingRecordsOfAMixedFile()
    {
        var registry = new ManagedProcessRegistry(Path_);
        registry.Record(At(Launched, pid: 111, pgid: 111, jobId: 1));
        registry.Record(At(Launched, pid: 222, pgid: 222, jobId: 2));
        registry.Record(At(Launched, pid: 333, pgid: 333, jobId: 3));

        var killed = new List<int>();
        var count = ManagedProcessRegistry.KillLeftovers(
            Path_,
            startTimeOf: pid => pid switch
            {
                111 => Launched.AddMilliseconds(2),   // ours, read by a different process
                222 => null,                          // gone
                _   => Launched.AddHours(3),          // pid recycled into somebody else's process
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
        new ManagedProcessRegistry(Path_).Record(At(Launched, pgid: null));

        var killed = new List<ManagedProcessRecord>();
        ManagedProcessRegistry.KillLeftovers(Path_, _ => Launched, killed.Add);

        Assert.Null(Assert.Single(killed).Pgid);
    }

    [Fact]
    public void KillLeftovers_ClearsTheFileAfterSweeping()
    {
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        ManagedProcessRegistry.KillLeftovers(Path_, startTimeOf: _ => null);

        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }

    [Fact]
    public void KillLeftovers_ClearsTheFileEvenWhenItKilledSomething()
    {
        // Otherwise the same pids are swept again on the next boot, by which time they belong to
        // somebody else.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        ManagedProcessRegistry.KillLeftovers(Path_, _ => Launched, _ => { });

        Assert.Empty(new ManagedProcessRegistry(Path_).Load());
    }

    [Fact]
    public void KillLeftovers_OnAnAbsentFile_IsANoOp()
    {
        Assert.Equal(0, ManagedProcessRegistry.KillLeftovers(
            Path_, _ => throw new InvalidOperationException("nothing should be probed")));
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

            Assert.Equal(1, ManagedProcessRegistry.KillLeftovers(
                Path_, _ => throw new InvalidOperationException(
                    "the exact token must not fall back to start times"),
                _ => { }));
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
            startTokenOf: _ => "boot-a:901"));                 // one jiffy later: somebody else
    }

    [Fact]
    public void ARecordWithNoStartToken_StillUsesTheStartTimeFallback()
    {
        // Records written by an older Relay, and every non-Linux host.
        new ManagedProcessRegistry(Path_).Record(At(Launched));

        Assert.Equal(1, ManagedProcessRegistry.KillLeftovers(
            Path_, _ => Launched.AddMilliseconds(3), _ => { },
            startTokenOf: _ => throw new InvalidOperationException(
                "a record with no token has no exact identity to check")));
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

            Assert.Equal(1, ManagedProcessRegistry.KillLeftovers(
                Path_, _ => reread.Value.AddMilliseconds(4), _ => { }));
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
