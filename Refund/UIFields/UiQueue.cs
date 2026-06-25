namespace Refund.UIFields;

/// <summary>
/// Field attribute for a cluster queue selector. Stores a queue ID (int).
/// Renders as a dropdown populated directly from DataManager.ClusterQueues
/// (the view injects DataManager). A value of -1 means "none / local mode".
/// </summary>
public class UiQueue : UiFieldBase
{
    public override Type ViewType => typeof(UiQueueView);

    /// <summary>
    /// Creates a new queue picker field.
    /// </summary>
    /// <param name="label">Display label in the UI.</param>
    /// <param name="helpText">Optional tooltip text.</param>
    public UiQueue(string label, string helpText = "")
        : base("", label, helpText)
    {
    }
}
