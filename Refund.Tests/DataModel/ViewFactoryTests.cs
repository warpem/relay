using Refund.DataModel;

namespace Refund.Tests.DataModel;

public class ViewFactoryTests
{
    [Fact]
    public void View_HasFactoryInstancesCollection()
    {
        var view = new View();
        Assert.NotNull(view.FactoryInstances);
        Assert.Empty(view.FactoryInstances);
    }

    [Fact]
    public void View_AddFactoryInstance_AddsToCollectionAndRootItems()
    {
        var view = new View();
        var inst = new FactoryInstance { Id = 1 };

        view.AddFactoryInstance(inst);

        Assert.Contains(inst, view.FactoryInstances);
        Assert.Contains(inst, view.RootItems);
    }

    [Fact]
    public void View_AddFactoryInstance_ToFolder()
    {
        var view = new View();
        var folder = new Folder { Id = 1, View = view };
        view.AddFolder(folder);

        var inst = new FactoryInstance { Id = 1 };
        view.AddFactoryInstance(inst, folder);

        Assert.Contains(inst, view.FactoryInstances);
        Assert.DoesNotContain(inst, view.RootItems);
        Assert.Contains(inst, folder.Items);
    }

    [Fact]
    public void View_RemoveFactoryInstance_RemovesFromAll()
    {
        var view = new View();
        var inst = new FactoryInstance { Id = 1 };
        view.AddFactoryInstance(inst);

        view.RemoveFactoryInstance(inst);

        Assert.Empty(view.FactoryInstances);
        Assert.Empty(view.RootItems);
    }

    [Fact]
    public void View_FindFactoryInstance_FindsById()
    {
        var view = new View();
        var inst = new FactoryInstance { Id = 5 };
        view.AddFactoryInstance(inst);

        Assert.Same(inst, view.FindFactoryInstance(5));
        Assert.Null(view.FindFactoryInstance(999));
    }
}
