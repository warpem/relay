using Refund.DataModel;
using Refund.Jobs.Fs.MotionCtf.MotionAndCTF2D;
using Refund.Mcp;

namespace Refund.Tests.Mcp;

[Collection("JobRegistry")]
public class RelayMcpProjectionsTests
{
    private static readonly object _lock = new();
    private static void EnsurePopulated()
    {
        lock (_lock)
            if (Job.Types.Count == 0)
                Job.PopulateStatic();
    }

    [Theory]
    [InlineData(10, new[] { 20, 30 }, 10, "owner")]
    [InlineData(10, new[] { 20, 30 }, 20, "member")]
    [InlineData(10, new[] { 20, 30 }, 99, "none")]
    public void ComputeProjectRole_ClassifiesCaller(int ownerId, int[] members, int caller, string expected)
    {
        Assert.Equal(expected, RelayMcpProjections.ComputeProjectRole(ownerId, members, caller));
    }

    [Fact]
    public void BuildJobTypeCatalog_IncludesKnownTypeWithParameters()
    {
        EnsurePopulated();
        var catalog = RelayMcpProjections.BuildJobTypeCatalog();
        Assert.NotEmpty(catalog);
        // MotionAndCTF2D, a known concrete job type.
        var motion = catalog.SingleOrDefault(t => t.TypeGuid == "77cdcb73-1bd0-43e0-b206-3d93acecafa8");
        Assert.NotNull(motion);
        Assert.Equal("Motion & CTF", motion!.TypeName);
        Assert.NotEmpty(motion.Parameters);
    }

    [Fact]
    public void ToDetailDto_PopulatesParametersAndPorts()
    {
        EnsurePopulated();
        var job = new MotionAndCTF2D().AsReadOnly();

        var dto = RelayMcpProjections.ToDetailDto(job);

        Assert.Equal("77cdcb73-1bd0-43e0-b206-3d93acecafa8", dto.TypeGuid);
        Assert.NotEmpty(dto.Parameters);              // reads the job's configured parameter values
        Assert.NotNull(dto.Inputs);                   // ports are enumerated (no edges on a bare job)
        Assert.NotNull(dto.Outputs);
        Assert.True(dto.Inputs.Count + dto.Outputs.Count > 0, "job should declare at least one port");
    }
}
