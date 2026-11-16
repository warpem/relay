using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Refund.Components;
using Refund.DataModel;
using Refund.UIFields;
using Xunit;

namespace Refund.Tests.UIFields;

/// <summary>
/// A decimal field accepts only as many decimals as its step has, so a step of 1 on a fraction makes 0.5
/// impossible to type. Each rule below follows from what a kind of parameter is and the values people
/// enter for it; it sets the coarsest step allowed, not the step itself. The rules apply to every
/// [UiDecimal] job parameter and to Warp Setup's inline @D(...) fields.
/// </summary>
public class DecimalFieldPrecisionTests
{
    private const string Fraction = "a fraction bounded by 1";
    private const string FinePixelSize = "a pixel size that admits detector pixel sizes below 1 Å";
    private const string Resolution = "a resolution limit or low-pass filter";
    private const string Defocus = "a defocus in µm";

    /// <summary>
    /// Decimals a field must accept, by rule. A fraction lies within [-1, 1]; a nonpositive B-factor has
    /// max 0 but is no fraction, and min ≥ max marks a broken range.
    /// </summary>
    internal static IEnumerable<(string Rule, int Decimals)> Requirements(string label, string unit, decimal min, decimal max)
    {
        if (-1 <= min && min < max && max <= 1)
            yield return (Fraction, 2);
        // Calibrated pixel sizes carry 3–4 decimals; fields bounded at 1 Å or more are coarse rescaling targets.
        if (label.Contains("pixel size", StringComparison.OrdinalIgnoreCase) && min < 1)
            yield return (FinePixelSize, 3);
        if (Regex.IsMatch(label, "resolution|low-pass", RegexOptions.IgnoreCase))
            yield return (Resolution, 1);
        if (unit == "µm")
            yield return (Defocus, 2);
    }

    public static IEnumerable<object[]> JobFields()
    {
        foreach (var type in typeof(Job).Assembly.GetTypes())
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            if (property.GetCustomAttribute<UiDecimal>(false) is { } field)
                yield return [$"{type.Name}.{property.Name}", field.Label, field.Unit ?? "", field.Min, field.Max, field.StepSize];
    }

    public static IEnumerable<object[]> WarpSetupFields()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Refund", "Jobs", "Fs", "Warp", "WarpSetup.razor")))
            directory = directory.Parent;
        var markup = File.ReadAllText(Path.Combine(directory!.FullName, "Refund", "Jobs", "Fs", "Warp", "WarpSetup.razor"));
        foreach (Match m in Regex.Matches(markup, @"@D\(""(\w+)"", ""([^""]*)"", ([-0-9.]+), ([-0-9.]+), ([0-9.]+)(?:, ""([^""]*)"")?"))
            yield return [$"WarpSettings.{m.Groups[1].Value}", m.Groups[2].Value, m.Groups[6].Value,
                          Parse(m.Groups[3].Value), Parse(m.Groups[4].Value), Parse(m.Groups[5].Value)];
    }

    [Theory]
    [MemberData(nameof(JobFields))]
    public void JobParameterStepAcceptsTypicalValues(string field, string label, string unit, decimal min, decimal max, decimal step) =>
        AssertPrecision(field, label, unit, min, max, step);

    [Theory]
    [MemberData(nameof(WarpSetupFields))]
    public void WarpSetupStepAcceptsTypicalValues(string field, string label, string unit, decimal min, decimal max, decimal step) =>
        AssertPrecision(field, label, unit, min, max, step);

    /// <summary>Guards against a rule matching nothing after a label or unit is renamed.</summary>
    [Fact]
    public void EveryRuleAppliesToSomeJobParameter()
    {
        var applied = JobFields()
            .SelectMany(f => Requirements((string)f[1], (string)f[2], (decimal)f[3], (decimal)f[4]))
            .Select(r => r.Rule)
            .ToHashSet();
        Assert.Equal(new[] { Fraction, FinePixelSize, Resolution, Defocus }.Order(), applied.Order());
    }

    private static void AssertPrecision(string field, string label, string unit, decimal min, decimal max, decimal step)
    {
        int digits = RobustFluentNumberField<decimal>.StepDecimals(step);
        foreach (var (rule, decimals) in Requirements(label, unit, min, max))
            Assert.True(digits >= decimals,
                $"{field} (\"{label}\") is {rule} and needs a step of {1m / (decimal)Math.Pow(10, decimals)} or finer, " +
                $"but its step {step.ToString(CultureInfo.InvariantCulture)} accepts only {digits} decimals");
    }

    private static decimal Parse(string value) => decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
}
