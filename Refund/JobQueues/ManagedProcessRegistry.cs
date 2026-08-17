using System.Diagnostics;
using System.Text.Json;

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
/// unrecognisable. Compared with a tolerance, never for equality; see
/// <see cref="ManagedProcessRegistry.StartTimeTolerance"/>.
/// </param>
public record ManagedProcessRecord(int ProjectId, int SpaceId, int JobId,
                                   int Pid, int? Pgid, long StartTimeTicks);

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

    private List<ManagedProcessRecord> LoadLocked()
    {
        try
        {
            if (!File.Exists(_path))
                return new List<ManagedProcessRecord>();

            return JsonSerializer.Deserialize<List<ManagedProcessRecord>>(File.ReadAllText(_path))
                   ?? new List<ManagedProcessRecord>();
        }
        catch
        {
            // A half-written file after a crash must never stop Relay from starting.
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

    /// <summary>All three parts, because Job.Id alone is only unique within its space.</summary>
    private static bool IsSameJob(ManagedProcessRecord record, int projectId, int spaceId, int jobId) =>
        record.ProjectId == projectId && record.SpaceId == spaceId && record.JobId == jobId;

    public void Clear()
    {
        lock (_sync)
            SaveLocked(new List<ManagedProcessRecord>());
    }

    /// <summary>
    /// Written to a sibling temp file and moved into place, so a crash mid-write leaves either the
    /// old file or the new one — never a truncated one that loses every other live job's record.
    /// </summary>
    private void SaveLocked(List<ManagedProcessRecord> records)
    {
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
    /// describe the same process.
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
    /// A few seconds costs nothing against pid recycling: for a stale record to be mistaken for a
    /// live process, the kernel would have to have recycled that exact pid into a process started
    /// within this window of the recorded launch.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(5);

    /// <summary>UTC ticks, for storing in a record; see ManagedProcessRecord.StartTimeTicks.</summary>
    public static long UtcTicksOf(DateTime startTime) => startTime.ToUniversalTime().Ticks;

    /// <summary>
    /// Kills every recorded process that is still alive and still the same process, then clears the
    /// file. Returns how many were killed. Call once at startup, before any job is admitted.
    /// </summary>
    /// <param name="startTimeOf">
    /// Start time of the live process with this pid <b>in UTC</b>, or null if no such process
    /// exists. Injected so the recycling logic is testable without spawning anything.
    /// </param>
    public static int KillLeftovers(string path, Func<int, DateTime?> startTimeOf) =>
        KillLeftovers(path, startTimeOf, KillRecord);

    /// <summary>Overload with the kill injected, so the identity rules can be tested without
    /// putting real pids in range of a real SIGKILL.</summary>
    internal static int KillLeftovers(string path, Func<int, DateTime?> startTimeOf,
                                      Action<ManagedProcessRecord> kill)
    {
        var registry = new ManagedProcessRegistry(path);
        int killed = 0;

        foreach (var record in registry.Load())
        {
            DateTime? actual;
            try { actual = startTimeOf(record.Pid); }
            catch { continue; }                                  // unreadable: leave it alone

            if (actual == null)
                continue;                                        // already gone

            // Tolerance, not equality: the two readings come from two different processes. See
            // StartTimeTolerance — this is the whole reason the sweep works on Linux at all.
            if (Math.Abs(actual.Value.Ticks - record.StartTimeTicks) > StartTimeTolerance.Ticks)
                continue;                                        // pid recycled: not our process

            kill(record);
            killed++;
        }

        // Cleared whatever happened. A record we could not kill is not going to become killable on
        // a later boot, and keeping it would put a stale pid in range of every future sweep.
        registry.Clear();
        return killed;
    }

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
