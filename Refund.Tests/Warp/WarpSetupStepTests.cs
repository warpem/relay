using System.Globalization;
using System.Text.RegularExpressions;
using Refund.Components;
using Refund.Services.Warp;
using Xunit;

namespace Refund.Tests.Warp;

/// <summary>
/// Warp's Setup declares decimal fields inline as @D(property, label, min, max, step, …). As with job
/// parameters, the step sets the shown precision, so every default must fit it.
/// </summary>
public class WarpSetupStepTests
{
    public static IEnumerable<object[]> SetupFields()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Refund", "Jobs", "Fs", "Warp", "WarpSetup.razor")))
            directory = directory.Parent;
        var markup = File.ReadAllText(Path.Combine(directory!.FullName, "Refund", "Jobs", "Fs", "Warp", "WarpSetup.razor"));
        foreach (Match m in Regex.Matches(markup, @"@D\(""(\w+)"", ""[^""]*"", [^,]+, [^,]+, ([0-9.]+)"))
            yield return [m.Groups[1].Value, m.Groups[2].Value];
    }

    [Theory]
    [MemberData(nameof(SetupFields))]
    public void DefaultFitsStepPrecision(string property, string step)
    {
        object? value = typeof(WarpSettings).GetProperty(property)!.GetValue(new WarpSettings());
        int stepDigits = RobustFluentNumberField<decimal>.StepDecimals(step);
        Assert.True(RobustFluentNumberField<decimal>.Decimals(value) <= stepDigits,
            $"WarpSettings.{property} defaults to {Convert.ToString(value, CultureInfo.InvariantCulture)} but its step {step} allows only {stepDigits} decimals");
    }
}
