using Refund.Services.Core.DataManager;

namespace Refund.Tests.Services;

public class GroupNameFactoryTests
{
    [Fact]
    public void FactoryDefinition_WithIds_FormatsCorrectly()
    {
        Assert.Equal("P1_S2_FD3", GroupName.FactoryDefinition(1, 2, 3));
    }

    [Fact]
    public void FactoryDefinition_WithWildcards_FormatsCorrectly()
    {
        Assert.Equal("P1_S2_FD*", GroupName.FactoryDefinition(1, 2, null));
        Assert.Equal("P1_S*_FD*", GroupName.FactoryDefinition(1, null, null));
        Assert.Equal("P*_S*_FD*", GroupName.FactoryDefinition(null, null, null));
    }

    [Fact]
    public void FactoryDefinition_ValidatesHierarchy()
    {
        Assert.Throws<ArgumentException>(() => GroupName.FactoryDefinition(null, 2, 3));
        Assert.Throws<ArgumentException>(() => GroupName.FactoryDefinition(1, null, 3));
    }

    [Fact]
    public void FactoryDefinitionHierarchy_ReturnsAllLevels()
    {
        var hierarchy = GroupName.FactoryDefinitionHierarchy(1, 2, 3);
        Assert.Equal(4, hierarchy.Length);
        Assert.Equal("P1_S2_FD3", hierarchy[0]);
        Assert.Equal("P1_S2_FD*", hierarchy[1]);
        Assert.Equal("P1_S*_FD*", hierarchy[2]);
        Assert.Equal("P*_S*_FD*", hierarchy[3]);
    }

    [Fact]
    public void FactoryDefinitionHierarchy_WithoutId_OmitsSpecific()
    {
        var hierarchy = GroupName.FactoryDefinitionHierarchy(1, 2, null);
        Assert.Equal(3, hierarchy.Length);
        Assert.Equal("P1_S2_FD*", hierarchy[0]);
    }

    [Fact]
    public void FactoryInstance_WithIds_FormatsCorrectly()
    {
        Assert.Equal("P1_S2_FI3", GroupName.FactoryInstance(1, 2, 3));
    }

    [Fact]
    public void FactoryInstance_WithWildcards_FormatsCorrectly()
    {
        Assert.Equal("P1_S2_FI*", GroupName.FactoryInstance(1, 2, null));
    }

    [Fact]
    public void FactoryInstanceHierarchy_ReturnsAllLevels()
    {
        var hierarchy = GroupName.FactoryInstanceHierarchy(1, 2, 5);
        Assert.Equal(4, hierarchy.Length);
        Assert.Equal("P1_S2_FI5", hierarchy[0]);
        Assert.Equal("P1_S2_FI*", hierarchy[1]);
        Assert.Equal("P1_S*_FI*", hierarchy[2]);
        Assert.Equal("P*_S*_FI*", hierarchy[3]);
    }
}
