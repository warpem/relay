using System.Diagnostics;
using System.Text.Json;
using Serilog;

namespace Refund.JobQueues;

/// <summary>One launched job, identified well enough to be killed after a Relay restart.</summary>
/// <param name="ProjectId">
/// Part of the job's identity, with SpaceId. Job.Id is allocated per space (Space.cs:190), so it
/// is not unique on a host: two spaces routinely both have a job 5. Keying on Job.Id alone would
/// make one space's launch drop the other's record, and one space's settle
/// <em>un-register a running process</em> belonging to the other.
/// </param>
/// <param name="Pgid">Null when the platform had no setsid; see SystemManagedProcess.Pgid.</param>
/// <param name="StartTimeTicks">
/// <b>UTC</b> ticks. Process.StartTime is DateTimeKind.Local, and a DST or timezone change between
/// the crash and the restart would shift a local value by an hour — enough to make the process
/// unrecognisable. The <em>fallback</em> identity, compared with a tolerance and only when
/// <paramref name="StartToken"/> is absent; see <see cref="ManagedProcessRegistry.StartTimeTolerance"/>.
/// </param>
/// <param name="StartToken">
/// The exact, tolerance-free process identity on Linux; null on every other platform and on records
/// written by an older Relay. See <see cref="ManagedProcessRegistry.StartTokenOf"/>.
/// </param>
public record ManagedProcessRecord(int ProjectId, int SpaceId, int JobId,
                                   int Pid, int? Pgid, long StartTimeTicks,
                                   string StartToken = null);

/// <summary>What the startup leftover sweep managed to do.</summary>
/// <param name="Killed">Processes signalled and then confirmed gone.</param>
/// <param name="Unconfirmed">
/// Processes that were ours, were signalled, and were <em>still there</em> when the sweep gave up
/// waiting. Each one may still be holding a GPU, so managed admission must report
/// <see cref="AdmissionResult.Busy"/> — not Reject — until it clears: the condition is transient
/// and the daemon retries the kill on every reap tick.
/// </param>
public record LeftoverSweepResult(int Killed, IReadOnlyList<ManagedProcessRecord> Unconfirmed);

/// <summary>
/// Persists which processes a managed queue launched, so leftovers from a crashed Relay can be
/// killed at the next startup.
/// </summary>
/// <remarks>
/// <para>
/// Graceful shutdown cannot cover SIGKILL or a hard crash, and an orphan holding a GPU makes every
/// later job on a single-GPU host wait or be rejected. Identity is pid <em>plus start time</em>:
/// pids are recycled, and killing on pid alone could take out an unrelated process.
/// </para>
/// <para>
/// A record's <see cref="ManagedProcessRecord.Pgid"/> must be read at the moment it is persisted
/// and re-read if it was still unresolved then — never captured inside the launch call. See the
/// remarks on <see cref="SystemManagedProcess.Pgid"/>: a read taken immediately after
/// <c>Process.Start</c> loses the race with the child's <c>setsid</c> every time on Linux, and a
/// null recorded here makes the sweep below a permanent no-op on exactly the platform that has
/// process groups.
/// </para>
/// </remarks>
public sealed class ManagedProcessRegistry
{
    private readonly string _path;
    private readonly object _sync = new();

    public ManagedProcessRegistry(string path) => _path = path;

    public IReadOnlyList<ManagedProcessRecord> Load()
    {
        lock (_sync)
            return LoadLocked();
    }

    /// <summary>
    /// Set the first time the file turns out to be present but unreadable. While it is set nothing
    /// is written: those bytes may describe a live orphan, and an empty load that then overwrote
    /// them would make that process invisible to this run <em>and</em> to every run after it.
    /// Never cleared — the file is left exactly as found, for inspection and for a later run to
    /// read again from scratch.
    /// </summary>
    private bool _unreadable;

