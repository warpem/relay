using System.Diagnostics;
using Refund.DataModel;
using Serilog;

namespace Refund.JobQueues;

/// <summary>
/// Runs jobs as local processes and accounts for the host's resources. Exactly one instance exists
/// per Relay host, owned by QueueRepository — a host has one set of GPUs, so it has one ledger.
/// </summary>
public sealed class ManagedExecutor
{
    private sealed class Entry
    {
        private static long _nextToken;

        /// <summary>
        /// Identifies this reservation, as opposed to the job it belongs to. _entries is keyed by
        /// Job, so a job that is aborted and re-admitted gets a <em>different</em> Entry under the
        /// same key — and a launch in flight across that swap must not be allowed to bind its
        /// process to the replacement. See <see cref="Launch(Job, Func{IReadOnlyList{int}, IManagedProcess})"/>.
        /// </summary>
        public long Token { get; } = Interlocked.Increment(ref _nextToken);

        public required ResourceAllocation Allocation { get; init; }
        public IManagedProcess Process { get; set; }
        public int? ExitCode { get; set; }

        /// <summary>
        /// Set once this entry has been marked for death, and never un-set. It survives the job
        /// becoming active again, which is what stops a re-queued job resurrecting the leftover
        /// process of its previous run; see <see cref="Condemn"/>.
        /// </summary>
        public bool Condemned { get; set; }

        /// <summary>
        /// Whether this entry is a reservation the job could still be launched into: not condemned,
        /// and not already spent by a process that has run and exited.
        /// </summary>
        public bool IsUsableReservation => !Condemned && ExitCode == null;

        /// <summary>
        /// What was last written to the leftover registry for this entry, or null if nothing was.
        /// Kept so a record persisted before the child had entered its process group can be
        /// upgraded once the group appears — see <see cref="RefreshRegistryPgid"/> — and so a
        /// retired entry only rewrites the file when it actually had a record to drop.
        /// </summary>
        public ManagedProcessRecord Record { get; set; }
    }

    /// <summary>
    /// Keyed by job identity, not by allocation: <see cref="ResourceAllocation"/> is a record whose
    /// GpuIndices compares by reference, so it has no usable value equality. Job does not override
    /// Equals either, which is exactly what is wanted here — one entry per job object.
    /// </summary>
    private readonly Dictionary<Job, Entry> _entries = new();

    private readonly object _sync = new();

    /// <summary>
    /// Where launched processes are persisted so a restarted Relay can kill what a crashed one left
    /// behind. Optional: every test that only exercises the accounting rules passes none.
    /// </summary>
    private readonly ManagedProcessRegistry _registry;

    /// <summary>
    /// Set once, never cleared. Volatile because the guards read it outside <see cref="_sync"/>.
    /// </summary>
    private volatile bool _shuttingDown;

    /// <summary>
    /// Leftovers from a crashed Relay that the startup sweep signalled but could not confirm dead.
    /// Guarded by <see cref="_sync"/>. Each one may still be holding a GPU that this executor's
    /// ledger believes is free, so nothing is admitted while any remain.
    /// </summary>
    private readonly List<ManagedProcessRecord> _unconfirmedLeftovers = new();

    /// <summary>
    /// Retries containment of one survivor, returning true once it is confirmed gone. Injected so
    /// the block-and-self-heal behaviour can be tested without real pids.
    /// </summary>
    private readonly Func<ManagedProcessRecord, bool> _containLeftover;

    /// <summary>Throttles the survivor warning; see <see cref="RetryLeftoversLocked"/>.</summary>
    private long _lastLeftoverWarningAt;

    public ManagedExecutor(ManagedProcessRegistry registry = null,
                           IEnumerable<ManagedProcessRecord> unconfirmedLeftovers = null,
                           Func<ManagedProcessRecord, bool> containLeftover = null)
    {
        _registry = registry;
        _containLeftover = containLeftover ?? ManagedProcessRegistry.RetryContainment;

        if (unconfirmedLeftovers != null)
            _unconfirmedLeftovers.AddRange(unconfirmedLeftovers);
    }

