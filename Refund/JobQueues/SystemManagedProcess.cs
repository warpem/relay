using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace Refund.JobQueues;

/// <summary>
/// A real OS process running one job's submission script, in its own process group.
/// </summary>
/// <remarks>
/// The group is what makes the tree killable. Jobs launch mpirun, which launches ranks; signalling
/// only the direct child orphans the real work. A group id also survives losing the Process handle,
/// which is what lets a restarted Relay clean up leftovers (see ManagedProcessRegistry).
/// </remarks>
public sealed class SystemManagedProcess : IManagedProcess
{
    private readonly Process _process;
    private readonly Task _pumps;
    private readonly TimeSpan _drainGrace;
    private readonly OwnProcessGroup _group;

    /// <summary>
    /// Timestamp at which the OS process was first observed gone, or 0 if it has not been. Only
    /// ever moved off 0, and only once, so <see cref="HasExited"/> stays monotonic.
    /// </summary>
    private long _exitObservedAt;

    /// <summary>
    /// How long output is given to finish draining after the process itself has gone. Bounded on
    /// purpose: a background grandchild that inherited the pipe keeps it open with no writer we
    /// will ever see again, and the executor frees this job's cores, memory and GPUs on exactly
    /// this signal. Waiting forever would strand the allocation for the life of the daemon.
    /// The buffered output of a process that has actually exited drains in microseconds, so this
    /// only ever fires for the pathological case.
    /// </summary>
    private static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(10);

    public int Pid { get; }

    /// <summary>
    /// The process group we created for this job, or null when we have no group of our own —
    /// either because the platform has no <c>setsid</c> (macOS), or because the child has not
    /// entered its group yet. Null means the child is, as far as we can prove, still in
    /// <em>Relay's own</em> group, and group-signalling that would kill Relay — so a null here
    /// must never be turned into a group kill anywhere, including the startup leftover sweep.
    /// </summary>
    /// <remarks>
    /// <b>Resolved on read, not at launch, and callers must treat an early null as "not yet".</b>
    /// <c>Process.Start</c> returns as soon as <c>fork(2)</c> returns in the parent, well before
    /// the child has finished <c>execve</c>, libc startup and <c>setsid(2)</c> — measured at
    /// 1-140 ms. A read taken inside <see cref="Start"/> therefore observes Relay's group every
    /// single time, and latching that would permanently disable group kills and the startup
    /// leftover sweep on the one platform that has them. Anything persisting this value must
    /// re-read it rather than capture it at launch.
    /// </remarks>
    public int? Pgid => _group.Value;

    public DateTime StartTime { get; }

    /// <summary>
    /// True only once the process has exited AND its output has been fully flushed — or the drain
    /// grace has run out; see <see cref="DefaultDrainGrace"/>. Reporting a terminal status while
    /// output is still buffered lets HandleJobCompletion run its final progress tracking and
    /// dequeue the job before its last log lines are on disk.
    /// </summary>
    /// <remarks>
    /// Called under the executor's host-wide lock, so it never blocks: the drain bound is a
    /// timestamp comparison, not a wait.
    /// </remarks>
    public bool HasExited
    {
        get
        {
            if (!_process.HasExited)
                return false;

            if (_pumps.IsCompleted)
                return true;

            // Start the drain clock the first time we notice, so the grace is measured from the
            // process ending rather than from whenever something happened to poll us.
            Interlocked.CompareExchange(ref _exitObservedAt, Stopwatch.GetTimestamp(), 0);

            return Stopwatch.GetElapsedTime(Interlocked.Read(ref _exitObservedAt)) > _drainGrace;
        }
    }

    public int ExitCode => _process.ExitCode;

    private SystemManagedProcess(Process process, Task pumps, bool ownGroup, TimeSpan drainGrace)
    {
        _process = process;
        _pumps = pumps;
        _drainGrace = drainGrace;
        Pid = process.Id;
        StartTime = SafeStartTime(process);

        // The raw process liveness, not this class's HasExited: whether the pid is still ours to
        // ask about has nothing to do with whether the output pumps have drained.
        _group = new OwnProcessGroup(process.Id, ownGroup, GetPgid, () => SafeHasExited(process));
    }

