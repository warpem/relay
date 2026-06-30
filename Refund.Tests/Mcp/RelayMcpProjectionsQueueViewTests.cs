using Refund.DataModel;
using Refund.JobQueues;
using Refund.Mcp;
using Xunit;

namespace Refund.Tests.Mcp;

public class RelayMcpProjectionsQueueViewTests
{
    [Fact]
    public void ToDto_LocalQueue_TypeIsLocal()
    {
        var local = new LocalQueue((_, _) => { }) { Id = -1, Alias = "Local", QueueType = JobQueueType.Local };
        var dto = RelayMcpProjections.ToDto(local.AsReadOnly());
        Assert.Equal(-1, dto.Id);
        Assert.Equal("local", dto.Type);
    }

    [Fact]
    public void ToDto_GpuQueue_TypeIsCluster()
    {
        var cluster = new ClusterQueue((_, _) => { }) { Id = 3, Alias = "gpu-a100", QueueType = JobQueueType.GPU };
        var dto = RelayMcpProjections.ToDto(cluster.AsReadOnly());
        Assert.Equal(3, dto.Id);
        Assert.Equal("cluster", dto.Type);
    }
}