    /// <summary>
    /// Whether a leftover from a previous run is still, as far as anyone can prove, running on this
    /// host. While true nothing is admitted: the ledger cannot see that process's GPU.
    /// </summary>
    public bool HasUnconfirmedLeftovers
    {
        get { lock (_sync) return _unconfirmedLeftovers.Count > 0; }
    }

    /// <summary>
    /// Re-signal every retained survivor and drop the ones that have since died. A process that
    /// resisted the startup sweep and then exited — the job finished, an operator killed it — must
    /// release the admission block on its own, or a single failed kill would wedge the host until
    /// somebody restarted Relay.
    /// </summary>
    private void RetryLeftoversLocked()
    {
        if (_unconfirmedLeftovers.Count == 0)
            return;

        _unconfirmedLeftovers.RemoveAll(record =>
        {
            try { return _containLeftover(record); }
            catch (Exception exc)
            {
                Log.ForContext<ManagedExecutor>().Error(
                    exc, "Could not retry containment of leftover process {Pid} (job {JobId}).",
                    record.Pid, record.JobId);
                return false;
            }
        });

        if (_unconfirmedLeftovers.Count == 0)
        {
            Log.ForContext<ManagedExecutor>().Information(
                "Every leftover process from the previous run is now gone; managed queues are " +
                "admitting jobs again.");

            _registry?.ReplaceAll(_entries.Values.Select(e => e.Record).Where(r => r != null));
            return;
        }

        // Throttled: this runs on every daemon tick, and the reason has to be findable in the log
        // without drowning it. A student staring at a queue where nothing starts must be able to
        // read why.
        if (_lastLeftoverWarningAt != 0 &&
            Stopwatch.GetElapsedTime(_lastLeftoverWarningAt) < LeftoverWarningInterval)
            return;

        _lastLeftoverWarningAt = Stopwatch.GetTimestamp();

        Log.ForContext<ManagedExecutor>().Warning(
            "Managed queues are not admitting jobs: {Count} process(es) left over from a previous " +
            "run could not be killed and may still be using this host's GPUs — pid(s) {Pids}. " +
            "Relay retries on every daemon tick and resumes on its own once they are gone.",
            _unconfirmedLeftovers.Count,
            string.Join(", ", _unconfirmedLeftovers.Select(r => r.Pid)));
    }

    private static readonly TimeSpan LeftoverWarningInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Cores are per-process in the job model (<c>Job.CoreCount</c>, Job.cs:347) while memory is
    /// already a total in every override. Conflating the two silently over- or under-books the host.
    /// </summary>
    /// <remarks>
    /// Every dimension is clamped at zero. <see cref="ResourceLedger"/> is deliberately permissive
    /// and will fit a negative request, handing back an allocation that *adds* capacity once it is
    /// in the live set — a job declaring -4 cores would make the host report four cores more than
    /// it has. The two core factors are clamped separately rather than only their product, so a
    /// pair of negatives cannot multiply back into a plausible-looking positive.
    /// </remarks>
    public static ResourceRequest RequestFor(Job job)
    {
        // long, so a pathological ProcessCount x CoreCount cannot overflow into a negative int.
        long processes = Math.Max(0, job.ProcessCount);
        long coresPerProcess = Math.Max(0, job.CoreCount);
        long cores = Math.Min(processes * coresPerProcess, int.MaxValue);

        return new ResourceRequest((int)cores,
                                   Math.Max(0, job.MemoryGb),
                                   Math.Max(0, job.GpuCount));
    }

