using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Refund.Components;

/// <summary>
/// A listbox component with filtering capabilities that allows users to search within a list of options.
/// Supports templating for custom option rendering and configurable search behavior.
/// 
/// This component is designed to be flexible and reusable across the application, and is used 
/// by components like ListboxButton to provide searchable dropdown functionality. It implements 
/// a generic approach that works with any value type and supports customizable filter behavior.
/// </summary>
/// <typeparam name="TValue">The type of value stored in the listbox options</typeparam>
public partial class FilteredListbox<TValue>
{
    /// <summary>
    /// Current filter text entered by user in the search box.
    /// </summary>
    private string _filterText = string.Empty;

    /// <summary>
    /// Gets or sets the currently selected option value.
    /// </summary>
    [Parameter] public TValue SelectedOption { get; set; }
    
    /// <summary>
    /// Event callback that is triggered when the selected option changes.
    /// </summary>
    [Parameter] public EventCallback<TValue> SelectedOptionChanged { get; set; }

    /// <summary>
    /// Collection of options to display in the listbox.
    /// Each option is a tuple containing the value and display text.
    /// </summary>
    [Parameter] public IEnumerable<(TValue Value, string Text)> Options { get; set; } = [];

    /// <summary>
    /// Gets or sets whether the listbox is disabled.
    /// When true, user interaction is prevented.
    /// </summary>
    [Parameter] public bool Disabled { get; set; } = false;
    
    /// <summary>
    /// Placeholder text displayed in the filter input when empty.
    /// </summary>
    [Parameter] public string FilterPlaceholder { get; set; } = "Search";
    
    /// <summary>
    /// When true, filters options that start with the filter text.
    /// When false (default), filters options that contain the filter text anywhere.
    /// </summary>
    [Parameter] public bool MatchStart { get; set; }

    /// <summary>
    /// Width of the listbox in pixels.
    /// </summary>
    [Parameter] public int ListWidth { get; set; } = 100;
    
    /// <summary>
    /// Height of the listbox in pixels.
    /// </summary>
    [Parameter] public int ListHeight { get; set; } = 300;
    
    /// <summary>
    /// Custom template for rendering each option item.
    /// If not provided, default rendering will be used.
    /// </summary>
    [Parameter] public RenderFragment<(TValue Value, string Text)>? OptionTemplate { get; set; }

    /// <summary>
    /// Optional function that maps a value to a navigation URL.
    /// When set, each option is wrapped in an &lt;a&gt; tag for native link behavior.
    /// </summary>
    [Parameter] public Func<TValue, string> HrefSelector { get; set; }

    /// <summary>
    /// Callback fired when an option's link is clicked (only when HrefSelector is set).
    /// Receives both the MouseEventArgs (for modifier detection) and the option value.
    /// </summary>
    [Parameter] public EventCallback<(MouseEventArgs Args, TValue Value)> OnOptionClicked { get; set; }
    
    /// <summary>
    /// Unique identifier for the component instance, used for DOM element identification.
    /// </summary>
    private string _id = Guid.NewGuid().ToString()[..16];

    /// <summary>
    /// Determines whether an option should be displayed based on the current filter text.
    /// </summary>
    /// <param name="text">The display text of the option</param>
    /// <returns>True if the option should be shown, false if it should be filtered out</returns>
    private bool ShouldShowOption(string text)
    {
        if (string.IsNullOrWhiteSpace(_filterText))
            return true;

        return MatchStart 
                   ? text.StartsWith(_filterText, StringComparison.InvariantCultureIgnoreCase)
                   : text.Contains(_filterText, StringComparison.InvariantCultureIgnoreCase);
    }

    /// <summary>
    /// Handles selection of an option from the listbox.
    /// Updates the selected value and triggers the SelectedOptionChanged event.
    /// </summary>
    /// <param name="value">The value of the selected option</param>
    private async Task OnValueChanged(TValue value)
    {
        SelectedOption = value;
        if (SelectedOptionChanged.HasDelegate)
            await SelectedOptionChanged.InvokeAsync(value);
    }
}