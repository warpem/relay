namespace Refund.Tests;

[CollectionDefinition("JobRegistry", DisableParallelization = true)]
public class JobRegistryCollection { }

internal static class JobRegistry
{
    private static readonly object PopulateLock = new();

    public static void EnsurePopulated()
    {
        lock (PopulateLock)
        {
            if (global::Refund.DataModel.Job.Types.Count == 0)
                global::Refund.DataModel.Job.PopulateStatic();
        }
    }
}