    /// <summary>
    /// Reserve resources for a job, or explain why not. Idempotent for a reservation belonging to
    /// the job's current run: re-offering it returns Admit without booking the host a second time.
    /// </summary>
    /// <remarks>
    /// A stale entry — one that is condemned, or whose process has already run and exited — is
    /// never reused. A job can be re-queued while the process of its previous run is still alive
    /// (Aborted -> Building -> Waiting is a legal transition, Job.cs:1346), and handing that run
    /// back the old entry would launch it against an allocation sized for the old parameters and
    /// feed it the old GPU indices. It is condemned instead, and the job waits for it to die.
    /// </remarks>
    public AdmissionResult TryAdmit(Job job, ResourceTotals totals)
    {
        // Busy, not Reject: the host is not permanently incapable of running this job, it is going
        // away. The daemon leaves a Busy job Waiting, so it is still queued after the restart.
        if (_shuttingDown)
            return AdmissionResult.IsBusy;

        var request = RequestFor(job);

        if (!ResourceLedger.CanEverFit(totals, request))
            return new AdmissionResult.Reject(
                $"Job needs {request.Cores} cores, {request.MemoryGb} GB and {request.Gpus} GPU(s); " +
                $"this queue has {totals.Cores} cores, {totals.MemoryGb} GB and {totals.Gpus} GPU(s), " +
                "so it can never run here.");

        lock (_sync)
        {
            // Busy, not Reject, and this is the important part. A leftover the startup sweep could
            // not confirm dead may still be holding a GPU the ledger below believes is free, so
            // admitting now would double-book it. But it is transient — the daemon's reap tick
            // re-kills survivors and drops the ones that have gone — so the job stays Waiting and
            // starts by itself, rather than failing permanently or wedging with no explanation.
            // The reason is logged, throttled, from RetryLeftoversLocked.
            if (_unconfirmedLeftovers.Count > 0)
                return AdmissionResult.IsBusy;

            Reconcile();

            if (_entries.TryGetValue(job, out var existing))
            {
                if (existing.IsUsableReservation)
                    return AdmissionResult.Admitted;   // this run's own reservation; idempotent

                // Leftover from a previous run of the same job, and two separate things have to
                // happen to it. Condemning marks it for retirement the instant its process exits,
                // whatever the job's status says — without that the job's new Waiting status would
                // make the entry look active forever and nothing would clean it up. (It does not
                // make Reconcile re-kill: Condemn signals at most once, by design.)
                Condemn(existing);
                Reconcile();

                // And the job has to be told to wait rather than given a fresh allocation, even
                // when the host has room for one. _entries is keyed by Job, so a new entry would
                // *replace* the one tracking the live process — orphaning it exactly as an Attach
                // overwrite would, only via the ledger instead.
                if (_entries.ContainsKey(job))
                    return AdmissionResult.IsBusy;     // still winding down; the daemon re-asks
            }

            if (!ResourceLedger.TryFit(totals, LiveAllocationsLocked(), request, out var allocation))
                return AdmissionResult.IsBusy;

            _entries[job] = new Entry { Allocation = allocation };
            return AdmissionResult.Admitted;
        }
    }

    /// <summary>
    /// The GPU indices this job was given, for CUDA_VISIBLE_DEVICES. Empty unless the job holds a
    /// reservation it could still be launched into — the same rule <see cref="Attach"/> applies,
    /// so a caller can never read devices belonging to a run that is over.
    /// </summary>
    public IReadOnlyList<int> GpuIndicesFor(Job job)
    {
        lock (_sync)
        {
            Reconcile();

            return _entries.TryGetValue(job, out var e) && e.IsUsableReservation
                       ? e.Allocation.GpuIndices
                       : Array.Empty<int>();
        }
    }

