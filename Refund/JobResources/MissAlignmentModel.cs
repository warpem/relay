using Refund.DataModel;

namespace Refund.JobResources;

public class MissAlignmentModel : Resource
{
    // Final checkpoint for seeding another training run.
    public string ModelPath { get; set; }

    // Inference reuses every per-iteration checkpoint and the original schedule/patch settings.
    public string ModelRunDirectory { get; set; }
    public string ConfigPath { get; set; }
}