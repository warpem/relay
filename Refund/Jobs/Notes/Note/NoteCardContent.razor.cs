using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;

namespace Refund.Jobs.Notes.Note;

/// <summary>
/// Component for displaying a Note job in the workflow card view.
/// </summary>
/// <remarks>
/// This component is specifically referenced by the Note job class through its
/// CardViewType property:
/// 
/// ```csharp
/// // In Note.cs:
/// public override Type CardViewType => typeof(NoteCardContent);
/// ```
/// 
/// When a Note job is displayed in the workflow view, the application's component
/// factory uses this reference to instantiate the appropriate card view. This pattern
/// enables specialized job-specific card displays while maintaining a consistent
/// interface through the job system.
/// 
/// The component is designed to display the Note's text content in a compact, 
/// card-sized format suitable for the workflow visualization. It integrates with
/// the workflow UI system through the ReadOnlyNote parameter that provides access 
/// to the job's properties.
/// </remarks>
public partial class NoteCardContent : ComponentBase
{
    /// <summary>
    /// The Note job to display in this card component.
    /// </summary>
    /// <remarks>
    /// This parameter contains the read-only wrapper for the Note job, providing
    /// access to the job's properties like ProcessingNote for display in the card.
    /// The parameter is bound by the workflow system when this component is
    /// instantiated to display a specific Note job.
    /// 
    /// The component relies on this parameter to access the Note's content and
    /// render it appropriately within the card's limited space. This parameter
    /// binding is part of the standard pattern for job-specific card components.
    /// </remarks>
    [Parameter]
    public ReadOnlyNote Job { get; set; }
}