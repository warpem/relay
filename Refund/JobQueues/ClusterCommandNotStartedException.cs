namespace Refund.JobQueues;

/// <summary>A local failure before the cluster command could have reached the scheduler.</summary>
public sealed class ClusterCommandNotStartedException : Exception
{
    public ClusterCommandNotStartedException(string message, Exception innerException = null)
        : base(message, innerException)
    {
    }
}