    /// <summary>
    /// Bind a freshly spawned process to its reservation. Only ever binds to a reservation that
    /// has no process yet.
    /// </summary>
    /// <returns>
    /// False if there is nothing to bind to, for any of three reasons: the reservation was retired
    /// while the process was starting; it already holds a process; or it has been condemned or
    /// spent. In every case the caller owns an unaccounted process and must kill it rather than
    /// leave it running outside the ledger.
    /// <para>
    /// The last two reasons each close an orphan route. Overwriting a live
    /// <see cref="Entry.Process"/> would make the old one unreachable, so nothing would ever kill
    /// it, reap it, or account for the GPU it holds. Adopting into a condemned entry is just as
    /// bad the other way round: <see cref="Condemn"/> has already fired, so the adopted process
    /// would never be signalled at all, and its booking would stand until it happened to exit on
    /// its own.
    /// </para>
    /// </returns>
    public bool Attach(Job job, IManagedProcess process) => Attach(job, process, reservation: null);

    /// <summary>
    /// Overload that binds to one specific reservation rather than to whatever the job holds now.
    /// </summary>
    /// <param name="reservation">
    /// The <see cref="Entry.Token"/> the caller reserved against, or null to accept the job's
    /// current reservation whatever it is.
    /// <para>
    /// Passing it closes a fourth orphan route that the presence-and-usability checks cannot see.
    /// A job that is aborted and re-queued while its process is starting has its old entry retired
    /// and a <em>new</em> one booked under the same Job key, and that new entry is present, usable
    /// and process-less — so it accepts the attach. The process then runs on the old entry's GPUs
    /// while the ledger believes it is on the new entry's, leaving the GPUs it is really using free
    /// to be handed to somebody else.
    /// </para>
    /// </param>
    internal bool Attach(Job job, IManagedProcess process, long? reservation)
    {
        lock (_sync)
        {
            // The third door, closed for the same reason as TryAdmit and Launch. Adopting a process
            // now would put it in the table after KillAllAsync had taken its snapshot, so nothing
            // would ever signal it; refusing hands it back to the caller, whose contract is to kill
            // what it cannot get accounted for.
            if (_shuttingDown)
                return false;

            if (!_entries.TryGetValue(job, out var entry))
                return false;

            if (reservation is { } token && entry.Token != token)
                return false;

            if (entry.Process != null || !entry.IsUsableReservation)
                return false;

            entry.Process = process;
            return true;
        }
    }

    /// <summary>
    /// Spawns the job's script and attaches the resulting process to its (already admitted) entry.
    /// Throws if the job holds no reservation it could be launched into — a process must never run
    /// unaccounted for.
    /// </summary>
    public IManagedProcess Launch(Job job, string scriptPath, string workingDirectory) =>
        Launch(job, gpus => SystemManagedProcess.Start(scriptPath, workingDirectory, gpus,
                                                       job.PathStdOut, job.PathStdErr));

    /// <summary>Overload with the spawn injected, so the failure paths can be tested.</summary>
    /// <remarks>
    /// The spawn deliberately happens outside the lock — starting a process is slow, and holding
    /// the host-wide lock across it would stall status polling for every other job.
    /// </remarks>
    internal IManagedProcess Launch(Job job, Func<IReadOnlyList<int>, IManagedProcess> spawn)
    {
        // Checked before the spawn, and again below under the lock. A staging task that was already
        // in flight when shutdown began arrives here with a perfectly valid reservation, and the
        // kill sweep has no way to find a process that does not exist yet.
        if (_shuttingDown)
            throw new InvalidOperationException(
                $"Relay is shutting down; refusing to launch job {job.Id}.");

        IReadOnlyList<int> gpus;
        long reservation;
        lock (_sync)
        {
            // Presence is not enough. An entry can exist while condemned, or spent by a process
            // that has already run and exited, and <see cref="Attach"/> refuses both — so a
            // presence check would spawn a real process only to find nothing will account for it.
            if (!_entries.TryGetValue(job, out var entry) || !entry.IsUsableReservation)
                throw new InvalidOperationException(
                    $"Job {job.Id} holds no usable reservation; refusing to launch it unaccounted for.");

            gpus = entry.Allocation.GpuIndices;

            // Which reservation these GPUs came from, not just which job. The lock is dropped
            // across the spawn below, and the job can be aborted and re-admitted in that window.
            reservation = entry.Token;
        }

        var process = spawn(gpus);

        if (!Attach(job, process, reservation))
        {
            // The reservation was retired, or replaced by a later one, while the process was
            // starting — an abort, typically. We now own a running process nothing is accounting
            // for, and leaving it alive would hold cores, memory and GPUs that no ledger can ever
            // reclaim, on top of whatever the replacement reservation booked.
            //
            // Shutdown starting mid-spawn arrives here too, and must: Attach refuses once shutdown
            // has begun, so a process that came up after KillAllAsync took its snapshot is killed
            // by the caller that owns it rather than left with nothing to signal it.
            KillUnaccounted(job, process);

            throw new InvalidOperationException(
                $"Job {job.Id}'s reservation was retired or replaced while its process was " +
                "starting; the process has been killed rather than left running unaccounted for.");
        }

        RecordLaunch(job, process);

        return process;
    }

