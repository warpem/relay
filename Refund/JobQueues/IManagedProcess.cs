namespace Refund.JobQueues;

/// <summary>
/// The bit of an OS process the executor needs. Exists so the reconciliation rules — which is where
/// the resource-accounting bugs live — can be tested deterministically without spawning anything.
/// </summary>
/// <remarks>
/// <para><b>Threading contract. Implementations must honour all three points.</b></para>
/// <para>
/// 1. Every member except <see cref="WaitForExitAsync"/> is called while
/// <c>ManagedExecutor</c> holds its single host-wide lock, and must therefore return promptly.
/// <see cref="HasExited"/> and <see cref="KillTree"/> in particular must not block on process exit,
/// on draining output pumps, or on any grace period: doing so stalls admission and status polling
/// for every other job on the host for as long as it takes.
/// </para>
/// <para>
/// 2. No member may call back into the executor. The lock is re-entrant, so a re-entrant call
/// would not deadlock outright — it would observe a half-reconciled entry table, which is worse.
/// </para>
/// <para>
/// 3. Members must be safe to call from any thread, and <see cref="KillTree"/> must tolerate being
/// called on a process that has already exited. The executor signals a condemned process exactly
/// once, but <see cref="Kill"/>-style requests can arrive from the UI at any moment.
/// </para>
/// <para>
/// Escalating an unresponsive process — SIGTERM, then SIGKILL after a grace period — belongs
/// inside a <see cref="KillTree"/> implementation that arranges it asynchronously, not in a caller
/// re-signalling on every reconciliation pass.
/// </para>
/// </remarks>
public interface IManagedProcess
{
    int Pid { get; }

    /// <summary>Start time, paired with the pid to survive pid recycling across a Relay restart.</summary>
    DateTime StartTime { get; }

    bool HasExited { get; }

    /// <summary>Only meaningful once <see cref="HasExited"/> is true.</summary>
    int ExitCode { get; }

    /// <summary>
    /// Terminate the whole tree, not just the direct child — jobs launch mpirun. Must signal and
    /// return; see the threading contract on the interface.
    /// </summary>
    void KillTree();

    /// <summary>
    /// The one member the executor never calls under its lock, and so the only one that may block.
    /// </summary>
    Task WaitForExitAsync(CancellationToken ct = default);
}
