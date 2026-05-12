using Refund.DataModel.ReadOnly;

namespace Relay.Screens.Main.View;

/// <summary>
/// Scoped service tracking drag-and-drop state for items in the view screen.
/// </summary>
public class ViewDragDropService
{
    public List<IViewItem> DraggedItems { get; private set; } = new();
    public bool IsDragging => DraggedItems.Count > 0;

    public event Action OnDragStateChanged;

    public void StartDrag(IEnumerable<IViewItem> items)
    {
        DraggedItems = items.ToList();
        OnDragStateChanged?.Invoke();
    }

    public void EndDrag()
    {
        DraggedItems.Clear();
        OnDragStateChanged?.Invoke();
    }
}