    /// <summary>
    /// The records on disk, or an empty list. An absent file and an unreadable one both load as
    /// empty — Relay must start either way — but only the absent one is <em>believed</em>; see
    /// <see cref="_unreadable"/>.
    /// </summary>
    private List<ManagedProcessRecord> LoadLocked()
    {
        try
        {
            if (!File.Exists(_path))
                return new List<ManagedProcessRecord>();

            return JsonSerializer.Deserialize<List<ManagedProcessRecord>>(File.ReadAllText(_path))
                   ?? new List<ManagedProcessRecord>();
        }
        catch (Exception exc)
        {
            // A half-written file after a crash must never stop Relay from starting. It must not be
            // quietly discarded either: failing open here used to have the startup sweep replace it
            // with an empty list, so a leftover it described lost its last trace.
            if (!_unreadable)
                Log.ForContext<ManagedProcessRegistry>().Error(
                    exc, "The managed process registry at {Path} exists but could not be read. It " +
                         "may list processes left over from a previous run, which cannot now be " +
                         "found or killed. The file is being left untouched for inspection, and " +
                         "nothing will be written to it until Relay is restarted.", _path);

            _unreadable = true;
            return new List<ManagedProcessRecord>();
        }
    }

    /// <summary>
    /// Persist one launched process, replacing any earlier record for the same job — a job runs at
    /// most one process at a time, so an older one is by definition finished with.
    /// </summary>
    public void Record(ManagedProcessRecord record)
    {
        lock (_sync)
        {
            var all = LoadLocked();
            all.RemoveAll(r => IsSameJob(r, record.ProjectId, record.SpaceId, record.JobId));
            all.Add(record);
            SaveLocked(all);
        }
    }

    public void Forget(int projectId, int spaceId, int jobId)
    {
        lock (_sync)
        {
            var all = LoadLocked();
            if (all.RemoveAll(r => IsSameJob(r, projectId, spaceId, jobId)) == 0)
                return;                     // nothing to drop; do not rewrite the file for nothing

            SaveLocked(all);
        }
    }

    /// <summary>
    /// Drop exactly this record, and only if the file still holds it. Unlike
    /// <see cref="Forget(int, int, int)"/> this leaves a <em>later</em> record for the same job
    /// alone, which is what the failed-attach path needs: the run that displaced it may have
    /// recorded a live process under the same key in the meantime.
    /// </summary>
    public void Forget(ManagedProcessRecord record)
    {
        lock (_sync)
        {
            var all = LoadLocked();
            if (all.RemoveAll(r => IsSameJob(r, record.ProjectId, record.SpaceId, record.JobId) &&
                                   r.Pid == record.Pid) == 0)
                return;

            SaveLocked(all);
        }
    }

    /// <summary>All three parts, because Job.Id alone is only unique within its space.</summary>
    private static bool IsSameJob(ManagedProcessRecord record, int projectId, int spaceId, int jobId) =>
        record.ProjectId == projectId && record.SpaceId == spaceId && record.JobId == jobId;

    public void Clear()
    {
        lock (_sync)
            SaveLocked(new List<ManagedProcessRecord>());
    }

    /// <summary>Replace the whole file with <paramref name="records"/>. Used by the startup sweep,
    /// which keeps exactly the leftovers it could not confirm dead.</summary>
    public void ReplaceAll(IEnumerable<ManagedProcessRecord> records)
    {
        lock (_sync)
            SaveLocked(records.ToList());
    }

