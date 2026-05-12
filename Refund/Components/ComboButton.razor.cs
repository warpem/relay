using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Refund.Components;

/// <summary>
/// A combined button component that features both a primary action button and a dropdown menu.
/// This component provides a space-efficient way to expose a primary action and related secondary actions.
/// </summary>
public partial class ComboButton : ComponentBase
{
    /// <summary>
    /// Gets the unique ID for the main button element.
    /// </summary>
    private string MainButtonId => $"main-btn-{_id}";
    
    /// <summary>
    /// Gets the unique ID for the dropdown button element.
    /// </summary>
    private string DropdownButtonId => $"dropdown-btn-{_id}";

    /// <summary>
    /// Content to display within the primary button.
    /// </summary>
    [Parameter] public RenderFragment ChildContent { get; set; }
    
    /// <summary>
    /// Content to display in the dropdown menu.
    /// </summary>
    [Parameter] public RenderFragment MenuContent { get; set; }
    
    /// <summary>
    /// Icon to display at the beginning of the button.
    /// </summary>
    [Parameter] public Icon IconStart { get; set; }
    
    /// <summary>
    /// Icon to display at the end of the button.
    /// </summary>
    [Parameter] public Icon IconEnd { get; set; }
    
    /// <summary>
    /// Whether the button should display a loading indicator.
    /// </summary>
    [Parameter] public bool Loading { get; set; }
    
    /// <summary>
    /// Whether the button is disabled and cannot be interacted with.
    /// </summary>
    [Parameter] public bool Disabled { get; set; }
    
    /// <summary>
    /// The visual appearance style of the button (Accent, Lightweight, Neutral, etc.).
    /// </summary>
    [Parameter] public Appearance? Appearance { get; set; } = Microsoft.FluentUI.AspNetCore.Components.Appearance.Neutral;
    
    /// <summary>
    /// Custom text color for the button.
    /// </summary>
    [Parameter] public string? Color { get; set; }
    
    /// <summary>
    /// Custom background color for the button.
    /// </summary>
    [Parameter] public string? BackgroundColor { get; set; }
    
    /// <summary>
    /// Controls the horizontal positioning of the dropdown menu relative to the button.
    /// </summary>
    [Parameter] public HorizontalPosition? HorizontalPosition { get; set; }
    
    /// <summary>
    /// Controls the vertical positioning of the dropdown menu relative to the button.
    /// </summary>
    [Parameter] public VerticalPosition VerticalPosition { get; set; } = VerticalPosition.Bottom;
    
    /// <summary>
    /// Whether the button should automatically receive focus when rendered.
    /// </summary>
    [Parameter] public bool AutoFocus { get; set; } = false;
    
    /// <summary>
    /// Whether the dropdown menu is currently open.
    /// </summary>
    [Parameter] public bool IsOpen { get; set; } = false;
    
    /// <summary>
    /// Maximum width of the button in pixels.
    /// </summary>
    [Parameter] public int ButtonMaxWidth { get; set; } = 150;
    
    /// <summary>
    /// Minimum width of the button in pixels.
    /// </summary>
    [Parameter] public int ButtonMinWidth { get; set; } = 50;
    
    /// <summary>
    /// Additional custom CSS styles to apply to the component.
    /// </summary>
    [Parameter] public string? Style { get; set; }
    
    /// <summary>
    /// Event callback triggered when the main button is clicked.
    /// </summary>
    [Parameter] public EventCallback<MouseEventArgs> OnClick { get; set; }

    /// <summary>
    /// Optional navigation URL. When set, wraps the main button in an &lt;a&gt; tag
    /// to enable native browser link behavior (middle-click, Ctrl+click, right-click "Open in new tab").
    /// </summary>
    [Parameter] public string Href { get; set; }
    
    /// <summary>
    /// Unique identifier for this component instance.
    /// </summary>
    private Guid _id = Guid.NewGuid();
    
    /// <summary>
    /// Handles clicks on the primary part of the combo button.
    /// If OnClick has a delegate, invokes the main button click handler;
    /// otherwise, toggles the dropdown menu.
    /// </summary>
    /// <param name="args">Mouse event arguments</param>
    private async Task HandlePrimaryClick(MouseEventArgs args)
    {
        if (OnClick.HasDelegate)
            await OnMainButtonClick(args);
        else
            await OnDropdownButtonClick();
    }

    /// <summary>
    /// Handles the main button click by invoking the OnClick callback.
    /// </summary>
    /// <param name="args">Mouse event arguments</param>
    private async Task OnMainButtonClick(MouseEventArgs args)
    {
        if (OnClick.HasDelegate)
            await OnClick.InvokeAsync(args);
    }

    /// <summary>
    /// Toggles the dropdown menu's open state.
    /// </summary>
    private async Task OnDropdownButtonClick()
    {
        IsOpen = !IsOpen;
    }
}