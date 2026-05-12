using System.Text.Json.Nodes;
using Refund.DataModel;

namespace Refund.Tests.DataModel;

public class FactoryDefinitionTests
{
    [Fact]
    public void FactoryDefinition_DefaultState()
    {
        var def = new FactoryDefinition();
        Assert.Equal(-1, def.Id);
        Assert.Equal("", def.Alias);
        Assert.Empty(def.SubJobs);
        Assert.Empty(def.InternalEdges);
        Assert.Empty(def.ExternalEdges);
        Assert.Empty(def.ExposedPortsIn);
        Assert.Empty(def.ExposedPortsOut);
        Assert.Empty(def.ExposedProperties);
        Assert.Empty(def.QueueAssignments);
        Assert.Null(def.DiagramLayout);
    }

    [Fact]
    public void FactoryDefinition_SerializesSimpleProperties()
    {
        var def = new FactoryDefinition
        {
            Id = 1,
            Alias = "Preprocessing Pipeline"
        };

        var json = def.ToJson();

        Assert.Equal(1, json["Id"]?.GetValue<int>());
        Assert.Equal("Preprocessing Pipeline", json["Alias"]?.GetValue<string>());
    }

    [Fact]
    public void FactoryDefinition_SerializesInternalEdges()
    {
        var def = new FactoryDefinition { Id = 1 };
        def.InternalEdges.Add(new FactoryEdge("1.Frames", "2.InputFrames"));

        var json = def.ToJson();
        var edges = json["InternalEdges"]?.AsArray();

        Assert.NotNull(edges);
        Assert.Single(edges);
        Assert.Equal("1.Frames", edges[0]["Source"]?.GetValue<string>());
    }

    [Fact]
    public void FactoryDefinition_SerializesExternalEdges()
    {
        var def = new FactoryDefinition { Id = 1 };
        def.ExternalEdges.Add(new FactoryExternalEdge(2, "InputMap", 42, "Map"));

        var json = def.ToJson();
        var edges = json["ExternalEdges"]?.AsArray();

        Assert.NotNull(edges);
        Assert.Single(edges);
        Assert.Equal(42, edges[0]["ExternalJobId"]?.GetValue<int>());
    }

    [Fact]
    public void FactoryDefinition_SerializesExposedPorts()
    {
        var def = new FactoryDefinition { Id = 1 };
        def.ExposedPortsIn.Add(new ExposedPort
        {
            CustomName = "Raw Frames",
            SubJobId = 1,
            PortName = "Frames",
            ResourceType = "MicrographSet"
        });
        def.ExposedPortsOut.Add(new ExposedPort
        {
            CustomName = "Output",
            SubJobId = 2,
            PortName = "Micrographs",
            ResourceType = "MicrographSet"
        });

        var json = def.ToJson();

        Assert.Single(json["ExposedPortsIn"]?.AsArray());
        Assert.Single(json["ExposedPortsOut"]?.AsArray());
        Assert.Equal("Raw Frames", json["ExposedPortsIn"][0]["CustomName"]?.GetValue<string>());
    }

    [Fact]
    public void FactoryDefinition_SerializesExposedProperties()
    {
        var def = new FactoryDefinition { Id = 1 };
        def.ExposedProperties.Add(new ExposedProperty
        {
            CustomName = "Pixel Size",
            SubJobId = 1,
            PropertyName = "PixelSize"
        });

        var json = def.ToJson();
        Assert.Single(json["ExposedProperties"]?.AsArray());
        Assert.Equal("PixelSize", json["ExposedProperties"][0]["PropertyName"]?.GetValue<string>());
    }

    [Fact]
    public void FactoryDefinition_SerializesQueueAssignments()
    {
        var def = new FactoryDefinition { Id = 1 };
        def.QueueAssignments[2] = 3;
        def.QueueAssignments[4] = null;

        var json = def.ToJson();
        var qa = json["QueueAssignments"]?.AsObject();

        Assert.NotNull(qa);
        Assert.Equal(3, qa["2"]?.GetValue<int>());
        Assert.True(qa.ContainsKey("4"));
    }

    [Fact]
    public void FactoryDefinition_SerializesNullQueueAssignment()
    {
        var def = new FactoryDefinition { Id = 1 };
        def.QueueAssignments[2] = 3;
        def.QueueAssignments[4] = null; // unassigned

        var json = def.ToJson();
        var restored = new FactoryDefinition();
        restored.ReadFromJson(json);

        Assert.Equal(3, restored.QueueAssignments[2]);
        Assert.Null(restored.QueueAssignments[4]); // null survives round-trip
    }

    [Fact]
    public void FactoryDefinition_RoundTripsWithoutSubJobs()
    {
        // Sub-job round-trip requires Job.Types to be populated (needs app startup),
        // so we test the non-SubJob parts independently.
        var def = new FactoryDefinition
        {
            Id = 5,
            Alias = "My Pipeline"
        };
        def.InternalEdges.Add(new FactoryEdge("1.Out", "2.In"));
        def.ExternalEdges.Add(new FactoryExternalEdge(1, "Map", 10, "OutputMap"));
        def.ExposedPortsIn.Add(new ExposedPort { CustomName = "Input", SubJobId = 1, PortName = "Data", ResourceType = "DataSetFs" });
        def.ExposedPortsOut.Add(new ExposedPort { CustomName = "Result", SubJobId = 2, PortName = "Volume", ResourceType = "Map" });
        def.ExposedProperties.Add(new ExposedProperty { CustomName = "Resolution", SubJobId = 2, PropertyName = "Resolution" });
        def.QueueAssignments[1] = 5;

        var json = def.ToJson();
        var restored = new FactoryDefinition();
        restored.ReadFromJson(json);

        Assert.Equal(5, restored.Id);
        Assert.Equal("My Pipeline", restored.Alias);
        Assert.Single(restored.InternalEdges);
        Assert.Equal("1.Out", restored.InternalEdges[0].Source);
        Assert.Single(restored.ExternalEdges);
        Assert.Equal(10, restored.ExternalEdges[0].ExternalJobId);
        Assert.Single(restored.ExposedPortsIn);
        Assert.Single(restored.ExposedPortsOut);
        Assert.Single(restored.ExposedProperties);
        Assert.Equal(5, restored.QueueAssignments[1]);
    }

    [Fact]
    public void FactoryDefinition_AsReadOnly_ReturnsCachedWrapper()
    {
        var def = new FactoryDefinition { Id = 1, Alias = "Test" };
        var ro1 = def.AsReadOnly();
        var ro2 = def.AsReadOnly();

        Assert.Same(ro1, ro2);
        Assert.Equal(1, ro1.Id);
        Assert.Equal("Test", ro1.Alias);
    }
}
