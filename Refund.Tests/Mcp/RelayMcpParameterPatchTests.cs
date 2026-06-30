using System.Text.Json;
using Refund.DataModel;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Mcp;
using Xunit;

namespace Refund.Tests.Mcp;

[Collection("JobRegistry")]
public class RelayMcpParameterPatchTests
{
    private static readonly object _lock = new();
    private static void EnsurePopulated()
    {
        lock (_lock)
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Resolve_CoercesIntParameter()
    {
        EnsurePopulated();
        var patch = new Dictionary<string, JsonElement> { ["NClasses"] = J("5") };
        var assignments = RelayMcpParameterPatch.Resolve(typeof(Class3D), patch);
        var (prop, value) = Assert.Single(assignments);
        Assert.Equal("NClasses", prop.Name);
        Assert.Equal(5, Assert.IsType<int>(value));
    }

    [Fact]
    public void Resolve_AppliesViaSetValue()
    {
        EnsurePopulated();
        var job = new Class3D();
        var patch = new Dictionary<string, JsonElement> { ["NClasses"] = J("8") };
        foreach (var (p, v) in RelayMcpParameterPatch.Resolve(typeof(Class3D), patch))
            p.SetValue(job, v);
        Assert.Equal(8, job.NClasses);
    }

    [Fact]
    public void Resolve_UnknownName_Throws()
    {
        EnsurePopulated();
        var patch = new Dictionary<string, JsonElement> { ["NotAParam"] = J("1") };
        var ex = Assert.Throws<ArgumentException>(() => RelayMcpParameterPatch.Resolve(typeof(Class3D), patch));
        Assert.Contains("NotAParam", ex.Message);
    }

    [Fact]
    public void Resolve_IsAllOrNothing_OnBadEntry()
    {
        EnsurePopulated();
        // One good, one unknown -> throws, and (by contract) returns nothing applied.
        var patch = new Dictionary<string, JsonElement>
        {
            ["NClasses"] = J("3"),
            ["Bogus"] = J("9")
        };
        Assert.Throws<ArgumentException>(() => RelayMcpParameterPatch.Resolve(typeof(Class3D), patch));
    }

    [Fact]
    public void CoerceJsonValue_HandlesCommonTypes()
    {
        Assert.Equal(4, RelayMcpParameterPatch.CoerceJsonValue(J("4"), typeof(int)));
        Assert.Equal(2.5m, RelayMcpParameterPatch.CoerceJsonValue(J("2.5"), typeof(decimal)));
        Assert.Equal(true, RelayMcpParameterPatch.CoerceJsonValue(J("true"), typeof(bool)));
        Assert.Equal("hi", RelayMcpParameterPatch.CoerceJsonValue(J("\"hi\""), typeof(string)));
        Assert.Equal(7, RelayMcpParameterPatch.CoerceJsonValue(J("7"), typeof(int?)));
        Assert.Null(RelayMcpParameterPatch.CoerceJsonValue(J("null"), typeof(int?)));
        Assert.Equal(AccessLevel.EditRun,
            RelayMcpParameterPatch.CoerceJsonValue(J("\"EditRun\""), typeof(AccessLevel)));
    }
}
