using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Refund.Components;

/// <summary>Adapts string-valued job metadata bounds to the v5 typed numeric input.</summary>
public partial class RobustFluentNumberField<TValue> : BufferedInputBase<TValue>, IAsyncDisposable where TValue : new()
{
    [Inject] private IJSRuntime JS { get; set; }
    private RelayNumberInput<TValue> _input;
    private static readonly Dictionary<int, FluentNumberInputCultureInfo> Cultures = new();
    private const int MaxDecimals = 6;
    private IJSObjectReference? _module;
    private (string? Min, string? Max, string? Step, bool Disabled)? _stepOptions;
    [Parameter] public string? Class { get; set; }
    [Parameter] public string? Style { get; set; }
    [Parameter] public string? Placeholder { get; set; }
    [Parameter] public string? Min { get; set; }
    [Parameter] public string? Max { get; set; }
    [Parameter] public string? Step { get; set; }
    [Parameter] public bool Disabled { get; set; }
    [Parameter] public bool Immediate { get; set; }
    [Parameter] public int ImmediateDelay { get; set; } = 200;
    [Parameter] public string? AutoComplete { get; set; }
    /// <summary>Passed to the input, e.g. aria-label and title.</summary>
    [Parameter(CaptureUnmatchedValues = true)] public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var options = (Min, Max, Step, Disabled);
        if (_stepOptions == options)
            return;

        _stepOptions = options;
        _module ??= await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Refund/Components/RobustFluentNumberField.razor.js");
        if (IsDisposed)
        {
            await _module.DisposeAsync();
            return;
        }
        var culture = CultureInfo.CurrentCulture.NumberFormat;
        await _module.InvokeVoidAsync("configure", _input.Element,
            Convert.ToString(ParseBound(Min, "Min"), CultureInfo.InvariantCulture),
            Convert.ToString(ParseBound(Max, "Max"), CultureInfo.InvariantCulture),
            Convert.ToString(ParseBound(Step, "Step"), CultureInfo.InvariantCulture),
            culture.NumberDecimalSeparator, culture.NumberGroupSeparator);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_module is null)
            return;
        try
        {
            await _module.InvokeVoidAsync("cleanup", _input.Element);
            await _module.DisposeAsync();
        }
        catch (Exception e) when (e is JSDisconnectedException or OperationCanceledException) { }
    }

    /// <summary>
    /// A field shows and accepts its step's precision ("15.0" for step 0.1). A stored value with more
    /// decimals than its step still shows all of them rather than being silently rounded.
    /// </summary>
    private FluentNumberInputCultureInfo DisplayCulture
    {
        get
        {
            int digits = Math.Max(StepDecimals(Step), Decimals(EditingValue));
            lock (Cultures)
            {
                if (!Cultures.TryGetValue(digits, out var culture))
                    Cultures[digits] = culture = new FluentNumberInputCultureInfo(digits);
                return culture;
            }
        }
    }

    internal static int StepDecimals(string? step) =>
        decimal.TryParse(step, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? StepDecimals(value) : 0;

    /// <summary>Decimals a step implies, capped (not rounded) at <see cref="MaxDecimals"/>.</summary>
    internal static int StepDecimals(decimal step)
    {
        step = Math.Abs(step);
        int digits = 0;
        while (digits < MaxDecimals && step != Math.Round(step, digits)) digits++;
        return digits;
    }

    /// <summary>Fewest decimals that represent the value exactly, capped at <see cref="MaxDecimals"/>.</summary>
    internal static int Decimals(object? value)
    {
        decimal number;
        try { number = value is null ? 0 : Convert.ToDecimal(value, CultureInfo.InvariantCulture); }
        catch (Exception e) when (e is OverflowException or InvalidCastException or FormatException) { return 0; }
        number = Math.Round(number, MaxDecimals);
        int digits = 0;
        while (digits < MaxDecimals && number != Math.Round(number, digits)) digits++;
        return digits;
    }

    private static TValue ParseBound(string? value, string kind)
    {
        var type = Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue);
        if (!string.IsNullOrWhiteSpace(value))
            return (TValue)Convert.ChangeType(value, type, CultureInfo.InvariantCulture);

        var number = kind == "Step"
            ? Convert.ChangeType(1, type, CultureInfo.InvariantCulture)
            : type.GetField(kind == "Min" ? "MinValue" : "MaxValue")!.GetValue(null)!;
        return (TValue)number;
    }
}
