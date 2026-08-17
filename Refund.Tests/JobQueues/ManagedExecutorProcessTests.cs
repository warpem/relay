using System.Diagnostics;
using System.Runtime.InteropServices;
using Refund.DataModel;
using Refund.JobQueues;
using MaskJob = Refund.Jobs.Refinement.Masks.CreateMask.CreateMask;
using Class2DJob = Refund.Jobs.Refinement.Classes2D.Class2D.Class2D;

namespace Refund.Tests.JobQueues;

/// <summary>
/// Exercises the real-process end of the managed queue: spawning, output pumps, terminal-status
/// ordering, and the group-kill interlock.
/// </summary>
/// <remarks>
/// These tests start actual OS processes, so they are the only ones in the managed-queue suite that
/// are platform-sensitive. Every platform-dependent assertion is written as "assert what this
/// platform must do", never as a skip: the setsid branch is asserted on Linux and the inherited-group
/// branch is asserted on macOS, so both halves of the interlock are covered by whichever machine
/// runs them.
/// </remarks>
[Collection("JobRegistry")]
public class ManagedExecutorProcessTests : IDisposable
{
    private static readonly object _populateLock = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "relay-managed-" + Guid.NewGuid());

    public ManagedExecutorProcessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);

    private static void EnsurePopulated()
    {
        lock (_populateLock)
        {
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
        }
    }

    /// <summary>CreateMask: 1 process x 1 core, 8 GB, no GPU.</summary>
    private Job NewJob()
    {
        EnsurePopulated();
        return new MaskJob { Space = new Space { RootDirectory = _dir }, Status = JobStatus.Running };
    }

    /// <summary>Class2D with a GPU: 1 process x 1 core, 16 GB, 1 GPU.</summary>
    private Job NewGpuJob()
    {
        EnsurePopulated();
        return new Class2DJob
        {
            UseGpu = true, Space = new Space { RootDirectory = _dir }, Status = JobStatus.Running
        };
    }

    private string WriteScript(string body, string name = "submit.sh")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
        return path;
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition not met within timeout.");
    }

    /// <summary>Stands in for a real OS process where the test needs to drive it deterministically.</summary>
    private sealed class FakeProcess : IManagedProcess
    {
        public int Pid { get; init; } = 4242;
        public DateTime StartTime { get; init; } = new(2026, 1, 1);
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }
        public int KillCount { get; private set; }

        public void Exit(int code) { ExitCode = code; HasExited = true; }
        public void KillTree() => KillCount++;
        public Task WaitForExitAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    #region Running a script

    [Fact]
    public async Task Launch_RunsTheScript_AndReportsFinishedOnCleanExit()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        executor.Launch(job, WriteScript("exit 0"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        Assert.Equal(ClusterJobStatus.Finished, executor.GetStatus(job));
    }

    [Fact]
    public async Task Launch_NonZeroExit_ReportsFailed()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        executor.Launch(job, WriteScript("exit 3"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Failed);

        Assert.Equal(ClusterJobStatus.Failed, executor.GetStatus(job));
    }

    [Fact]
    public async Task Launch_ReportsRunning_BeforeTheScriptHasFinished()
    {
        // Pending vs Running is what tells ClusterQueue the job actually started; a Launch that
        // spawned but failed to attach would leave the entry reporting Pending forever.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var release = Path.Combine(_dir, "release");
        executor.Launch(job, WriteScript($"while [ ! -f '{release}' ]; do sleep 0.05; done"), _dir);

        Assert.Equal(ClusterJobStatus.Running, executor.GetStatus(job));

        File.WriteAllText(release, "");
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);
    }

    [Fact]
    public async Task Launch_RunsTheScriptInTheGivenWorkingDirectory()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var workDir = Path.Combine(_dir, "work");
        Directory.CreateDirectory(workDir);

        executor.Launch(job, WriteScript("pwd"), workDir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        // macOS resolves /var -> /private/var, so compare the leaf rather than the whole path.
        var written = await File.ReadAllTextAsync(job.PathStdOut);
        Assert.EndsWith("/work", written.Trim());
    }

    #endregion

    #region Output pumps and terminal-status ordering

    [Fact]
    public async Task TerminalStatus_IsWithheldUntilOutputHasBeenFlushed()
    {
        // Process.HasExited can go true while buffered output is unwritten. HandleJobCompletion
        // runs final progress tracking and then dequeues, so reporting Finished early loses the
        // job's last log lines.
        //
        // The line count is load-bearing. 5000 short lines fit inside the pipe, so the script
        // writes the lot and exits without ever blocking, while the pump — one flush per line, by
        // design — is still thousands of lines behind. That makes the backlog at process exit
        // certain rather than incidental: at 500 lines the pump drains within a single 25 ms poll,
        // and an implementation that ignored the pumps entirely passed this test anyway.
        const int Lines = 5000;

        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        executor.Launch(job, WriteScript($"for i in $(seq 1 {Lines}); do echo line-$i; done"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        // Read at the first terminal observation: the contract is that everything is already on
        // disk by then, not that it turns up shortly afterwards.
        var written = await File.ReadAllTextAsync(job.PathStdOut);

        Assert.Contains("line-1\n", written);
        Assert.Contains($"line-{Lines}\n", written);
        Assert.Equal(Lines, written.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task OutputFilesAreTruncatedPerRun_NotAppendedTo()
    {
        // Every progress parser reads the whole file and indexes iterations by line position from
        // the top (Class3D.cs:1197, Refine3D.cs:976, InitialReference.cs:391, PostProcess.cs:413),
        // so a re-queued job appending to its previous run's log parses stale markers as current
        // progress. The SLURM path this replaces truncates via #SBATCH --output=.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        await File.WriteAllTextAsync(job.PathStdOut, "stale-iteration-from-the-previous-run\n");
        await File.WriteAllTextAsync(job.PathStdErr, "stale-error-from-the-previous-run\n");

        executor.Launch(job, WriteScript("echo fresh-out; echo fresh-err 1>&2"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        var stdOut = await File.ReadAllTextAsync(job.PathStdOut);
        var stdErr = await File.ReadAllTextAsync(job.PathStdErr);

        Assert.DoesNotContain("stale-iteration", stdOut);
        Assert.DoesNotContain("stale-error", stdErr);
        Assert.Contains("fresh-out", stdOut);
        Assert.Contains("fresh-err", stdErr);
    }

    [Fact]
    public async Task StandardErrorGoesToTheJobsErrorFile_NotItsOutputFile()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        executor.Launch(job, WriteScript("echo to-out; echo to-err 1>&2"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        var stdOut = await File.ReadAllTextAsync(job.PathStdOut);
        var stdErr = await File.ReadAllTextAsync(job.PathStdErr);

        Assert.Contains("to-out", stdOut);
        Assert.DoesNotContain("to-err", stdOut);
        Assert.Contains("to-err", stdErr);
        Assert.DoesNotContain("to-out", stdErr);
    }

    [Fact]
    public async Task OutputIsFlushedLineByLine_WhileTheJobIsStillRunning()
    {
        // TrackProgressLogs tails these files to drive the job card. CopyToAsync's 80 KiB buffer
        // would leave the file empty until the job ended, so the card would never move.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var release = Path.Combine(_dir, "release");
        executor.Launch(job, WriteScript(
            $"echo early-line; while [ ! -f '{release}' ]; do sleep 0.05; done"), _dir);

        await WaitUntil(() => File.Exists(job.PathStdOut) && ReadShared(job.PathStdOut).Contains("early-line"));
        Assert.Equal(ClusterJobStatus.Running, executor.GetStatus(job));

        File.WriteAllText(release, "");
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task HasExited_WaitsForThePumps_ButNotForever()
    {
        // A background grandchild inherits the pipe, so the pump sees no EOF even though the job's
        // own process is long gone. Waiting for it unboundedly would strand the allocation, which
        // is released on exactly this signal.
        //
        // Both bounds are asserted as elapsed time rather than as a state read at some chosen
        // instant, because only elapsed time is robust under load: a slow machine can only push
        // the measurement further from the thing being asserted, never across it.
        var grace = TimeSpan.FromSeconds(1);
        var pidFile = Path.Combine(_dir, "grandchild.pid");

        // The sleep is detached and inherits stdout, so it outlives the script and holds the pipe
        // open with no writer we will ever see again. Its pid is recorded so the test can clean up
        // rather than leave it behind — on macOS there is no group to signal, so KillTree cannot.
        var script = WriteScript($"sleep 15 & echo $! > '{pidFile}'; echo done; exit 0");

        var started = Stopwatch.StartNew();
        var process = SystemManagedProcess.Start(script, _dir, Array.Empty<int>(),
                                                 Path.Combine(_dir, "std.out"),
                                                 Path.Combine(_dir, "std.err"),
                                                 drainGrace: grace);
        try
        {
            // Upper bound: the wait is bounded at all. An implementation that waited for the pumps
            // unconditionally would sit here for the full 15 s and time out.
            await WaitUntil(() => process.HasExited, timeoutMs: 10_000);

            // Lower bound: it really did wait. The script exits within milliseconds, so an
            // implementation that ignored the pumps would arrive here almost immediately.
            Assert.True(started.Elapsed >= grace,
                        $"HasExited fired after {started.Elapsed.TotalMilliseconds:F0} ms, " +
                        $"inside the {grace.TotalSeconds:F0} s drain grace");
        }
        finally
        {
            process.KillTree();
            KillIfRunning(pidFile);
        }
    }

    private static void KillIfRunning(string pidFile)
    {
        try
        {
            if (!File.Exists(pidFile) ||
                !int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                return;

            using var process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch { /* already gone */ }
    }

    [Fact]
    public async Task WaitForExitAsync_ReturnsOnceOutputIsOnDisk()
    {
        var script = WriteScript("for i in $(seq 1 500); do echo line-$i; done");
        var outPath = Path.Combine(_dir, "std.out");

        var process = SystemManagedProcess.Start(script, _dir, Array.Empty<int>(),
                                                 outPath, Path.Combine(_dir, "std.err"));
        await process.WaitForExitAsync();

        Assert.Contains("line-500", await File.ReadAllTextAsync(outPath));
        Assert.True(process.HasExited);
        Assert.Equal(0, process.ExitCode);
    }

    #endregion

    #region Environment

    [Fact]
    public async Task Launch_ExportsAssignedGpuIndices()
    {
        var executor = new ManagedExecutor();
        var job = NewGpuJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 4));

        // Read while the reservation is still live: GpuIndicesFor deliberately goes empty once the
        // run is over, and an expectation of "" would match anything.
        var indices = executor.GpuIndicesFor(job);
        Assert.Single(indices);

        executor.Launch(job, WriteScript("echo \"visible=$CUDA_VISIBLE_DEVICES\""), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        var expected = string.Join(",", indices);
        Assert.Contains($"visible={expected}", await File.ReadAllTextAsync(job.PathStdOut));
    }

    [Fact]
    public async Task Launch_ExportsAnEmptyDeviceList_ForACpuJob()
    {
        // Not "leave it unset": a CPU job that inherited Relay's own CUDA_VISIBLE_DEVICES would
        // happily grab a GPU nothing booked for it.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 4));

        var previous = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "7");
        try
        {
            executor.Launch(job, WriteScript("echo \"visible=[$CUDA_VISIBLE_DEVICES]\""), _dir);
            await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", previous);
        }

        Assert.Contains("visible=[]", await File.ReadAllTextAsync(job.PathStdOut));
    }

    [Fact]
    public async Task Launch_ScrubsRelaysOwnWebHostVariables()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://localhost:5099");
        Environment.SetEnvironmentVariable("Kestrel__Endpoints__Http__Url", "http://localhost:5099");
        Environment.SetEnvironmentVariable("PATH_SHOULD_SURVIVE", "yes");
        try
        {
            executor.Launch(job, WriteScript(
                "echo \"urls=[$ASPNETCORE_URLS]\"; " +
                "echo \"kestrel=[$Kestrel__Endpoints__Http__Url]\"; " +
                "echo \"other=[$PATH_SHOULD_SURVIVE]\""), _dir);
            await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__Http__Url", null);
            Environment.SetEnvironmentVariable("PATH_SHOULD_SURVIVE", null);
        }

        var written = await File.ReadAllTextAsync(job.PathStdOut);
        Assert.Contains("urls=[]", written);
        Assert.Contains("kestrel=[]", written);
        Assert.Contains("other=[yes]", written);   // only Relay's own variables are removed
    }

    #endregion

    #region Killing

    [Fact]
    public async Task Kill_TerminatesTheWholeTree_NotJustTheDirectChild()
    {
        // Jobs launch mpirun, which launches ranks. Killing only the shell orphans the real work.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var marker = Path.Combine(_dir, "child-alive");
        executor.Launch(job, WriteScript(
            $"( while true; do touch '{marker}'; sleep 0.1; done ) & wait"), _dir);

        await WaitUntil(() => File.Exists(marker));
        executor.Kill(job);
        await WaitUntil(() => executor.GetStatus(job) is ClusterJobStatus.Failed
                                                      or ClusterJobStatus.Finished);

        File.Delete(marker);
        await Task.Delay(500);
        Assert.False(File.Exists(marker), "grandchild survived the kill");
    }

    [Fact]
    public void GroupKill_IsNeverUsedForAGroupWeDidNotCreate()
    {
        // Without setsid (macOS) the child inherits Relay's process group. Turning that pgid into
        // kill(-pgid) would terminate Relay itself. Assert the interlock directly: a null pgid, or
        // one that is not the child's own pid, must take the fallback path and never signal a group.
        var signals = new List<(int Pid, int Signal)>();
        var fallbackCalled = false;

        SystemManagedProcess.KillTree(pid: 1234, pgid: null,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false,
                                      signal: (p, s) => { signals.Add((p, s)); return 0; });
        Assert.True(fallbackCalled);
        Assert.Empty(signals);

        fallbackCalled = false;
        SystemManagedProcess.KillTree(pid: 1234, pgid: 1,      // pgid != pid: not ours
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false,
                                      signal: (p, s) => { signals.Add((p, s)); return 0; });
        Assert.True(fallbackCalled);
        Assert.Empty(signals);

        // The one that actually looks plausible: a real, ordinary group id that simply is not the
        // one we made. On macOS this is precisely Relay's own group, so signalling it is the
        // failure mode the whole interlock exists to prevent.
        fallbackCalled = false;
        SystemManagedProcess.KillTree(pid: 1234, pgid: 999,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false,
                                      signal: (p, s) => { signals.Add((p, s)); return 0; });
        Assert.True(fallbackCalled);
        Assert.Empty(signals);
    }

    [Fact]
    public void GroupKill_SignalsTheNegatedGroup_WhenTheGroupIsOurs()
    {
        // The negation is the whole point: kill(pgid) hits only the group leader and leaves every
        // mpirun rank running, while kill(-pgid) takes the group.
        var signals = new List<(int Pid, int Signal)>();
        var fallbackCalled = false;

        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false,
                                      signal: (p, s) => { signals.Add((p, s)); return 0; });

        Assert.Equal((-4321, 9), Assert.Single(signals));   // SIGKILL, which nothing can ignore
        Assert.False(fallbackCalled);
    }

    [Fact]
    public void GroupKill_IsNeverUsedForGroupOneOrBelow()
    {
        // kill(-1, SIGKILL) means "every process this user may signal". No pgid we created can be
        // 1, so treating that as our own group could only ever be a bug — with the whole session
        // as the blast radius.
        var signals = new List<(int Pid, int Signal)>();
        var fallbackCalled = false;

        SystemManagedProcess.KillTree(pid: 1, pgid: 1,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false,
                                      signal: (p, s) => { signals.Add((p, s)); return 0; });

        Assert.Empty(signals);
        Assert.True(fallbackCalled);
    }

    [Fact]
    public void KillTree_DoesNothingForAnAlreadyExitedProcess()
    {
        var signals = new List<(int Pid, int Signal)>();
        var fallbackCalled = false;

        SystemManagedProcess.KillTree(pid: 1234, pgid: null,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => true,
                                      signal: (p, s) => { signals.Add((p, s)); return 0; });

        Assert.False(fallbackCalled);
        Assert.Empty(signals);
    }

    [Fact]
    public void KillTree_StillSignalsOurGroup_WhenItsLeaderHasAlreadyExited()
    {
        // The group outlives its leader. A submission script that runs `( ... ) &` or mpirun
        // routinely has bash exit while the real compute is still a member of the group setsid
        // gave us — so skipping the group signal on leader liveness left that compute running
        // while the executor released its GPUs and the registry dropped the record. Signalling an
        // empty group instead costs one ESRCH.
        var signals = new List<(int Pid, int Signal)>();

        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => Assert.Fail("should not fall back"),
                                      hasExited: () => true,
                                      signal: (p, s) => { signals.Add((p, s)); return 0; });

        Assert.Equal((-4321, 9), Assert.Single(signals));
    }

    [Fact]
    public void KillTree_DoesNotTreeWalkAnExitedProcess_WhenTheGroupSignalDidNotLand()
    {
        // The other half of the ordering. Once the group signal has been attempted and failed —
        // an empty group returns ESRCH — there is nothing left but .NET's tree walk, and that
        // needs a live direct child to walk from. A reaped pid can have been recycled, so walking
        // it would be walking somebody else's tree.
        var signals = new List<(int Pid, int Signal)>();

        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => Assert.Fail("should not walk a dead pid"),
                                      hasExited: () => true,
                                      signal: (p, s) => { signals.Add((p, s)); return -1; });

        Assert.Equal((-4321, 9), Assert.Single(signals));   // tried, once
    }

    [Fact]
    public void GroupKill_FallsBackWhenTheGroupSignalDoesNotLand()
    {
        // A kill(2) that returns nonzero, or a p/invoke that throws, must not be swallowed into
        // silence: the tree walk is weaker containment but far better than a job left running
        // while the ledger hands its resources to somebody else.
        var fallbackCalled = false;
        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false,
                                      signal: (_, _) => -1);
        Assert.True(fallbackCalled);

        fallbackCalled = false;
        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => false,
                                      signal: (_, _) => throw new DllNotFoundException("libc"));
        Assert.True(fallbackCalled);
    }

    [Fact]
    public void KillTree_ToleratesAProcessThatDiesMidKill()
    {
        // The threading contract requires KillTree to be safe on an already-dead process, and the
        // exit can also land between hasExited() and the signal. Nothing may escape into the
        // executor, which calls this under its host-wide lock.
        SystemManagedProcess.KillTree(pid: 4321, pgid: null,
                                      fallbackKill: () => throw new InvalidOperationException("gone"),
                                      hasExited: () => false,
                                      signal: (_, _) => 0);

        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => throw new InvalidOperationException("gone"),
                                      hasExited: () => false,
                                      signal: (_, _) => throw new InvalidOperationException("gone"));
    }

    [Fact]
    public void KillTree_ToleratesAHasExitedProbeThatThrows()
    {
        // Process.HasExited throws once the handle is disposed. Treat an unreadable liveness probe
        // as "might still be alive" and try the tree walk rather than assuming it is safe to skip.
        var fallbackCalled = false;

        SystemManagedProcess.KillTree(pid: 4321, pgid: null,   // no group: liveness is all there is
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => throw new InvalidOperationException("disposed"),
                                      signal: (_, _) => 0);

        Assert.True(fallbackCalled);

        // And with a group whose signal did not land, so the probe is reached there too.
        fallbackCalled = false;
        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => throw new InvalidOperationException("disposed"),
                                      signal: (_, _) => -1);

        Assert.True(fallbackCalled);
    }

    [Fact]
    public async Task KillTree_OnALiveProcess_ActuallyKillsIt()
    {
        // The unit tests above pin the interlock with a fake signal; this one pins that the real
        // wiring — whichever branch this platform takes — reaches the process.
        var script = WriteScript("sleep 30");
        var process = SystemManagedProcess.Start(script, _dir, Array.Empty<int>(),
                                                 Path.Combine(_dir, "std.out"),
                                                 Path.Combine(_dir, "std.err"));

        process.KillTree();
        await WaitUntil(() => process.HasExited, timeoutMs: 5_000);
    }

    [Fact]
    public async Task GroupIsEmpty_AnswersTheRealKernel_InBothDirections()
    {
        // The probe the leftover sweep now believes before it drops a record, so both answers have
        // to be right against a real kernel rather than only against a fake. Reading the errno
        // wrongly would report every group as still occupied, and managed admission would then stay
        // Busy for as long as Relay ran — the wedge this design works hardest to avoid.
        Assert.False(SystemManagedProcess.GroupIsEmpty(getpgid(Environment.ProcessId)));

        var process = SystemManagedProcess.Start(WriteScript("sleep 30"), _dir, Array.Empty<int>(),
                                                 Path.Combine(_dir, "std.out"),
                                                 Path.Combine(_dir, "std.err"));
        process.KillTree();
        await WaitUntil(() => process.HasExited, timeoutMs: 5_000);

        // On the setsid branch this is the group we created, now emptied; on macOS there is no such
        // group at all, which reads as ESRCH for the same reason and gives the same answer.
        await WaitUntil(() => SystemManagedProcess.GroupIsEmpty(process.Pid), timeoutMs: 5_000);
    }

    #endregion

    #region Process groups

    [Fact]
    public async Task Pgid_IsNonNullOnlyWhenWeReallyCreatedTheGroup()
    {
        // Asserts the invariant the whole interlock rests on, against the OS rather than against
        // our own bookkeeping. On Linux setsid gives the child its own group, so pgid == pid. On
        // macOS there is no setsid, the child lands in *Relay's* group, and Pgid must be null --
        // this test proves the inherited group really is Relay's, i.e. that null is required and
        // not merely cautious.
        var script = WriteScript("sleep 30");
        var process = SystemManagedProcess.Start(script, _dir, Array.Empty<int>(),
                                                 Path.Combine(_dir, "std.out"),
                                                 Path.Combine(_dir, "std.err"));
        try
        {
            await WaitUntil(() => getpgid(process.Pid) > 0, timeoutMs: 5_000);

            var childGroup = getpgid(process.Pid);
            var relayGroup = getpgid(Environment.ProcessId);

            if (SystemManagedProcess.CanCreateOwnGroup)
            {
                Assert.Equal(process.Pid, childGroup);       // setsid worked
                Assert.Equal(process.Pid, process.Pgid);
                Assert.NotEqual(relayGroup, childGroup);
            }
            else
            {
                Assert.Equal(relayGroup, childGroup);        // inherited Relay's own group
                Assert.Null(process.Pgid);
            }
        }
        finally
        {
            process.KillTree();
        }
    }

    [Fact]
    public void OwnProcessGroup_KeepsAskingWhileTheChildIsStillOnItsWayIntoTheGroup()
    {
        // THE regression test for this type's reason to exist. Process.Start returns when fork(2)
        // returns; execve, libc startup and setsid(2) are all still ahead of the child, so the
        // first reads legitimately see Relay's group. Latching that answer disables group kills
        // and the startup sweep permanently, on the deployment platform, with no other symptom.
        var calls = 0;
        var group = new OwnProcessGroup(pid: 4321, mayHaveOwnGroup: true,
                                        getPgid: _ => ++calls <= 3 ? 100 : 4321,
                                        hasExited: () => false);

        Assert.Null(group.Value);
        Assert.Null(group.Value);
        Assert.Null(group.Value);

        Assert.Equal(4321, group.Value);   // setsid has landed
    }

    [Fact]
    public void OwnProcessGroup_AsksTheOsOnlyUntilItGetsAnAnswer()
    {
        var calls = 0;
        var group = new OwnProcessGroup(4321, true, _ => { calls++; return 4321; }, () => false);

        Assert.Equal(4321, group.Value);
        Assert.Equal(4321, group.Value);
        Assert.Equal(4321, group.Value);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void OwnProcessGroup_RemembersAConfirmedGroup_AfterTheProcessHasGone()
    {
        // The registry has to keep being able to signal the group after Relay has lost the handle;
        // that is the entire point of recording a group rather than a Process.
        var exited = false;
        var group = new OwnProcessGroup(4321, true, _ => 4321, () => exited);

        Assert.Equal(4321, group.Value);
        exited = true;
        Assert.Equal(4321, group.Value);
    }

    [Fact]
    public void OwnProcessGroup_WillNotResolveAProcessThatIsAlreadyGone()
    {
        // Unconfirmed and exited: the pid may have been recycled, and the recycled process's
        // group is somebody else's tree.
        var group = new OwnProcessGroup(4321, true, _ => 4321, () => true);

        Assert.Null(group.Value);
    }

    [Fact]
    public void OwnProcessGroup_NeverAsksWhenThePlatformCannotCreateAGroup()
    {
        // On macOS the child is in Relay's group and always will be. There is no answer getpgid
        // could give that would make signalling it safe, so it is never asked. The probe returns
        // a *convincing* group rather than throwing: a swallowed exception would look like the
        // right answer for the wrong reason.
        var asked = false;
        var group = new OwnProcessGroup(4321, mayHaveOwnGroup: false,
                                        getPgid: _ => { asked = true; return 4321; },
                                        hasExited: () => false);

        Assert.Null(group.Value);
        Assert.False(asked);
    }

    [Fact]
    public void OwnProcessGroup_NeverTrustsAGroupThatIsNotTheChildsOwnPid()
    {
        Assert.Null(new OwnProcessGroup(4321, true, _ => 100, () => false).Value);
        Assert.Null(new OwnProcessGroup(1, true, _ => 1, () => false).Value);   // never kill(-1)
    }

    [Fact]
    public void OwnProcessGroup_ToleratesAFailingSyscall()
    {
        var group = new OwnProcessGroup(4321, true,
                                        _ => throw new DllNotFoundException("libc"),
                                        () => false);

        Assert.Null(group.Value);
    }

    [Fact]
    public async Task Pgid_IsResolvedOnceTheChildHasEnteredItsOwnGroup()
    {
        // Exercises the *deployment* branch, which a macOS dev machine cannot otherwise reach and
        // which is the one where getting the group wrong is unrecoverable. Under the previous
        // eager read this test is deterministically red — the child has not run setsid yet when
        // Start returns, so Pgid latched null and never recovered.
        var standIn = SetsidStandIn();
        Assert.NotNull(standIn);

        var previous = SystemManagedProcess.SetsidPathOverride;
        SystemManagedProcess.SetsidPathOverride = standIn;
        try
        {
            Assert.True(SystemManagedProcess.CanCreateOwnGroup);

            var process = SystemManagedProcess.Start(WriteScript("sleep 8"), _dir,
                                                     Array.Empty<int>(),
                                                     Path.Combine(_dir, "std.out"),
                                                     Path.Combine(_dir, "std.err"));
            try
            {
                await WaitUntil(() => process.Pgid != null, timeoutMs: 5_000);

                Assert.Equal(process.Pid, process.Pgid);
                Assert.Equal(process.Pid, getpgid(process.Pid));                  // the OS agrees
                Assert.NotEqual(getpgid(Environment.ProcessId), getpgid(process.Pid));
            }
            finally
            {
                process.KillTree();
                await WaitUntil(() => process.HasExited, timeoutMs: 5_000);
            }
        }
        finally
        {
            SystemManagedProcess.SetsidPathOverride = previous;
        }
    }

    /// <summary>
    /// The real setsid where there is one, otherwise a script that does the same thing to itself
    /// before exec-ing. Only used to force the Linux branch on a machine that has no setsid.
    /// </summary>
    private string SetsidStandIn()
    {
        foreach (var real in new[] { "/usr/bin/setsid", "/bin/setsid" })
            if (File.Exists(real))
                return real;

        string body =
            File.Exists("/usr/bin/python3")
                ? "exec /usr/bin/python3 -c 'import os,sys; os.setsid(); " +
                  "os.execv(sys.argv[1], sys.argv[1:])' \"$@\""
            : File.Exists("/usr/bin/perl")
                ? "exec /usr/bin/perl -e 'setpgrp(0,0); exec @ARGV or die' \"$@\""
                : null;

        if (body == null)
            return null;

        var path = WriteScript(body, "setsid-standin");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                   UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task ThePersistedRecordEndsUpCarryingTheProcessGroup_OnTheSetsidBranch()
    {
        // The deployment branch of the containment design, forced onto whatever machine runs this.
        //
        // Pgid resolves lazily, so the record written at launch is *born null* — the child has not
        // reached setsid(2) when Process.Start returns. That is expected and is not the bug; the
        // bug would be leaving it null forever, because the startup sweep has no Process handle and
        // dispatches on a non-null group. So this asserts the upgrade actually lands: after
        // reconciliation the persisted Pgid is the child's own pid, which is exactly what
        // KillTree needs to issue kill(-pgid).
        var standIn = SetsidStandIn();
        Assert.NotNull(standIn);

        var previous = SystemManagedProcess.SetsidPathOverride;
        SystemManagedProcess.SetsidPathOverride = standIn;
        try
        {
            Assert.True(SystemManagedProcess.CanCreateOwnGroup);

            var registry = new ManagedProcessRegistry(Path.Combine(_dir, "managed-processes.json"));
            var executor = new ManagedExecutor(registry);

            var job = NewJob();
            executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

            var release = Path.Combine(_dir, "release");
            var process = executor.Launch(
                job, WriteScript($"while [ ! -f '{release}' ]; do sleep 0.05; done"), _dir);
            try
            {
                // Recorded from the instant it launched, and — measured, not assumed — with no
                // group yet: the child is still between fork(2) and setsid(2). If this ever starts
                // failing, the record is resolving its group at launch and the refresh below has
                // stopped being the thing under test.
                var born = Assert.Single(registry.Load());
                Assert.Equal(process.Pid, born.Pid);
                Assert.Null(born.Pgid);

                await WaitUntil(() =>
                {
                    executor.Reap();
                    return registry.Load().Single().Pgid != null;
                }, timeoutMs: 5_000);

                var record = registry.Load().Single();
                var pgid = record.Pgid;
                Assert.NotNull(pgid);
                Assert.Equal(process.Pid, pgid);
                Assert.Equal(pgid.Value, getpgid(process.Pid));             // the OS agrees
                Assert.NotEqual(getpgid(Environment.ProcessId), pgid.Value);
                // UTC, and demonstrably not the raw Local ticks: on this machine the two differ by
                // the timezone offset, which is exactly what a DST change would introduce between
                // a crash and a restart even on a machine that is UTC today.
                Assert.Equal(ManagedProcessRegistry.UtcTicksOf(process.StartTime),
                             record.StartTimeTicks);
                Assert.InRange(new DateTime(record.StartTimeTicks, DateTimeKind.Utc),
                               DateTime.UtcNow.AddMinutes(-5), DateTime.UtcNow.AddSeconds(1));

                // And the sweep's own probe, which is what a restarted Relay would use, agrees.
                var probed = ManagedProcessRegistry.LiveProcessStartTime(process.Pid);
                Assert.NotNull(probed);
                Assert.True(
                    TimeSpan.FromTicks(Math.Abs(probed.Value.Ticks - record.StartTimeTicks))
                        <= ManagedProcessRegistry.StartTimeTolerance);
            }
            finally
            {
                File.WriteAllText(release, "");
                executor.Kill(job);
                await WaitUntil(() => process.HasExited, timeoutMs: 10_000);
            }

            // And once it has settled it is no longer a leftover.
            job.Status = JobStatus.Finished;
            executor.Reap();
            Assert.Empty(registry.Load());
        }
        finally
        {
            SystemManagedProcess.SetsidPathOverride = previous;
        }
    }

    [Fact]
    public async Task WithoutASetsidBranch_ThePersistedRecordStaysNull()
    {
        // The macOS half of the same interlock, asserted rather than skipped: with no group of our
        // own the record must never acquire one, because on that platform the child's pgid is
        // Relay's and kill(-pgid) would take Relay down.
        if (SystemManagedProcess.CanCreateOwnGroup)
            return;   // this platform has setsid; the test above is the one that applies here

        var registry = new ManagedProcessRegistry(Path.Combine(_dir, "managed-processes.json"));
        var executor = new ManagedExecutor(registry);

        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var release = Path.Combine(_dir, "release");
        var process = executor.Launch(
            job, WriteScript($"while [ ! -f '{release}' ]; do sleep 0.05; done"), _dir);
        try
        {
            for (var i = 0; i < 10; i++)
            {
                executor.Reap();
                await Task.Delay(25);
                Assert.Null(registry.Load().Single().Pgid);
            }
        }
        finally
        {
            File.WriteAllText(release, "");
            executor.Kill(job);
            await WaitUntil(() => process.HasExited, timeoutMs: 10_000);
        }
    }

    [Fact]
    public void CanCreateOwnGroup_TracksWhetherSetsidIsActuallyInstalled()
    {
        var setsidExists = File.Exists("/usr/bin/setsid") || File.Exists("/bin/setsid");

        Assert.Equal(setsidExists, SystemManagedProcess.CanCreateOwnGroup);

        // A deployment precondition, pinned here because nothing else would notice it break:
        // macOS's base system has no setsid, and Linux's util-linux does. On a Linux host without
        // it, every job silently loses its own process group and the startup leftover sweep
        // becomes a no-op — the containment design would be gone with no other symptom.
        Assert.Equal(!OperatingSystem.IsMacOS(), SystemManagedProcess.CanCreateOwnGroup);
    }

    #endregion

    #region Admission interlock

    [Fact]
    public async Task Launch_WithoutAdmission_Throws_AndSpawnsNothing()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();

        var marker = Path.Combine(_dir, "spawned");
        var script = WriteScript($"touch '{marker}'");

        Assert.Throws<InvalidOperationException>(() => executor.Launch(job, script, _dir));

        await Task.Delay(300);
        Assert.False(File.Exists(marker), "a process ran without a reservation");
    }

    [Fact]
    public async Task Launch_IntoASpentReservation_Throws_AndSpawnsNothing()
    {
        // The entry still exists -- its previous run's exit code is recorded and the job is still
        // active -- but it is no longer a reservation anything may be launched into. A presence
        // check would spawn here and only then discover Attach refusing it.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var spent = new FakeProcess();
        Assert.True(executor.Attach(job, spent));
        spent.Exit(0);
        executor.Reap();

        var marker = Path.Combine(_dir, "spawned");
        var script = WriteScript($"touch '{marker}'");

        Assert.Throws<InvalidOperationException>(() => executor.Launch(job, script, _dir));

        await Task.Delay(300);
        Assert.False(File.Exists(marker), "a process ran against a spent reservation");
    }

    [Fact]
    public async Task Launch_IntoACondemnedReservation_Throws_AndSpawnsNothing()
    {
        // Condemned but still holding a live process: the entry survives reconciliation, so a
        // presence check passes, yet Attach is guaranteed to refuse it.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var live = new FakeProcess();
        Assert.True(executor.Attach(job, live));
        executor.Kill(job);
        executor.Reap();

        var marker = Path.Combine(_dir, "spawned");
        var script = WriteScript($"touch '{marker}'");

        Assert.Throws<InvalidOperationException>(() => executor.Launch(job, script, _dir));

        await Task.Delay(300);
        Assert.False(File.Exists(marker), "a process ran against a condemned reservation");
    }

    [Fact]
    public void Launch_WhenTheReservationVanishesDuringSpawn_KillsTheProcessAndThrows()
    {
        // The reservation can be retired between admission and the process being up -- an abort,
        // typically. At that point we own a running process nothing is accounting for: leaving it
        // alive holds a GPU no ledger can ever reclaim.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var orphan = new FakeProcess();

        Assert.Throws<InvalidOperationException>(() => executor.Launch(job, _ =>
        {
            executor.Kill(job);    // retires the reservation while the process is "starting"
            executor.Reap();
            return orphan;
        }));

        Assert.Equal(1, orphan.KillCount);
    }

    [Fact]
    public async Task Launch_WhenTheReservationVanishesDuringSpawn_KillsARealProcessToo()
    {
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        var marker = Path.Combine(_dir, "orphan-alive");
        var script = WriteScript($"while true; do touch '{marker}'; sleep 0.1; done");

        IManagedProcess? spawned = null;
        Assert.Throws<InvalidOperationException>(() => executor.Launch(job, gpus =>
        {
            executor.Kill(job);
            executor.Reap();
            spawned = SystemManagedProcess.Start(script, _dir, gpus,
                                                 job.PathStdOut, job.PathStdErr);
            return spawned;
        }));

        await WaitUntil(() => File.Exists(marker), timeoutMs: 5_000);
        File.Delete(marker);
        await Task.Delay(500);
        Assert.False(File.Exists(marker), "the unaccounted process was left running");
        Assert.NotNull(spawned);
    }

    [Fact]
    public void Launch_WhenTheReservationIsReplacedDuringSpawn_KillsTheProcessAndThrows()
    {
        // Not the same case as "the reservation vanished". Here the job is aborted and re-queued
        // while its process is starting, so a *different* entry is booked under the same Job key
        // — present, usable and process-less, so presence-and-usability checks wave it through.
        // Adopting it points the ledger at the new entry's GPUs while the process runs on the old
        // entry's, leaving the GPUs actually in use free for the next job to be admitted onto.
        var executor = new ManagedExecutor();
        var job = NewGpuJob();
        var host = new ResourceTotals(8, 32, 2);
        executor.TryAdmit(job, host);

        var launchedWith = executor.GpuIndicesFor(job);
        Assert.Single(launchedWith);

        var orphan = new FakeProcess();
        IReadOnlyList<int>? rebooked = null;

        Assert.Throws<InvalidOperationException>(() => executor.Launch(job, gpus =>
        {
            executor.Kill(job);                 // aborted: the reservation is retired ...
            executor.Reap();
            executor.TryAdmit(job, host);       // ... and re-queued onto a fresh one
            rebooked = executor.GpuIndicesFor(job);
            return orphan;
        }));

        Assert.Equal(1, orphan.KillCount);

        // The replacement reservation is intact and was not handed the process.
        Assert.Equal(ClusterJobStatus.Pending, executor.GetStatus(job));
        Assert.NotNull(rebooked);
        Assert.Single(executor.LiveAllocations());
    }

    [Fact]
    public void Attach_WithoutAReservationToken_StillBindsToWhateverTheJobHolds()
    {
        // The public two-argument overload keeps its old meaning; only Launch, which knows which
        // reservation it read, opts into the identity check.
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        Assert.True(executor.Attach(job, new FakeProcess()));
        Assert.Equal(ClusterJobStatus.Running, executor.GetStatus(job));
    }

    [Fact]
    public void Launch_HandsTheProcessTheGpusItWasAdmittedWith()
    {
        var executor = new ManagedExecutor();
        var job = NewGpuJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 4));

        IReadOnlyList<int>? given = null;
        var process = executor.Launch(job, gpus => { given = gpus; return new FakeProcess(); });

        Assert.NotNull(process);
        Assert.Equal(executor.GpuIndicesFor(job), given);
        Assert.Single(given!);
    }

    #endregion
}