    private static void KillUnaccounted(Job job, IManagedProcess process)
    {
        try
        {
            process.KillTree();
        }
        catch (Exception exc)
        {
            Log.ForContext<ManagedExecutor>().Error(
                exc, "Could not kill the unaccounted process {Pid} of job {JobId}; " +
                     "it may still be holding this host's resources.", process.Pid, job.Id);
        }
    }

    /// <summary>
    /// Persist the launched process so a restarted Relay can kill it if this one dies first.
    /// Written only once the process is attached: an attach that failed has already killed it, and
    /// recording under the job's id would clobber the record of whichever run displaced it.
    /// </summary>
    /// <remarks>
    /// <b>Pgid is read here, at persist time, and re-read later if it is still null.</b> It resolves
    /// lazily against the OS (see <see cref="SystemManagedProcess.Pgid"/>) because the child has not
    /// finished <c>execve</c> and <c>setsid</c> when <c>Process.Start</c> returns — on Linux this
    /// read loses that race often, and on a fast path essentially always. A null latched into the
    /// file would make the startup sweep fall back to a tree walk it cannot do without a Process
    /// handle, i.e. into nothing, on the one platform that has process groups at all.
    /// </remarks>
    private void RecordLaunch(Job job, IManagedProcess process)
    {
        if (_registry == null)
            return;

        // Space and Project can be absent in tests and in a job that was never filed; -1 is then
        // simply another identity, and every record of such a job shares it consistently.
        var record = new ManagedProcessRecord(job.Space?.Project?.Id ?? -1,
                                              job.Space?.Id ?? -1,
                                              job.Id,
                                              process.Pid,
                                              PgidOf(process),
                                              ManagedProcessRegistry.UtcTicksOf(process.StartTime),
                                              // The exact identity, where the platform has one.
                                              // Read now, while the process is certainly alive.
                                              ManagedProcessRegistry.StartTokenOf(process.Pid));

        lock (_sync)
        {
            // Only if this process is still the tracked one. Between the attach above and here the
            // entry can have been retired, and writing then would leave a record no Forget clears.
            if (!_entries.TryGetValue(job, out var entry) || !ReferenceEquals(entry.Process, process))
                return;

            entry.Record = record;
            _registry.Record(record);
        }
    }

    /// <summary>The group we created for this process, or null — including for every non-system
    /// process, which by definition has no group of ours.</summary>
    private static int? PgidOf(IManagedProcess process) =>
        process is SystemManagedProcess system ? system.Pgid : null;

    /// <summary>
    /// Re-read the process group of an entry whose record was written before the child had entered
    /// it, and upgrade the persisted record once the OS confirms one. Without this the file keeps
    /// the null it was necessarily born with and the startup sweep can never group-kill anything.
    /// </summary>
    private void RefreshRegistryPgid(Entry entry)
    {
        if (_registry == null || entry.Record is not { Pgid: null } stale)
            return;

        if (PgidOf(entry.Process) is not { } pgid)
            return;

        entry.Record = stale with { Pgid = pgid };
        _registry.Record(entry.Record);
    }

