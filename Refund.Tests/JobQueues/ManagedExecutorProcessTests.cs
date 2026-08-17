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
        var executor = new ManagedExecutor();
        var job = NewJob();
        executor.TryAdmit(job, new ResourceTotals(8, 32, 0));

        executor.Launch(job, WriteScript("for i in $(seq 1 500); do echo line-$i; done"), _dir);
        await WaitUntil(() => executor.GetStatus(job) == ClusterJobStatus.Finished);

        var written = await File.ReadAllTextAsync(job.PathStdOut);
        Assert.Contains("line-1\n", written);
        Assert.Contains("line-500", written);
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
        // The sleep is a detached grandchild, so it deliberately outlives every handle we hold and
        // (on macOS, which has no group to signal) survives KillTree. Kept short so it reaps itself.
        var script = WriteScript("( sleep 8 ) & echo done; exit 0");

        var process = SystemManagedProcess.Start(script, _dir, Array.Empty<int>(),
                                                 Path.Combine(_dir, "std.out"),
                                                 Path.Combine(_dir, "std.err"),
                                                 drainGrace: TimeSpan.FromSeconds(1));
        try
        {
            // The script itself exits in milliseconds; the pump cannot drain until the sleep does.
            await Task.Delay(400);
            Assert.False(process.HasExited);

            await WaitUntil(() => process.HasExited, timeoutMs: 5_000);
        }
        finally
        {
            process.KillTree();
        }
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
    public void KillTree_DoesNotGroupSignalAnExitedProcess()
    {
        // Same interlock on the group path: a reaped pid can have been recycled, and the recycled
        // process's group is somebody else's tree.
        var signals = new List<(int Pid, int Signal)>();

        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => Assert.Fail("should not fall back"),
                                      hasExited: () => true,
                                      signal: (p, s) => { signals.Add((p, s)); return 0; });

        Assert.Empty(signals);
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

        SystemManagedProcess.KillTree(pid: 4321, pgid: 4321,
                                      fallbackKill: () => fallbackCalled = true,
                                      hasExited: () => throw new InvalidOperationException("disposed"),
                                      signal: (_, _) => 0);

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
