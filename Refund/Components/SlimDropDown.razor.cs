using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Refund.Components;

/// <summary>
/// A lightweight dropdown component for selecting a value from a list of options.
/// </summary>
public partial class SlimDropDown : ComponentBase, IDisposable
{
    /// <summary>
    /// A unique identifier for this component; Based on counter;
    /// </summary>
    private int _id = 0;

    /// <summary>
    ///  The number of instances of this component
    /// </summary>
    private static int _counter = 0;
    
    /// <summary>
    /// JavaScript runtime for interacting with browser APIs
    /// </summary>
    [Inject]
    protected IJSRuntime IJSRuntime { get; set; }

    /// <summary>
    /// The list of options that can be selected in the dropdown
    /// </summary>
    [Parameter]
    public List<string> Values { get; set; } = new();

    /// <summary>
    /// The currently selected value; can be bound
    /// </summary>
    [Parameter]
    public string? Value { get; set; }

    /// <summary>
    /// Callback that triggers when a new value is selected
    /// </summary>
    [Parameter]
    public EventCallback<string> ValueChanged { get; set; }

    private DotNetObjectReference<SlimDropDown>? _objRef;
    private IJSObjectReference _module = null!;

    protected override void OnInitialized()
    {
        _id = _counter++;
        base.OnInitialized();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if(firstRender)
        {
            _objRef = DotNetObjectReference.Create(this);

            _module = await IJSRuntime.InvokeAsync<IJSObjectReference>("import",
                "./_content/Refund/Components/SkinnyDropDown.razor.js");

            await _module.InvokeVoidAsync("registerComponent", _objRef, _id);
        }
    }

    private async Task UpdateValue(string newValue)
    {
        Value = newValue;
        await HidePopup();
        await ValueChanged.InvokeAsync(Value);
    }

    private async Task HidePopup()
    {
        await _module.InvokeVoidAsync("hidePopup", _id);
    }

    private async Task ShowPopup(MouseEventArgs e)
    {
        await _module.InvokeVoidAsync("showPopup", _id, e);
    }

    public void Dispose()
    {
        _objRef?.Dispose();
        _module?.DisposeAsync().ConfigureAwait(true);
    }
}