    /// <summary>
    /// Written to a sibling temp file and moved into place, so a crash mid-write leaves either the
    /// old file or the new one — never a truncated one that loses every other live job's record.
    /// </summary>
    private void SaveLocked(List<ManagedProcessRecord> records)
    {
        // Every write goes through here, so this one guard covers Record, Forget, Clear and the
        // startup sweep's ReplaceAll alike. What we would write is derived from a load that
        // returned empty because it failed, not because the file was empty.
        if (_unreadable)
            return;

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tmp = _path + ".tmp." + Environment.ProcessId;
        File.WriteAllText(tmp, JsonSerializer.Serialize(records,
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>
    /// How far apart two readings of one process's start time may be and still be believed to
    /// describe the same process. <b>Only consulted when a record has no
    /// <see cref="ManagedProcessRecord.StartToken"/></b> — i.e. off Linux, or for a record written
    /// by an older Relay.
    /// </summary>
    /// <remarks>
    /// <b>Exact tick equality does not hold across a restart on Linux, which is the deployment
    /// platform.</b> .NET computes Process.StartTime there as <c>BootTime + jiffiesSinceBoot</c>,
    /// where BootTime is cached per-process as <c>CLOCK_REALTIME_COARSE - CLOCK_BOOTTIME</c>. The
    /// coarse clock is quantised to a kernel tick, so two independent samples — one by the Relay
    /// that launched the job, one by the Relay that is sweeping after it crashed — differ by
    /// milliseconds, i.e. tens of thousands of ticks, plus any realtime slew in between. Requiring
    /// equality would make the sweep skip every record, clear the file and report nothing: the same
    /// silent, Linux-only, no-other-symptom shape as an eagerly-read pgid. It is invisible on macOS,
    /// where the kernel hands back an absolute p_starttime and the two reads agree exactly, and
    /// invisible to any single-process test, where both reads share the cached boot time.
    /// <para>
    /// <b>Sized to measured drift, and to nothing else.</b> The measurement above is 1-4 ms of
    /// tick quantisation plus whatever realtime slew NTP applied between the two samples; 250 ms is
    /// two orders of magnitude of headroom over that and still 20x tighter than the window a pid
    /// would have to be recycled into for an unrelated process to be mistaken for ours and have its
    /// group SIGKILLed. It is emphatically <em>not</em> sized to a test that reads whole-second
    /// timestamps out of <c>ps</c>: on Linux, which is where the drift lives, the exact
    /// <see cref="StartTokenOf"/> identity is used instead and no tolerance applies at all.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromMilliseconds(250);

    /// <summary>UTC ticks, for storing in a record; see ManagedProcessRecord.StartTimeTicks.</summary>
    public static long UtcTicksOf(DateTime startTime) => startTime.ToUniversalTime().Ticks;

    /// <summary>
    /// An exact, tolerance-free identity for a live process on Linux, or null anywhere else.
    /// Two different processes can never share one; one process always reads back the same one,
    /// from any process, at any later time in the same boot.
    /// </summary>
    /// <remarks>
    /// <c>"&lt;boot_id&gt;:&lt;starttime&gt;"</c>, where <c>starttime</c> is field 22 of
    /// <c>/proc/&lt;pid&gt;/stat</c> — the process's start in jiffies since boot, an integer the
    /// kernel stores and hands back verbatim to every reader. Unlike Process.StartTime it is not
    /// derived from a per-process cached boot time, so it needs no tolerance: the Relay that
    /// launched the job and the Relay sweeping after it crashed read the identical number.
    /// <para>
    /// Paired with the boot id because jiffies-since-boot is only unique <em>within</em> a boot, and
    /// a leftover file survives a reboot. <c>/proc/sys/kernel/random/boot_id</c> is a UUID generated
    /// once per boot and is stable for its whole life — unlike <c>/proc/stat</c>'s <c>btime</c>,
    /// which the kernel recomputes as <c>walltime - uptime</c> on every read and which therefore
    /// moves by a second under rounding and NTP slew, i.e. is exactly the kind of value this is
    /// meant to replace.
    /// </para>
    /// </remarks>
    public static string StartTokenOf(int pid)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return null;

            var boot = BootId;
            if (boot == null)
                return null;

            var stat = File.ReadAllText($"/proc/{pid}/stat");

            // comm (field 2) is parenthesised and may itself contain spaces and parentheses, so
            // the only safe split point is the *last* closing paren. Everything after it is
            // fields 3..52, whitespace-separated, so field 22 (starttime) is index 19.
            int close = stat.LastIndexOf(')');
            if (close < 0)
                return null;

            var fields = stat[(close + 1)..].Split((char[])null, StringSplitOptions.RemoveEmptyEntries);

            // Field 3 (index 0) is the state, and 'Z' is a zombie: the process is over and holds no
            // cores, memory or GPU — only a slot in its parent's wait queue. Reporting it as still
            // present would leave the sweep unable to confirm any kill it made, since a leftover
            // whose parent Relay crashed is a zombie until init gets round to reaping it.
            if (fields.Length > 0 && fields[0] == "Z")
                return null;

            return fields.Length > 19 && long.TryParse(fields[19], out var startJiffies)
                       ? $"{boot}:{startJiffies}"
                       : null;
        }
        catch
        {
            // No /proc entry means no such process, which the liveness probe reports separately;
            // an unreadable one means the fallback identity has to carry it.
            return null;
        }
    }

    /// <summary>Constant for the life of the boot, so read once.</summary>
    private static readonly string BootId = ReadBootId();

    private static string ReadBootId()
    {
        try
        {
            return OperatingSystem.IsLinux()
                       ? File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim()
                       : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Whether the pid in <paramref name="record"/> is still occupied by the very process that
    /// record describes, rather than by an unrelated one the kernel recycled the number into.
    /// </summary>
    /// <remarks>
    /// The exact token wins outright where there is one on both sides: it is reproducible to the
    /// jiffy, so a mismatch is a different process and no amount of tolerance should rescue it.
    /// Only a record with no token — an older file, or a non-Linux host — falls back to comparing
    /// start times within <see cref="StartTimeTolerance"/>.
    /// </remarks>
    private static bool IsStillTheSameProcess(ManagedProcessRecord record,
                                              Func<int, DateTime?> startTimeOf,
                                              Func<int, string> startTokenOf)
    {
        if (!string.IsNullOrEmpty(record.StartToken))
            return record.StartToken == startTokenOf(record.Pid);

        var actual = startTimeOf(record.Pid);

        return actual != null &&
               Math.Abs(actual.Value.Ticks - record.StartTimeTicks) <= StartTimeTolerance.Ticks;
    }

    /// <summary>
    /// How long a killed leftover is given to actually disappear before the sweep gives up on it
    /// and reports it as a survivor. SIGKILL is delivered synchronously and the process is torn
    /// down on the next scheduling opportunity, so this only ever elapses when the signal did not
    /// land at all — a permission failure, or an uninterruptible-sleep task in D state.
    /// </summary>
    public static readonly TimeSpan DefaultConfirmWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ConfirmPollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Kills every recorded process that is still alive and still the same process, waits to see
    /// each one actually go, and rewrites the file with exactly the ones it could not confirm dead.
    /// Call once at startup, before any job is admitted.
    /// </summary>
    /// <remarks>
    /// <b>Invoking the kill is not evidence that it worked.</b> The production kill path suppresses
    /// both the group-signal error and the fallback's, so counting a kill as a success and clearing
    /// the file unconditionally meant a failed kill left compute running on the host while Relay
    /// went on to hand its GPU to the next job. Only a record whose process is confirmed gone — or
    /// whose identity no longer matches, so it was never ours — is dropped;
    /// <see cref="LeftoverSweepResult.Unconfirmed"/> carries the rest, and the caller is expected to
    /// keep managed admission Busy until they clear.
    /// </remarks>
    /// <param name="startTimeOf">
    /// Start time of the live process with this pid <b>in UTC</b>, or null if no such process
    /// exists. Injected so the recycling logic is testable without spawning anything.
    /// </param>
    public static LeftoverSweepResult KillLeftovers(string path, Func<int, DateTime?> startTimeOf) =>
        KillLeftovers(path, startTimeOf, KillRecord,
                      groupIsEmpty: SystemManagedProcess.GroupIsEmpty);

    /// <summary>Overload with the kill injected, so the identity rules can be tested without
    /// putting real pids in range of a real SIGKILL.</summary>
    /// <param name="startTokenOf">
    /// The exact identity probe; see <see cref="StartTokenOf"/>. Only consulted for a record that
    /// carries a token of its own, so a test whose records have none never reaches it.
    /// </param>
    /// <param name="confirmWait">
    /// How long to keep re-probing a killed process before declaring it a survivor; see
    /// <see cref="DefaultConfirmWait"/>. Zero means one re-probe and no waiting.
    /// </param>
    /// <param name="groupIsEmpty">
    /// The group-emptiness probe; see <see cref="SystemManagedProcess.GroupIsEmpty"/>. Left unset
    /// only by the injected-kill callers, i.e. tests, whose pgids name no real group and must not
    /// be handed to a syscall: null answers "empty", so those tests see the leader-identity rule
    /// alone. Every production entry point passes the real probe.
    /// </param>
    internal static LeftoverSweepResult KillLeftovers(string path, Func<int, DateTime?> startTimeOf,
                                                      Action<ManagedProcessRecord> kill,
                                                      Func<int, string> startTokenOf = null,
                                                      TimeSpan? confirmWait = null,
                                                      Func<int, bool> groupIsEmpty = null)
    {
        startTokenOf ??= StartTokenOf;

        var registry = new ManagedProcessRegistry(path);
        int killed = 0;
        var unconfirmed = new List<ManagedProcessRecord>();

        foreach (var record in registry.Load())
        {
            bool stillOurs;
            try { stillOurs = IsStillTheSameProcess(record, startTimeOf, startTokenOf); }
            catch { continue; }                                  // unreadable: leave it alone

            // Gone, or the pid was recycled into somebody else's process. Either way not ours to
            // kill, and not ours to keep: the stored pgid equals the pid, so a record still in the
            // file after the identity stopped matching would put a stranger's group in range of
            // every future sweep.
            if (!stillOurs)
                continue;

            if (TryContain(record, startTimeOf, startTokenOf, kill,
                           confirmWait ?? DefaultConfirmWait, groupIsEmpty))
                killed++;
            else
                unconfirmed.Add(record);
        }

        // Exactly the survivors, and nothing else. Clearing unconditionally is what let a failed
        // kill vanish; keeping a record whose identity no longer matches is what puts a recycled
        // pid in range of the next sweep.
        registry.ReplaceAll(unconfirmed);

        return new LeftoverSweepResult(killed, unconfirmed);
    }

    /// <summary>
    /// Check one leftover is still ours, signal it if so, and wait briefly to see it go. True once
    /// its process is confirmed gone — or its identity no longer matches, in which case nothing is
    /// signalled at all, because it was never ours in the first place.
    /// </summary>
    /// <remarks>
    /// Re-used by the daemon's reap tick to retry a survivor, so a process that only dies later
    /// releases the admission block without needing a Relay restart. That retry has no identity
    /// check of its own, which is why this one has to come before the signal.
    /// </remarks>
    public static bool TryContain(ManagedProcessRecord record) =>
        TryContain(record, LiveProcessStartTime, StartTokenOf, KillRecord, DefaultConfirmWait,
                   SystemManagedProcess.GroupIsEmpty);

    /// <summary>
    /// The reap-tick retry. Identical containment, but with a much shorter wait: it runs under the
    /// executor's host-wide lock on every daemon tick, and anything it misses is simply tried again
    /// a tick later.
    /// </summary>
    public static bool RetryContainment(ManagedProcessRecord record) =>
        TryContain(record, LiveProcessStartTime, StartTokenOf, KillRecord,
                   TimeSpan.FromMilliseconds(100), SystemManagedProcess.GroupIsEmpty);

    /// <param name="groupIsEmpty">
    /// See the parameter of the same name on
    /// <see cref="KillLeftovers(string, Func{int, DateTime?}, Action{ManagedProcessRecord}, Func{int, string}, TimeSpan?, Func{int, bool})"/>.
    /// </param>
    internal static bool TryContain(ManagedProcessRecord record,
                                    Func<int, DateTime?> startTimeOf,
                                    Func<int, string> startTokenOf,
                                    Action<ManagedProcessRecord> kill,
                                    TimeSpan confirmWait,
                                    Func<int, bool> groupIsEmpty = null)
    {
        // Identity before signal, and not the other way round. KillLeftovers gates on identity
        // before it calls in here, but <see cref="RetryContainment"/> does not: the daemon calls
        // it once per reap tick for as long as a survivor is retained. A survivor that exits
        // between two ticks can have its pid recycled by the kernel, and signalling first meant
        // kill(-pgid, SIGKILL) against a group that no longer exists — ESRCH, a nonzero return,
        // which falls through KillTree's interlock into the fallback; and KillRecord passes
        // hasExited: () => false, so that fallback is
        // Process.GetProcessById(pid).Kill(entireProcessTree: true) on a stranger and every child
        // it has. The probe only ran afterwards, when the damage was already done.
        //
        // A record whose identity no longer matches is contained by definition: nothing of ours
        // is left at that pid, which is exactly what the caller needs to know to drop it.
        try
        {
            // Nothing of ours is left at that pid, so there is nothing we may safely signal: the
            // kernel could have recycled the pid, and with it the group. Whether the *work* is
            // over is a separate question, and the one below answers it.
            if (!IsStillTheSameProcess(record, startTimeOf, startTokenOf))
                return GroupIsGone(record, groupIsEmpty);
        }
        catch
        {
            // An unreadable probe is not a confirmation, and it is not a licence to signal
            // either — the same verdict KillLeftovers reaches when the probe throws on it.
            return false;
        }

        try { kill(record); }
        catch { /* the probe below is the only verdict that counts */ }

        var start = Stopwatch.GetTimestamp();

        while (true)
        {
            try
            {
                if (!IsStillTheSameProcess(record, startTimeOf, startTokenOf) &&
                    GroupIsGone(record, groupIsEmpty))
                    return true;
            }
            catch
            {
                // An unreadable probe is not a confirmation. Retaining the record costs a Busy
                // queue that self-heals; dropping it costs a GPU nothing is tracking.
                return false;
            }

            if (Stopwatch.GetElapsedTime(start) >= confirmWait)
                return false;

            Thread.Sleep(ConfirmPollInterval < confirmWait ? ConfirmPollInterval : confirmWait);
        }
    }

    /// <summary>
    /// Whether the group this record owned still has a member. The leader's identity disappearing
    /// is not the same thing: a descendant — an mpirun rank, a <c>( ... ) &amp;</c> subshell, a task
    /// in uninterruptible sleep — can outlive the leader in the same group while still holding the
    /// GPU the record exists to account for.
    /// </summary>
    /// <remarks>
    /// A record with no group of ours has no group to ask about, and the leader check is then all
    /// there is; that is the macOS path and the path for records written by an older Relay.
    /// Reporting a non-empty group as not-contained is safe to be strict about because the caller
    /// keeps the record, keeps managed admission Busy, and retries on the next daemon tick — so it
    /// resolves itself once the last descendant exits, rather than wedging.
    /// </remarks>
    private static bool GroupIsGone(ManagedProcessRecord record, Func<int, bool> groupIsEmpty) =>
        groupIsEmpty == null || record.Pgid is not { } pgid || pgid <= 1 || groupIsEmpty(pgid);

    /// <summary>
    /// The real kill. Goes through <see cref="SystemManagedProcess.KillTree(int, int?, Action, Func{bool})"/>
    /// because there is no Process handle here — only a pid and, on a platform that has process
    /// groups, a group id. A null <see cref="ManagedProcessRecord.Pgid"/> is passed straight
    /// through and must stay null: it means the process was in <em>Relay's own</em> group, and
    /// turning it into <c>kill(-pgid)</c> would take down the Relay that is starting up.
    /// </summary>
    private static void KillRecord(ManagedProcessRecord record) =>
        SystemManagedProcess.KillTree(
            record.Pid, record.Pgid,
            fallbackKill: () => KillByPid(record.Pid),
            hasExited: () => false);       // liveness was just established by the start-time probe

    /// <summary>
    /// Default start-time probe for production use. Returns UTC: Process.StartTime is Local, and a
    /// timezone or DST change between the crash and the restart would otherwise move it by an hour.
    /// </summary>
    public static DateTime? LiveProcessStartTime(int pid)
    {
        try { return Process.GetProcessById(pid).StartTime.ToUniversalTime(); }
        catch { return null; }
    }

    private static void KillByPid(int pid)
    {
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { }
    }
}
