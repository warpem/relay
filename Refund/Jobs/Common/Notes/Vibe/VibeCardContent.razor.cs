using Microsoft.AspNetCore.Components;
using Refund.DataModel.ReadOnly;

namespace Refund.Jobs.Common.Notes.Vibe;

/// <summary>
/// Component responsible for rendering the Vibe job as a card in the workflow view.
/// This component is instantiated dynamically by the job rendering system based on the
/// CardViewType property of the Vibe job.
/// 
/// The component is referenced directly in the Vibe job class:
/// <code>public override Type CardViewType => typeof(VibeCardContent);</code>
/// which enables the workflow system to automatically create and render this component
/// when displaying a Vibe job in the workflow view.
/// </summary>
public partial class VibeCardContent : ComponentBase
{
    /// <summary>
    /// The ReadOnlyVibe job instance that this card represents.
    /// This parameter is injected by the job rendering system to connect
    /// the UI component with its underlying data model.
    /// </summary>
    [Parameter]
    public ReadOnlyVibe Job { get; set; }
}