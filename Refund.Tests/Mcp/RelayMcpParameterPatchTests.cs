using System.Text.Json;
using Refund.DataModel;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Mcp;
using Warp.Tools;
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

    [Fact]
    public void CoerceJsonValue_HandlesInt3_Array()
    {
        var result = (int3)RelayMcpParameterPatch.CoerceJsonValue(J("[4, 4, 1]"), typeof(int3));
        Assert.Equal(new int3(4, 4, 1), result);
    }

    [Fact]
    public void CoerceJsonValue_HandlesInt3_Scalar()
    {
        // A scalar is broadcast to all components — e.g. 1 → [1, 1, 1].
        var result = (int3)RelayMcpParameterPatch.CoerceJsonValue(J("1"), typeof(int3));
        Assert.Equal(new int3(1, 1, 1), result);
    }

    [Fact]
    public void CoerceJsonValue_HandlesInt2_Array()
    {
        var result = (int2)RelayMcpParameterPatch.CoerceJsonValue(J("[8, 8]"), typeof(int2));
        Assert.Equal(new int2(8, 8), result);
    }

    [Fact]
    public void CoerceJsonValue_HandlesFloat3_Array()
    {
        var result = (float3)RelayMcpParameterPatch.CoerceJsonValue(J("[1.0, 2.5, 0.5]"), typeof(float3));
        Assert.Equal(1.0f, result.X);
        Assert.Equal(2.5f, result.Y);
        Assert.Equal(0.5f, result.Z);
    }

    [Fact]
    public void CoerceJsonValue_Int3_WrongLength_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            RelayMcpParameterPatch.CoerceJsonValue(J("[1, 2]"), typeof(int3)));
        Assert.Contains("3", ex.Message);
    }

    [Fact]
    public void Resolve_Int3Parameter_RoundTrips()
    {
        EnsurePopulated();
        var job = new MotionAndCTF2D();
        var patch = new Dictionary<string, JsonElement> { ["MotionGridDims"] = J("[4, 4, 1]") };
        foreach (var (p, v) in RelayMcpParameterPatch.Resolve(typeof(MotionAndCTF2D), patch))
            p.SetValue(job, v);
        Assert.Equal(new int3(4, 4, 1), job.MotionGridDims);
    }
}
