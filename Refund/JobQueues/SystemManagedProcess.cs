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
    /// The process group we created for this job, or null when the platform has no
    /// <c>setsid</c> (macOS). Null means the child inherited <em>Relay's own</em> group, and
    /// group-signalling it would kill Relay — so a null here must never be turned into a
    /// group kill anywhere, including the startup leftover sweep.
    /// </summary>
    public int? Pgid { get; }

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

    private SystemManagedProcess(Process process, Task pumps, int? pgid, TimeSpan drainGrace)
    {
        _process = process;
        _pumps = pumps;
        _drainGrace = drainGrace;
        Pid = process.Id;
        StartTime = SafeStartTime(process);
        Pgid = pgid;
    }

    /// <summary>
    /// setsid(1) is present on Linux but not in macOS's base system. Probed once: on Linux we get
    /// our own process group and can signal the whole tree by group id even after losing the
    /// handle; on macOS we fall back to .NET's tree walk, which cannot survive a Relay crash.
    /// </summary>
    private static readonly string SetsidPath =
        new[] { "/usr/bin/setsid", "/bin/setsid" }.FirstOrDefault(File.Exists);

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

        return new SystemManagedProcess(process, pumps, OwnGroupOf(process, ownGroup),
                                        drainGrace ?? DefaultDrainGrace);
    }

    /// <summary>
    /// The group id to record, checked against the OS rather than assumed.
    /// </summary>
    /// <remarks>
    /// setsid makes the child its own group leader, so pgid == pid by construction — but only if
    /// setsid execs in place rather than forking, and only if it succeeded at all. Since the whole
    /// interlock downstream is "a non-null Pgid is safe to group-kill", the invariant is verified
    /// here instead of assumed: anything other than a group equal to our own pid is recorded as
    /// null, which costs nothing but the (dev-only) fallback path.
    /// </remarks>
    private static int? OwnGroupOf(Process process, bool ownGroup)
    {
        if (!ownGroup)
            return null;

        try
        {
            int pid = process.Id;
            int pgid = GetPgid(pid);

            if (pgid == pid)
                return pgid;

            Log.ForContext<SystemManagedProcess>().Warning(
                "Process {Pid} was launched via setsid but reports process group {Pgid}; " +
                "falling back to tree-walk kills for it.", pid, pgid);
        }
        catch (Exception exc)
        {
            Log.ForContext<SystemManagedProcess>().Warning(
                exc, "Could not read the process group of a freshly launched job.");
        }

        return null;
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

            await using var writer = new StreamWriter(path, append: true) { AutoFlush = true };

            while (await reader.ReadLineAsync() is { } line)
                await writer.WriteLineAsync(line);
        }
        catch (Exception exc)
        {
            Log.ForContext<SystemManagedProcess>().Warning(
                exc, "Stopped pumping job output to {Path}.", path);
        }
    }

    public void KillTree() => KillTree(Pid, Pgid, () => _process.Kill(entireProcessTree: true),
                                       () => _process.HasExited);

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
            if (hasExited())
                return;

            // pgid == pid by construction for a group we made (verified in OwnGroupOf), and the
            // > 1 guard is what stops a corrupted or defaulted pair reaching kill(-1, SIGKILL),
            // which means "every process this user may signal". The negation is the point:
            // kill(pgid) would hit only the group leader and leave every mpirun rank running.
            if (pgid is { } group && group == pid && group > 1 && signal(-group, SIGKILL) == 0)
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
