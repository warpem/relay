using Refund.DataModel;
using Refund.Services.Core.Session;
using static Refund.Tests.Services.JobActivityTests;

namespace Refund.Tests.Services;

[Collection("JobRegistry")]
public class JobNavigationTests
{
    private readonly User _user = new() { Id = 1, Username = "me" };

    public JobNavigationTests() => JobRegistry.EnsurePopulated();

    [Fact]
    public void OpensNestedJobInItsContainingFolder()
    {
        var space = AddSpace(CreateProject(3, _user), 4);
        var job = AddJob(space, 7);
        var view = space.CreateView(null);
        view.AddJob(job);
        var outer = new Folder { Id = 1 };
        var inner = new Folder { Id = 2 };
        view.AddFolder(outer);
        view.AddFolder(inner, outer);
        view.MoveJobToFolder(job, inner);

        var request = JobNavigation.ForJob(job.AsReadOnly());

        Assert.Equal((3, 4, view.Id, 2, 7),
            (request.ProjectId, request.SpaceId, request.ViewId, request.FolderId, request.JobId));
        Assert.Null(request.FactoryInstanceId);
    }

    [Fact]
    public void FactorySubJobKeepsItsFactoryAndContainingFolderContext()
    {
        var space = AddSpace(CreateProject(1, _user), 2);
        var job = AddJob(space, 7);
        var view = space.CreateView(null);
        view.AddJob(job);
        var folder = new Folder { Id = 3 };
        view.AddFolder(folder);
        var instance = new FactoryInstance { Id = 7, Space = space, SubJobIds = [job.Id] };
        job.FactoryInstanceId = instance.Id;
        view.AddFactoryInstance(instance, folder);
        view.RemoveJobFromRootItems(job);

        var request = JobNavigation.ForJob(job.AsReadOnly());

        Assert.Equal(instance.Id, request.FactoryInstanceId);
        Assert.Equal(folder.Id, request.FolderId);
        Assert.Equal(job.Id, request.JobId);
    }

    [Fact]
    public void KeepsThePreferredViewWhenItContainsTheJob()
    {
        var space = AddSpace(CreateProject(1, _user), 2);
        var job = AddJob(space, 7);
        var first = space.CreateView(null);
        var preferred = space.CreateView(null);
        first.AddJob(job);
        preferred.AddJob(job);

        Assert.Equal(preferred.Id, JobNavigation.ForJob(job.AsReadOnly(), preferred.AsReadOnly()).ViewId);
        preferred.RemoveJob(job);
        Assert.Equal(first.Id, JobNavigation.ForJob(job.AsReadOnly(), preferred.AsReadOnly()).ViewId);
    }

    [Fact]
    public void OrphanedJobFallsBackToItsSpace()
    {
        var space = AddSpace(CreateProject(3, _user), 4);
        var job = AddJob(space, 7);

        var request = JobNavigation.ForJob(job.AsReadOnly());

        Assert.Equal(3, request.ProjectId);
        Assert.Equal(4, request.SpaceId);
        Assert.Null(request.ViewId);
        Assert.Null(request.JobId);
    }
}
