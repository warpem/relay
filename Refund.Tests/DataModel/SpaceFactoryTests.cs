using Refund.DataModel;

namespace Refund.Tests.DataModel;

public class SpaceFactoryTests
{
    [Fact]
    public void Space_HasFactoryDefinitionsCollection()
    {
        var space = new Space();
        Assert.NotNull(space.FactoryDefinitions);
        Assert.Empty(space.FactoryDefinitions);
    }

    [Fact]
    public void Space_HasFactoryInstancesCollection()
    {
        var space = new Space();
        Assert.NotNull(space.FactoryInstances);
        Assert.Empty(space.FactoryInstances);
    }

    [Fact]
    public void Space_CreateFactoryDefinition_AssignsId()
    {
        var space = new Space();
        var def = space.CreateFactoryDefinition();

        Assert.Equal(1, def.Id);
        Assert.Contains(def, space.FactoryDefinitions);
    }

    [Fact]
    public void Space_CreateFactoryDefinition_IncrementsIds()
    {
        var space = new Space();
        var def1 = space.CreateFactoryDefinition();
        var def2 = space.CreateFactoryDefinition();

        Assert.Equal(1, def1.Id);
        Assert.Equal(2, def2.Id);
    }

    [Fact]
    public void Space_DeleteFactoryDefinition_RemovesIt()
    {
        var space = new Space();
        var def = space.CreateFactoryDefinition();
        space.DeleteFactoryDefinition(def);

        Assert.Empty(space.FactoryDefinitions);
    }

    [Fact]
    public void Space_FindFactoryDefinition_FindsById()
    {
        var space = new Space();
        var def = space.CreateFactoryDefinition();
        var found = space.FindFactoryDefinition(def.Id);

        Assert.Same(def, found);
    }

    [Fact]
    public void Space_FindFactoryDefinition_ReturnsNullForMissing()
    {
        var space = new Space();
        Assert.Null(space.FindFactoryDefinition(999));
    }

    [Fact]
    public void Space_CreateFactoryInstance_AssignsId()
    {
        var space = new Space();
        var inst = space.CreateFactoryInstance(definitionId: 1);

        Assert.Equal(1, inst.Id);
        Assert.Equal(1, inst.DefinitionId);
        Assert.Same(space, inst.Space);
        Assert.Contains(inst, space.FactoryInstances);
    }

    [Fact]
    public void Space_DeleteFactoryInstance_RemovesIt()
    {
        var space = new Space();
        var inst = space.CreateFactoryInstance(definitionId: 1);
        space.DeleteFactoryInstance(inst);

        Assert.Empty(space.FactoryInstances);
    }

    [Fact]
    public void Space_FindFactoryInstance_FindsById()
    {
        var space = new Space();
        var inst = space.CreateFactoryInstance(definitionId: 1);
        var found = space.FindFactoryInstance(inst.Id);

        Assert.Same(inst, found);
    }
}
