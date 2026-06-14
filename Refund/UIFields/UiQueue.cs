namespace Refund.UIFields;

/// <summary>
/// Field attribute for a cluster queue selector. Stores a queue ID (int).
/// Renders as a dropdown populated from the available ClusterQueue objects
/// (supplied at render time via the field's DataDelegate -> AdditionalData).
/// A value of -1 means "none / local mode".
/// </summary>
public class UiQueue : UiFieldBase
{
    public override Type ViewType => typeof(UiQueueView);

    /// <summary>
    /// Creates a new queue picker field.
    /// </summary>
    /// <param name="label">Display label in the UI.</param>
    /// <param name="helpText">Optional tooltip text.</param>
    /// <param name="dataDelegateName">
    /// Name of a method on the declaring type that supplies the available queues
    /// (returns List&lt;(int id, string alias)&gt;). Wired in Task 8.
    /// </param>
    public UiQueue(string label, string helpText = "", string dataDelegateName = null)
        : base("", label, helpText, dataDelegateName: dataDelegateName)
    {
    }
}
