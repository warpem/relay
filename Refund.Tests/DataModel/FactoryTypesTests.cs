using System.Text.Json.Nodes;
using Refund.DataModel;

namespace Refund.Tests.DataModel;

public class FactoryEdgeTests
{
    [Fact]
    public void FactoryEdge_ParsesFromString()
    {
        var edge = new FactoryEdge("1.Frames", "2.InputFrames");
        Assert.Equal("1.Frames", edge.Source);
        Assert.Equal("2.InputFrames", edge.Target);
    }

    [Fact]
    public void FactoryEdge_SerializesToJson()
    {
        var edge = new FactoryEdge("1.Frames", "2.InputFrames");
        var node = edge.ToJson();
        Assert.Equal("1.Frames", node["Source"]?.GetValue<string>());
        Assert.Equal("2.InputFrames", node["Target"]?.GetValue<string>());
    }

    [Fact]
    public void FactoryEdge_DeserializesFromJson()
    {
        var node = new JsonObject
        {
            ["Source"] = "1.Frames",
            ["Target"] = "2.InputFrames"
        };
        var edge = FactoryEdge.FromJson(node);
        Assert.Equal("1.Frames", edge.Source);
        Assert.Equal("2.InputFrames", edge.Target);
    }

    [Fact]
    public void FactoryExternalEdge_RoundTrips()
    {
        var edge = new FactoryExternalEdge(2, "InputMap", 42, "Map");
        var node = edge.ToJson();
        var restored = FactoryExternalEdge.FromJson(node);

        Assert.Equal(2, restored.SubJobId);
        Assert.Equal("InputMap", restored.SubJobPort);
        Assert.Equal(42, restored.ExternalJobId);
        Assert.Equal("Map", restored.ExternalPort);
    }
}

public class ExposedPortTests
{
    [Fact]
    public void ExposedPort_RoundTrips()
    {
        var port = new ExposedPort
        {
            CustomName = "Raw Frames",
            SubJobId = 1,
            PortName = "Frames",
            ResourceType = "MicrographSet"
        };

        var node = port.ToJson();
        var restored = ExposedPort.FromJson(node);

        Assert.Equal("Raw Frames", restored.CustomName);
        Assert.Equal(1, restored.SubJobId);
        Assert.Equal("Frames", restored.PortName);
        Assert.Equal("MicrographSet", restored.ResourceType);
    }
}

public class ExposedPropertyTests
{
    [Fact]
    public void ExposedProperty_RoundTrips()
    {
        var prop = new ExposedProperty
        {
            CustomName = "Pixel Size",
            SubJobId = 1,
            PropertyName = "PixelSize"
        };

        var node = prop.ToJson();
        var restored = ExposedProperty.FromJson(node);

        Assert.Equal("Pixel Size", restored.CustomName);
        Assert.Equal(1, restored.SubJobId);
        Assert.Equal("PixelSize", restored.PropertyName);
    }
}
