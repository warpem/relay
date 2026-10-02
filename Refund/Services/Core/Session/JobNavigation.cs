using Refund.DataModel.ReadOnly;

namespace Refund.Services.Core.Session;

/// <summary>Resolves a job's view, folder and factory context when opening it from an overview.</summary>
public static class JobNavigation
{
    public static NavigationRequest ForJob(ReadOnlyJob job, ReadOnlyView preferredView = null)
    {
        var view = preferredView?.Jobs.Contains(job) == true
            ? preferredView
            : job.Space.Views.FirstOrDefault(v => v.Jobs.Contains(job));
        var instance = job.FactoryInstanceId is { } id ? view?.FindFactoryInstance(id) : null;
        var folder = instance != null
            ? view.Folders.FirstOrDefault(f => f.Items.OfType<ReadOnlyFactoryInstance>().Any(i => i.Id == instance.Id))
            : view?.FindFolderContainingJob(job.Id);

        return new NavigationRequest
        {
            ProjectId = job.Space.Project.Id,
            SpaceId = job.Space.Id,
            ViewId = view?.Id,
            FolderId = folder?.Id,
            FactoryInstanceId = instance?.Id,
            JobId = view != null ? job.Id : null
        };
    }
}
