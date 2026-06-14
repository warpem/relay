namespace Refund.JobQueues;

/// <summary>
/// Minimal interface over ClusterQueue used by WorkerPool.
/// Allows WorkerPool to be tested without a real cluster connection.
/// </summary>
public interface IPoolQueue
{
    Task<string> SubmitScript(string scriptPath);
    Task<HashSet<string>> ListActiveJobIds();
    Task CancelJobs(IEnumerable<string> jobIds);
    string BuildWorkerScript(string command, Dictionary<string, string> resourceValues,
                             string[] requiredModules, string scriptPath);
}