    /// <summary>
    /// setsid(1) is present on Linux but not in macOS's base system. Probed once: on Linux we get
    /// our own process group and can signal the whole tree by group id even after losing the
    /// handle; on macOS we fall back to .NET's tree walk, which cannot survive a Relay crash.
    /// </summary>
    private static readonly string InstalledSetsidPath =
        new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);

    /// <summary>
    /// Test seam. The setsid branch is the deployment branch but cannot be reached on a macOS dev
    /// machine, and it is the branch where getting the process group wrong is unrecoverable, so it
    /// has to be exercisable from here.
    /// </summary>
    internal static string SetsidPathOverride;

    private static string SetsidPath => SetsidPathOverride ?? InstalledSetsidPath;

    /// <summary>Whether this host can give a job a process group of its own.</summary>
    internal static bool CanCreateOwnGroup => SetsidPath != null;

    public static SystemManagedProcess Start(string scriptPath,
                                             string workingDirectory,
                                             IReadOnlyList<int> gpuIndices,
                                             string stdOutPath,
                                             string stdErrPath,
                                             TimeSpan? drainGrace = null)
    {
        bool ownGroup = CanCreateOwnGroup;

        var info = new ProcessStartInfo
        {
            // With setsid the script gets a fresh process group, so the whole tree can be signalled
            // by group id. Without it (macOS) we run bash directly and rely on .NET's tree walk.
            FileName = ownGroup ? SetsidPath : "/bin/bash",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (ownGroup)
            info.ArgumentList.Add("/bin/bash");
        info.ArgumentList.Add(scriptPath);

        // Enforced, unlike cores and memory: CUDA renumbers these to 0..n-1, which is what jobs
        // already assume (AlignMiss.cs:294, WarpJobGpu.cs:183). Set here rather than injected into
        // the script so the template stays scheduler-agnostic. Always set, never left inherited —
        // an empty value is "no GPUs", which is exactly right for a job that booked none.
        info.Environment["CUDA_VISIBLE_DEVICES"] = string.Join(",", gpuIndices);

        // Relay's own web-host variables would otherwise leak into compute processes.
        foreach (var key in info.Environment.Keys
                     .Where(k => k.StartsWith("ASPNETCORE_") || k.StartsWith("Kestrel__")).ToList())
            info.Environment.Remove(key);

        var process = new Process { StartInfo = info };
        process.Start();

        // Nothing on the far end of a job's stdin, and inheriting Relay's would let a script that
        // reads it block forever or steal console input. Closing it gives a deterministic EOF.
        try { process.StandardInput.Close(); } catch { /* already gone */ }

        var pumps = Task.WhenAll(
            PumpAsync(process.StandardOutput, stdOutPath),
            PumpAsync(process.StandardError, stdErrPath));

        return new SystemManagedProcess(process, pumps, ownGroup, drainGrace ?? DefaultDrainGrace);
    }

    /// <summary>Process.HasExited throws once the handle is disposed; treat that as "still alive",
    /// which is the conservative answer everywhere it is used here.</summary>
    private static bool SafeHasExited(Process process)
    {
        try { return process.HasExited; }
        catch { return false; }
    }

    /// <summary>
    /// Read once, immediately after starting, and paired with the pid to survive pid recycling.
    /// A script short enough to have been reaped already leaves nothing to read, and "now" is
    /// within milliseconds of the truth at this point.
    /// </summary>
    private static DateTime SafeStartTime(Process process)
    {
        try { return process.StartTime; }
        catch { return DateTime.Now; }
    }

    /// <summary>
    /// Line-by-line with a flush per line. CopyToAsync would buffer 80 KiB, which is far too coarse
    /// for TrackProgressLogs, which tails these files to drive the job card's live progress.
    /// </summary>
    /// <remarks>
    /// Never faults: a pump that threw would leave <see cref="Task.WhenAll"/> faulted, and every
    /// caller awaiting it would get the write error instead of the job's exit status.
    /// </remarks>
    private static async Task PumpAsync(StreamReader reader, string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Truncate, never append. These files are per-run: the SLURM path this replaces
            // truncates via #SBATCH --output=, Class3DContinue deletes PathStdOut before running,
            // and every progress parser (Class3D.cs:1197, Refine3D.cs:976, InitialReference.cs:391,
            // PostProcess.cs:413) reads the whole file and indexes iterations by line position from
            // the top. Appending would let a re-queued job parse its previous run's markers as
            // current progress.
            await using var writer = new StreamWriter(path, append: false) { AutoFlush = true };

            while (await reader.ReadLineAsync() is { } line)
                await writer.WriteLineAsync(line);
        }
        catch (Exception exc)
        {
            Log.ForContext<SystemManagedProcess>().Warning(
                exc, "Stopped pumping job output to {Path}.", path);
        }
    }

    public void KillTree()
    {
        // Resolved here rather than at launch; see the remarks on Pgid. An abort that lands within
        // milliseconds of the spawn can legitimately still see null, and takes the tree walk —
        // which is sound, because the direct child is necessarily still alive that early.
        int? pgid = Pgid;

        if (pgid == null && CanCreateOwnGroup)
            Log.ForContext<SystemManagedProcess>().Debug(
                "Killing process {Pid} by tree walk: no process group of ours could be confirmed " +
                "for it.", Pid);

        KillTree(Pid, pgid, () => _process.Kill(entireProcessTree: true),
                 () => SafeHasExited(_process));
    }

    /// <summary>
    /// Shared by the live path and the startup leftover sweep, which has no Process handle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <paramref name="pgid"/> null check is a safety interlock, not an optimisation. A null
    /// group means the child inherited Relay's group; <c>kill(-pgid)</c> would then take Relay down
    /// with it. Only a group we created ourselves is ever signalled as a group.
    /// </para>
    /// <para>
    /// There is deliberately no SIGTERM-then-SIGKILL escalation. Both branches go straight to a
    /// signal nothing can catch, block or ignore, so there is nothing left to escalate <em>to</em>:
    /// <c>kill(-pgid, SIGKILL)</c> takes every member of the group at once, and .NET's tree walk is
    /// SIGKILL as well. A graceful first signal would be strictly worse here — the executor
    /// condemns an entry once and never re-signals, so a SIGTERM the job ignored would hold its
    /// GPUs until it happened to exit on its own; and escalating later, gated on the direct child
    /// having gone, would miss exactly the surviving grandchildren this exists to reach, while
    /// escalating ungated risks signalling a group id the kernel has since recycled.
    /// </para>
    /// <para>
    /// Returns as soon as the signal is away, as the threading contract on
    /// <see cref="IManagedProcess"/> requires; it is called under the executor's host-wide lock.
    /// </para>
    /// <para>
    /// <b>Known residual, deliberately accepted: the live path has no recycled-pgid guard.</b>
    /// Signalling the group before consulting leader liveness — the paragraph above — is what makes
    /// the group signal reach surviving descendants, but it also means there is no liveness probe
    /// left in front of it. ManagedExecutor.Kill reaches Condemn, and Condemn calls
    /// <see cref="KillTree()"/> without a preceding Reconcile, so between .NET reaping the child
    /// and the kill landing the kernel could in principle recycle the pgid and the signal would go
    /// to a stranger's group. The window is small and the alternative — probing first — reinstates
    /// the far likelier bug of leaving mpirun ranks computing on a released GPU. Closing it
    /// properly needs OS-level containment (a cgroup per job), not another check here. The
    /// <em>registry</em> path has no such residual: ManagedProcessRegistry.TryContain probes
    /// identity before it signals, because its caller retries on a timer with no gate of its own.
    /// </para>
    /// </remarks>
    internal static void KillTree(int pid, int? pgid, Action fallbackKill, Func<bool> hasExited) =>
        KillTree(pid, pgid, fallbackKill, hasExited, Kill);

    /// <summary>Overload with the signal call injected, so the interlock can be tested without
    /// putting real pids in range of a real SIGKILL.</summary>
    internal static void KillTree(int pid, int? pgid, Action fallbackKill, Func<bool> hasExited,
                                  Func<int, int, int> signal)
    {
        try
        {
            // The group first, *before* leader liveness. A bash group leader routinely exits while
            // the work it started is still in the group — `( ... ) &` and mpirun both produce
            // exactly that — and returning early on hasExited() left those descendants computing
            // while the executor released their GPUs and the registry forgot the record. Signalling
            // a group that is already empty is harmless: kill(2) returns ESRCH, which is a nonzero
            // return, which falls through to the branch below exactly as a failed signal does.
            //
            // pgid == pid by construction for a group we made (verified in OwnGroupOf), and the
            // > 1 guard is what stops a corrupted or defaulted pair reaching kill(-1, SIGKILL),
            // which means "every process this user may signal". The negation is the point:
            // kill(pgid) would hit only the group leader and leave every mpirun rank running.
            if (pgid is { } group && group == pid && group > 1 && signal(-group, SIGKILL) == 0)
                return;

            // Only now. With no group of ours to signal there is nothing left but the tree walk,
            // and that needs a live direct child to walk from.
            if (hasExited())
                return;
        }
        catch { /* the group path is unavailable; the tree walk is still worth trying */ }

        // Reached when there is no group of ours to signal, and also when signalling it did not
        // work — a nonzero kill(2) or a throwing p/invoke. Never swallow a failed group kill into
        // silence: .NET's tree walk is a weaker containment but a far better outcome than a job
        // left running while its resources are handed to someone else.
        try { fallbackKill(); }
        catch { /* already gone */ }
    }

    private const int SIGKILL = 9;

    /// <summary>
    /// Whether <paramref name="pgid"/> has any member left. Signal 0 asks the kernel about
    /// existence and permission without delivering anything, so this is a probe and never a kill:
    /// ESRCH means no process is in the group at all, which is the only answer that proves the work
    /// a leftover record describes is really over. A leader's identity disappearing does not — a
    /// descendant can outlive it in the same group.
    /// </summary>
    /// <remarks>
    /// Any other failure (EPERM, or a p/invoke that throws) is reported as "not empty": something
    /// may be there and we cannot see it, and the caller's cost for a false negative is a queue
    /// that stays Busy and retries, against a GPU nothing is tracking for a false positive.
    /// </remarks>
    internal static bool GroupIsEmpty(int pgid)
    {
        // Same interlock as the kill path: -1 means "every process this user may signal", and no
        // group we created can be 1 or below. An unaskable group is not an empty one.
        if (pgid <= 1)
            return false;

        try { return Kill(-pgid, 0) != 0 && Marshal.GetLastWin32Error() == ESRCH; }
        catch { return false; }
    }

    /// <summary>No such process — the errno that means the group is genuinely empty.</summary>
    private const int ESRCH = 3;

    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        await _process.WaitForExitAsync(ct);

        // Same bound as HasExited, and for the same reason: an inherited pipe can outlive the job.
        try { await _pumps.WaitAsync(_drainGrace, ct); }
        catch (TimeoutException) { /* output gave up; the exit status is still the answer */ }
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);

    [DllImport("libc", EntryPoint = "getpgid", SetLastError = true)]
    private static extern int GetPgid(int pid);
}

