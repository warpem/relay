using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Refund.Components;

/// <summary>
/// A button component that opens a dropdown listbox when clicked.
/// Combines a button with a <see cref="FilteredListbox{TValue}"/> for convenient option selection.
/// </summary>
/// <typeparam name="TValue">The type of value stored in the listbox options</typeparam>
public partial class ListboxButton<TValue> : ComponentBase
{
    /// <summary>
    /// Flag indicating whether the dropdown list is currently open.
    /// </summary>
    private bool _isOpen = false;
    
    /// <summary>
    /// Gets or sets the text displayed on the button.
    /// </summary>
    [Parameter] public string ButtonLabel { get; set; } = "";
    
    /// <summary>
    /// Collection of options to display in the dropdown listbox.
    /// Each option is a tuple containing the value and display text.
    /// </summary>
    [Parameter] public IEnumerable<(TValue Value, string Text)> Options { get; set; } = Enumerable.Empty<(TValue, string)>();
     
    /// <summary>
    /// Icon to display at the start of the button (left side).
    /// </summary>
    [Parameter] public Icon ButtonIconStart { get; set; }
    
    /// <summary>
    /// Icon to display at the end of the button (right side).
    /// </summary>
    [Parameter] public Icon ButtonIconEnd { get; set; }
    
    /// <summary>
    /// Gets or sets whether the button should display a loading indicator.
    /// </summary>
    [Parameter] public bool ButtonLoading { get; set; }
    
    /// <summary>
    /// Gets or sets whether the button is disabled.
    /// When true, the button cannot be clicked and the dropdown cannot be opened.
    /// </summary>
    [Parameter] public bool ButtonDisabled { get; set; }
    
    /// <summary>
    /// Sets the visual appearance of the button.
    /// Uses FluentUI's Appearance enum for styling.
    /// </summary>
    [Parameter] public Appearance? ButtonAppearance { get; set; } = Appearance.Neutral;
    
    /// <summary>
    /// Gets or sets the text color of the button.
    /// </summary>
    [Parameter] public string? ButtonColor { get; set; }
    
    /// <summary>
    /// Gets or sets the background color of the button.
    /// </summary>
    [Parameter] public string? ButtonBackgroundColor { get; set; }
    
    /// <summary>
    /// Maximum width of the button in pixels.
    /// </summary>
    [Parameter] public int ButtonMaxWidth { get; set; } = 150;
    
    /// <summary>
    /// Minimum width of the button in pixels.
    /// </summary>
    [Parameter] public int ButtonMinWidth { get; set; } = 50;
    
    /// <summary>
    /// Additional CSS styles to apply to the button.
    /// </summary>
    [Parameter] public string? ButtonStyle { get; set; }
    
    /// <summary>
    /// Controls the horizontal positioning of the dropdown list.
    /// Uses FluentUI's HorizontalPosition enum for alignment.
    /// </summary>
    [Parameter] public HorizontalPosition ListHorizontalPosition { get; set; } = HorizontalPosition.Left;
    
    /// <summary>
    /// Controls the vertical positioning of the dropdown list.
    /// Uses FluentUI's VerticalPosition enum for alignment.
    /// </summary>
    [Parameter] public VerticalPosition ListVerticalPosition { get; set; } = VerticalPosition.Bottom;
    
    /// <summary>
    /// When true, the dropdown list will automatically receive focus when opened.
    /// </summary>
    [Parameter] public bool ListAutoFocus { get; set; } = false;
    
    /// <summary>
    /// Width of the dropdown list in pixels.
    /// </summary>
    [Parameter] public int ListWidth { get; set; } = 150;
    
    /// <summary>
    /// Height of the dropdown list in pixels.
    /// </summary>
    [Parameter] public int ListHeight { get; set; } = 300;
    
    /// <summary>
    /// When true, the dropdown list will include a filter box to search options.
    /// When false, a simple list without filtering is displayed.
    /// </summary>
    [Parameter] public bool IsFiltered { get; set; } = true;
    
    /// <summary>
    /// Event callback that is triggered when the button is clicked.
    /// </summary>
    [Parameter] public EventCallback<MouseEventArgs> OnClick { get; set; }
    
    /// <summary>
    /// Event callback that is triggered when an option is selected from the dropdown list.
    /// </summary>
    [Parameter] public EventCallback<TValue> OnOptionSelected { get; set; }

    /// <summary>
    /// Optional navigation URL passed through to ComboButton.
    /// When set, wraps the main button in an &lt;a&gt; tag for native link behavior.
    /// </summary>
    [Parameter] public string Href { get; set; }

    /// <summary>
    /// Optional function that maps a dropdown option value to a navigation URL.
    /// When set, each dropdown option is wrapped in an &lt;a&gt; tag for native link behavior
    /// (middle-click opens in background tab, right-click shows "Open in new tab").
    /// </summary>
    [Parameter] public Func<TValue, string> HrefSelector { get; set; }

    [Inject] private IJSRuntime JsRuntime { get; set; }

    /// <summary>
    /// Custom template for rendering each option item in the dropdown list.
    /// If not provided, default rendering will be used.
    /// </summary>
    [Parameter] public RenderFragment<(TValue Value, string Text)> OptionTemplate { get; set; }

    /// <summary>
    /// Unique identifier for the component instance, used for DOM element identification.
    /// </summary>
    private string _id = Guid.NewGuid().ToString();

    /// <summary>
    /// Handles selection of an option from the dropdown list.
    /// Closes the dropdown and triggers the OnOptionSelected event.
    /// </summary>
    /// <param name="value">The value of the selected option</param>
    private void OnListValueChanged(TValue value)
    {
        _isOpen = false;
        StateHasChanged();

        OnOptionSelected.InvokeAsync(value);
    }

    /// <summary>
    /// Handles clicks on option links when HrefSelector is set.
    /// Normal click: SPA navigation via OnListValueChanged.
    /// Modifier click: opens in new tab via window.open.
    /// </summary>
    private async Task HandleOptionLinkClick(MouseEventArgs args, TValue value)
    {
        if (args.CtrlKey || args.MetaKey)
        {
            var url = HrefSelector(value);
            await JsRuntime.InvokeVoidAsync("window.open", url, "_blank");
            _isOpen = false;
            StateHasChanged();
        }
        else
        {
            OnListValueChanged(value);
        }
    }
}