    private void ForgetRegistryRecord(Entry entry)
    {
        if (_registry == null || entry.Record == null)
            return;

        _registry.Forget(entry.Record.ProjectId, entry.Record.SpaceId, entry.Record.JobId);
        entry.Record = null;
    }

    /// <summary>
    /// Stops admitting and stops launching. Call before killing anything: an entry admitted but not
    /// yet launched has no process to find, and its staging task would otherwise spawn one after
    /// the sweep had already passed.
    /// </summary>
    public void BeginShutdown() => _shuttingDown = true;

    /// <summary>Kills every tracked process tree and waits for them to actually exit.</summary>
    /// <remarks>
    /// Admission closes first, and it closes inside this method rather than being left to the
    /// caller — the ordering is the correctness property, so it must not be possible to get it
    /// wrong from outside.
    /// </remarks>
    public async Task KillAllAsync()
    {
        BeginShutdown();

        List<IManagedProcess> processes;
        lock (_sync)
            processes = _entries.Values.Select(e => e.Process).Where(p => p != null).ToList();

        var signalled = new List<IManagedProcess>();

        foreach (var process in processes)
        {
            // One failure must not leave the rest of the host's processes unsignalled.
            try { process.KillTree(); signalled.Add(process); }
            catch (Exception exc)
            {
                Log.ForContext<ManagedExecutor>().Error(
                    exc, "Could not kill managed process {Pid} during shutdown.", process.Pid);
            }
        }

        // Only the ones that were actually signalled: awaiting a process the kill never reached
        // would block shutdown for as long as it keeps computing.
        var dead = new List<IManagedProcess>();

        await Task.WhenAll(signalled.Select(async p =>
        {
            try
            {
                await p.WaitForExitAsync();
                lock (dead) dead.Add(p);
            }
            catch (Exception exc)
            {
                Log.ForContext<ManagedExecutor>().Warning(
                    exc, "Managed process {Pid} did not report its exit during shutdown.", p.Pid);
            }
        }));

        lock (_sync)
        {
            // Drop only what is confirmed dead: a process whose kill or wait threw above may still
            // be alive on the host, and dropping its record would leave the next startup's sweep
            // nothing to find — a GPU held by something nothing tracks. What stays behind is
            // precisely the leftover the registry exists to hand to the next Relay.
            foreach (var (job, entry) in _entries.ToList())
            {
                bool confirmed = entry.Process == null ||          // never launched; nothing to sweep
                                 dead.Any(p => ReferenceEquals(p, entry.Process));
                if (!confirmed)
                    continue;

                _entries.Remove(job);
                ForgetRegistryRecord(entry);
            }
        }
    }

    public void Reap()
    {
        lock (_sync)
        {
            // Before reconciling, so a host wedged by an uncontainable leftover un-wedges itself as
            // soon as that process dies, without a Relay restart.
            RetryLeftoversLocked();
            Reconcile();
        }
    }

    /// <summary>
    /// A stable snapshot of what is currently held, suitable for handing to
    /// <see cref="ResourceLedger"/> — materialised under the lock, not lazy over the entry table.
    /// </summary>
    public IEnumerable<ResourceAllocation> LiveAllocations()
    {
        lock (_sync)
        {
            Reconcile();
            return LiveAllocationsLocked().ToList();
        }
    }

    public bool HasEntries(Func<Job, bool> predicate)
    {
        lock (_sync)
        {
            Reconcile();
            return _entries.Keys.Any(predicate);
        }
    }

    public ClusterJobStatus GetStatus(Job job)
    {
        lock (_sync)
        {
            Reconcile();

            if (!_entries.TryGetValue(job, out var entry))
                return ClusterJobStatus.Failed;     // untracked: nothing is running this

            if (entry.ExitCode is { } code)
                return code == 0 ? ClusterJobStatus.Finished : ClusterJobStatus.Failed;

            return entry.Process == null ? ClusterJobStatus.Pending : ClusterJobStatus.Running;
        }
    }

