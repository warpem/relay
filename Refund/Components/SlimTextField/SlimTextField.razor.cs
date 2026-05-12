using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Refund.Components.SlimTextField;

public partial class SlimTextField
{
    /// <summary>
    ///  The number of instances of this component
    /// </summary>
    static int TotalCount = 0;

    /// <summary>
    /// A unique identifier for this component; Based on TotalCount;
    /// </summary>
    private int identifier;

    /// <summary>
    /// JavaScript runtime for interacting with browser APIs
    /// </summary>
    [Inject]
    protected IJSRuntime JS { get; set; }

    private decimal _value = 0;

    /// <summary>
    /// The value displayed in the component; Can be bound;
    /// </summary>
    [Parameter]
    public decimal Value
    {
        get => _value;
        set => _value = RoundValue(value);
    }

    /// <summary>
    /// Callback that can optionally be set and triggers when the value is updated
    /// </summary>
    [Parameter]
    public EventCallback<decimal> OnValueChange { get; set; }

    /// <summary>
    /// Callback that is called automatically when the value is bound 
    /// </summary>
    [Parameter]
    public EventCallback<decimal> ValueChanged { get; set; }

    /// <summary>
    /// Optional field to specify a lower bound for the value (inclusive); Can be bound;
    /// </summary>
    [Parameter]
    public decimal? Min { get; set; } = null;

    /// <summary>
    /// Callback used when the Min parameter is bound
    /// </summary>
    [Parameter]
    public EventCallback<decimal> MinChanged { get; set; }

    /// <summary>
    /// Optional field to specify the upper bound for the value (inclusive); Can be bound;
    /// </summary>
    [Parameter]
    public decimal? Max { get; set; } = null;

    /// <summary>
    /// Callback used when the Max parameter is bound
    /// </summary>
    [Parameter]
    public EventCallback<decimal> MaxChanged { get; set; }

    private decimal? _step = 1;

    /// <summary>
    /// The increase or decrease applied to the value when the up or down buttons are pressed;
    /// </summary>
    [Parameter]
    public decimal? Step
    {
        get => _step;
        set
        {
            _step = value;
            Value = RoundValue(_value);
        }
    }

    /// <summary>
    /// Optional parameter - The amount of time to wait before submitting the udpated value to the backend; 
    ///  Used to decrease the load on systems that are still being updated (for instance, when typing)
    /// </summary>
    [Parameter]
    public decimal? DebounceInterval { get; set; } = 200;

    private DotNetObjectReference<SlimTextField>? objRef;
    private IJSObjectReference module;

    protected override void OnInitialized()
    {
        identifier = Interlocked.Increment(ref TotalCount);
        base.OnInitialized();
    }

    protected override void OnParametersSet()
    {
        base.OnParametersSet();
    }


    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if(firstRender)
        {
            objRef = DotNetObjectReference.Create(this);
            module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/SlimTextField/SlimTextField.razor.js");
            await module.InvokeVoidAsync("registerComponent", objRef, identifier, Step, Min, Max, DebounceInterval);
        }
    }

    protected override bool ShouldRender() => true;

    /// <summary>
    /// Rounds the given value to the same amount of decimal points as "Step"
    /// </summary>
    /// <param name="value">The value to round</param>
    /// <returns>A rounded value</returns>
    protected decimal RoundValue(decimal value)
    {
        decimal result;

        if(Step != null)
        {
            int count = BitConverter.GetBytes(decimal.GetBits(Step.Value)[3])[2];
            result = Math.Round(value, count);
        }
        else
        {
            result = value;
        }

        return result;
    }

    /// <summary>
    /// Takes a string value, converts it to a decial and stores it in Value for display
    /// </summary>
    /// <param name="filterValue">The value to set</param>
    [JSInvokable]
    public async Task UpdateFilterValue(string filterValue)
    {
        decimal result;
        var update = false;

        if(decimal.TryParse(filterValue, out result))
        {
            if(result != Value)
            {
                Value = result;
                update = true;
            }
        }
        else
        {
            if(!Regex.IsMatch(filterValue.ToString(), "^[-.0-9]*$"))
            {
                StateHasChanged();
            }
        }

        if(update)
        {
            await InvokeAsync(async () =>
            {
                await ValueChanged.InvokeAsync(Value);
                await OnValueChange.InvokeAsync(Value);
            });
        }
    }
}