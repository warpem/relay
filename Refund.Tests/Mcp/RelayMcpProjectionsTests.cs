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
    public void BuildJobTypeSummaries_IncludesKnownTypeWithCategoryPath()
    {
        EnsurePopulated();
        var summaries = RelayMcpProjections.BuildJobTypeSummaries();
        Assert.NotEmpty(summaries);
        // MotionAndCTF2D, a known concrete job type.
        var motion = summaries.SingleOrDefault(t => t.TypeGuid == "77cdcb73-1bd0-43e0-b206-3d93acecafa8");
        Assert.NotNull(motion);
        Assert.Equal("Motion & CTF", motion!.TypeName);
        Assert.Contains(".", motion.Category); // full context-menu path, e.g. "Frame-series.…"
    }

    [Fact]
    public void BuildJobTypeDetail_HasParametersAndPorts_ForKnownType()
    {
        EnsurePopulated();
        var detail = RelayMcpProjections.BuildJobTypeDetail("77cdcb73-1bd0-43e0-b206-3d93acecafa8");
        Assert.NotNull(detail);
        Assert.Equal("Motion & CTF", detail!.TypeName);
        Assert.NotEmpty(detail.Parameters);
        Assert.True(detail.Inputs.Count + detail.Outputs.Count > 0, "type should declare at least one port");
    }

    [Fact]
    public void BuildJobTypeDetail_ReturnsNull_ForUnknownGuid()
    {
        EnsurePopulated();
        Assert.Null(RelayMcpProjections.BuildJobTypeDetail("not-a-real-guid"));
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
