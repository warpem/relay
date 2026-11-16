using System.Reflection;
using Refund.Components;
using Refund.DataModel;
using Refund.UIFields;
using Xunit;

namespace Refund.Tests.UIFields;

/// <summary>
/// A decimal field's step sets the precision it shows and accepts ("15.0" for step 0.1), so every
/// default must be expressible at that precision; a 3.5 default needs a step of 0.1 or finer.
/// </summary>
[Collection("JobRegistry")]
public class DecimalFieldStepTests
{
    public static IEnumerable<object[]> DecimalFields()
    {
        var types = typeof(Job).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } && t.GetConstructor(Type.EmptyTypes) != null);
        foreach (var type in types)
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            if (property.GetCustomAttribute<UiDecimal>(true) is { } field)
                yield return [type.Name, property.Name, field.StepSize];
    }

    [Theory]
    [MemberData(nameof(DecimalFields))]
    public void DefaultFitsStepPrecision(string typeName, string propertyName, decimal step)
    {
        var type = typeof(Job).Assembly.GetTypes().Single(t => t.Name == typeName && t.GetConstructor(Type.EmptyTypes) != null);
        object? value = type.GetProperty(propertyName)!.GetValue(Activator.CreateInstance(type));
        if (value is null)
            return;
        int stepDigits = RobustFluentNumberField<decimal>.StepDecimals(step);
        int valueDigits = RobustFluentNumberField<decimal>.Decimals(value);
        Assert.True(valueDigits <= stepDigits,
            $"{typeName}.{propertyName} defaults to {value} but its step {step} allows only {stepDigits} decimals");
    }
}