/// <summary>
/// The process group a child was launched into, confirmed against the OS and then remembered.
/// Null until the kernel agrees the child leads a group of its own.
/// </summary>
/// <remarks>
/// <para>
/// Resolution is lazy on purpose, and this is the whole reason the type exists.
/// <c>Process.Start</c> returns as soon as <c>fork(2)</c> returns in the parent; the child still
/// has to complete <c>execve</c>, libc startup and <c>setsid(2)</c>. Measured on Linux, a
/// <c>getpgid</c> issued immediately after Start loses that race every time — 0 of 60 reads saw
/// the new group, with a lag of 1-140 ms. Recording that answer would latch null forever, which
/// silently downgrades every kill to the tree walk and turns the startup leftover sweep, which
/// dispatches on a non-null group, into a permanent no-op. On the deployment platform. With no
/// other symptom.
/// </para>
/// <para>
/// The alternative — polling inside Start until the group appears — would add up to ~140 ms to
/// every launch and still be probabilistic. Reading on use costs one syscall on a path
/// (<see cref="SystemManagedProcess.KillTree()"/>) that is already making one, and by the time
/// anyone can act on the answer it is correct.
/// </para>
/// <para>
/// Only a positive is cached, and only <c>pgid == pid &amp;&amp; pid &gt; 1</c> counts as one:
/// everything downstream treats a non-null value as safe to <c>kill(-pgid)</c>, so this never
/// guesses. Once confirmed the value survives the process exiting, because the registry needs to
/// keep signalling a group after Relay has lost the handle. Before confirmation an exited process
/// resolves to null instead, since its pid may already have been recycled into somebody else's
/// group.
/// </para>
/// </remarks>
internal sealed class OwnProcessGroup
{
    private readonly int _pid;
    private readonly bool _mayHaveOwnGroup;
    private readonly Func<int, int> _getPgid;
    private readonly Func<bool> _hasExited;

