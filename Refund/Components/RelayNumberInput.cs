using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Refund.Components;

/// <summary>
/// A <see cref="FluentNumberInput{TValue}"/> that keeps the culture's decimals on screen. Fluent formats
/// values to the culture's digit count, but its input mask strips trailing zeros when it loads, so a
/// step-0.1 field read "15" until an edit made it "15.0". Padding the mask makes both the same.
/// </summary>
public class RelayNumberInput<TValue> : FluentNumberInput<TValue>
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    public RelayNumberInput(LibraryConfiguration configuration) : base(configuration) { }

    // Fluent (re)creates its mask while rendering; padding afterwards is a no-op once applied.
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (!IsDecimal)
            return;
        try { await Js.InvokeVoidAsync("relayPadNumberMask", Id); }
        catch (Exception e) when (e is JSDisconnectedException or JSException or OperationCanceledException) { }
    }
}
