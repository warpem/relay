using Refund.Components;
using Xunit;

namespace Refund.Tests.Components;

/// <summary>Number fields show their step's precision, but never hide digits the stored value has.</summary>
public class RobustFluentNumberFieldDigitsTests
{
    [Theory]
    [InlineData(80.0, 0)]
    [InlineData(2.7, 1)]
    [InlineData(0.834, 3)]
    [InlineData(0.1 + 0.2, 1)] // floating-point noise is not precision
    [InlineData(1e-9, 0)]      // below the field's precision
    [InlineData(double.MaxValue, 0)]
    [InlineData(double.NaN, 0)]
    public void ValueDecimals(double value, int expected) =>
        Assert.Equal(expected, RobustFluentNumberField<double>.Decimals(value));

    [Theory]
    [InlineData(null, 0)]
    [InlineData("1", 0)]
    [InlineData("10", 0)]
    [InlineData("0.1", 1)]
    [InlineData("0.05", 2)]
    [InlineData("0.0010", 3)]
    [InlineData("0.0000000001", 6)] // finer than a field can show: capped, not rounded away
    public void StepDecimals(string? step, int expected) =>
        Assert.Equal(expected, RobustFluentNumberField<double>.StepDecimals(step));
}