    /// <summary>The confirmed group, or 0 while the OS has not yet agreed there is one.</summary>
    private int _confirmed;

    internal OwnProcessGroup(int pid, bool mayHaveOwnGroup, Func<int, int> getPgid,
                             Func<bool> hasExited)
    {
        _pid = pid;
        _mayHaveOwnGroup = mayHaveOwnGroup;
        _getPgid = getPgid;
        _hasExited = hasExited;
    }

    internal int? Value
    {
        get
        {
            int confirmed = Volatile.Read(ref _confirmed);
            if (confirmed != 0)
                return confirmed;

            // No setsid on this host, so the child is in Relay's group and always will be. Never
            // even ask: there is no answer that could make a group kill safe.
            if (!_mayHaveOwnGroup)
                return null;

            try
            {
                if (_hasExited())
                    return null;

                int pgid = _getPgid(_pid);

                // setsid makes the child its own group leader, so pgid == pid. Anything else is
                // either the race (not there yet) or a setsid that forked instead of exec-ing,
                // and both mean we have no group we may signal. The > 1 guard keeps a corrupt or
                // defaulted pair from ever becoming kill(-1), which is "every process this user
                // may signal".
                if (pgid == _pid && pgid > 1)
                {
                    Volatile.Write(ref _confirmed, pgid);
                    return pgid;
                }
            }
            catch { /* no group we can prove is ours */ }

            return null;
        }
    }
}