    /// <summary>
    /// Signal a job's process to stop. Condemns the entry, so it is retired as soon as the process
    /// dies even if the job's status has moved on in the meantime.
    /// </summary>
    /// <remarks>
    /// Signals at most once per entry; escalating an unresponsive process belongs inside
    /// <see cref="IManagedProcess.KillTree"/>, not in a caller that re-signals on a timer.
    /// </remarks>
    public void Kill(Job job)
    {
        lock (_sync)
            if (_entries.TryGetValue(job, out var entry))
                Condemn(entry);
    }

    /// <summary>
    /// Mark an entry for death and signal its process, at most once. Idempotent, and sticky: an
    /// entry never becomes un-condemned, so the job going active again cannot rescue it.
    /// </summary>
    private static void Condemn(Entry entry)
    {
        if (entry.Condemned)
            return;

        entry.Condemned = true;
        entry.Process?.KillTree();
    }

    /// <summary>
    /// The single reconciliation pass. Everything that frees a resource happens here, which is why
    /// there is no Release() for any exit path to forget.
    /// </summary>
    /// <remarks>
    /// The order of the cases is load-bearing. A running process always keeps its allocation,
    /// whatever the job's status says; status (or condemnation) can retire only an entry with no
    /// live process.
    /// </remarks>
    private void Reconcile()
    {
        foreach (var (job, entry) in _entries.ToList())
        {
            // Per entry, because enumeration order is stable: a throw that escaped this loop would
            // abort the whole pass, and then the same entry would abort every later pass too —
            // nothing after it would ever be reconciled again and every allocation behind it would
            // strand. Reconciliation is the one path that frees resources, so it has to survive one
            // sick entry.
            try { ReconcileEntry(job, entry); }
            catch (Exception exc)
            {
                Log.ForContext<ManagedExecutor>().Error(
                    exc, "Could not reconcile managed entry for job {JobId}; continuing with the rest.",
                    job.Id);
            }
        }
    }

    /// <summary>One entry's share of <see cref="Reconcile"/>; see that method's remarks for the ordering.</summary>
    private void ReconcileEntry(Job job, Entry entry)
    {
        if (entry.Process is { HasExited: true } exited)
        {
            entry.ExitCode ??= exited.ExitCode;         // resources free from here (see LiveAllocationsLocked)
            if (entry.Condemned || !IsJobActive(job))
            {
                _entries.Remove(job);                   // settled or condemned; forget it entirely
                ForgetRegistryRecord(entry);            // and it is no longer a leftover to sweep
            }
            return;
        }

        // Live process: the one moment its process group can be confirmed. Cheap and
        // self-limiting — it is a syscall only while a record is still carrying a null.
        RefreshRegistryPgid(entry);

        if (!entry.Condemned && IsJobActive(job))
            return;

        if (entry.Process != null)
        {
            // Terminal or condemned, live process. Never free here: HandleAbortingState
            // force-marks a job Aborted after 30s whether or not the kill landed, and releasing
            // would hand a still-computing job's GPU to someone else. Condemn and wait for the
            // exit. This runs on every pass, but it signals only on the first: what the flag
            // buys on later passes is that the entry is retired the moment its process exits
            // (see the branch above) even if the job has gone active again in the meantime.
            Condemn(entry);
            return;
        }

        _entries.Remove(job);                           // abandoned reservation, no process
        ForgetRegistryRecord(entry);                    // (normally none: it never launched)
    }

    private static bool IsJobActive(Job job) =>
        job.Status.IsUnsettled() || job.Status == JobStatus.Waiting;

    /// <summary>An entry holds resources until its process has exited; see Reconcile.</summary>
    private IEnumerable<ResourceAllocation> LiveAllocationsLocked() =>
        _entries.Values.Where(e => e.ExitCode == null).Select(e => e.Allocation);
}
