using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Serilog;

namespace Refund.Services.Core.DataManager;

public partial class DataManager
{
    /// <summary>
    /// Creates a new folder in the specified view.
    /// </summary>
    public async Task<ReadOnlyFolder> CreateFolder(ReadOnlyUser user, ReadOnlyView view, string alias, ReadOnlyFolder parentFolder = null)
    {
        ReadOnlyFolder createdFolder = null;
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);

                Folder parent = null;
                if (parentFolder != null)
                {
                    parent = originalView.FindFolder(parentFolder.Id);
                    if (parent == null)
                        throw new Exception($"Parent folder {parentFolder.Id} not found");
                }

                var folder = new Folder
                {
                    Id = originalView.GetNextFolderId(),
                    Alias = alias,
                    CreationDate = DateTime.Now,
                    CreatedBy = originalUser,
                    UpdateDate = DateTime.Now,
                    UpdatedBy = originalUser
                };

                originalView.AddFolder(folder, parent);

                parent?.UpdateLayout(originalView.Space);
                parent?.UpdateDiagramLayout(originalView.Space);
                originalView.UpdateDiagramLayout(originalView.Space);

                TouchAndSave(originalView, originalUser);

                createdFolder = folder.AsReadOnly();
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to create folder in view {ViewId} by user {UserId}", view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));

        return createdFolder;
    }

    /// <summary>
    /// Deletes a folder, ungrouping its contents to the parent level.
    /// </summary>
    public async Task DeleteFolder(ReadOnlyUser user, ReadOnlyView view, ReadOnlyFolder folder)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);

                Folder originalFolder = originalView.FindFolder(folder.Id);
                if (originalFolder == null)
                    throw new Exception($"Folder {folder.Id} not found");

                Folder parentFolder = originalFolder.ParentFolder;

                originalView.RemoveFolder(originalFolder);

                parentFolder?.UpdateLayout(originalView.Space);
                parentFolder?.UpdateDiagramLayout(originalView.Space);
                originalView.UpdateDiagramLayout(originalView.Space);

                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to delete folder {FolderId} in view {ViewId} by user {UserId}", folder.Id, view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    /// <summary>
    /// Updates a folder's properties (rename, color, etc.)
    /// </summary>
    public async Task UpdateFolder(ReadOnlyUser user, ReadOnlyView view, ReadOnlyFolder folder, Action<Folder> updateAction)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);

                Folder originalFolder = originalView.FindFolder(folder.Id);
                if (originalFolder == null)
                    throw new Exception($"Folder {folder.Id} not found");

                updateAction?.Invoke(originalFolder);
                originalFolder.UpdateDate = DateTime.Now;
                originalFolder.UpdatedBy = originalUser;

                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to update folder {FolderId} in view {ViewId} by user {UserId}", folder.Id, view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    /// <summary>
    /// Moves an item (job or folder) to a new position within a folder's items list.
    /// </summary>
    public async Task ReorderItemInFolder(ReadOnlyUser user, ReadOnlyView view, ReadOnlyFolder folder, IViewItem item, int newIndex)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);

                Folder originalFolder = originalView.FindFolder(folder.Id);
                if (originalFolder == null)
                    throw new Exception($"Folder {folder.Id} not found");

                IFolderContent mutableItem = item.ItemType switch
                {
                    ItemType.Job => _dataRepository.FindJob(view.Space.Project.Id, view.Space.Id, item.Id)
                        ?? throw new Exception($"Job {item.Id} not found"),
                    ItemType.Folder => (IFolderContent)(originalView.FindFolder(item.Id)
                        ?? throw new Exception($"Folder {item.Id} not found")),
                    _ => throw new Exception($"Unknown item type: {item.ItemType}")
                };

                originalFolder.MoveItem(mutableItem, newIndex);

                originalFolder.UpdateDate = DateTime.Now;
                originalFolder.UpdatedBy = originalUser;
                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to reorder item {ItemId} in folder {FolderId} in view {ViewId} by user {UserId}", item.Id, folder.Id, view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    /// <summary>
    /// Moves a job to a target folder (null = root level).
    /// </summary>
    public async Task MoveJobToFolder(ReadOnlyUser user, ReadOnlyView view, ReadOnlyJob job, ReadOnlyFolder targetFolder)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);
                var originalJob = ResolveJob(job.Space.Project.Id, job.Space.Id, job.Id);

                Folder target = null;
                if (targetFolder != null)
                {
                    target = originalView.FindFolder(targetFolder.Id);
                    if (target == null)
                        throw new Exception($"Target folder {targetFolder.Id} not found");
                }

                Folder sourceFolder = originalView.Folders.FirstOrDefault(f => f.Items.Contains(originalJob));

                originalView.MoveJobToFolder(originalJob, target);

                sourceFolder?.UpdateLayout(originalView.Space);
                target?.UpdateLayout(originalView.Space);
                sourceFolder?.UpdateDiagramLayout(originalView.Space);
                target?.UpdateDiagramLayout(originalView.Space);
                originalView.UpdateDiagramLayout(originalView.Space);

                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to move job {JobId} to folder in view {ViewId} by user {UserId}", job.Id, view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }

    /// <summary>
    /// Moves a folder to a new parent folder (null = root level).
    /// </summary>
    public async Task MoveFolderToFolder(ReadOnlyUser user, ReadOnlyView view, ReadOnlyFolder folder, ReadOnlyFolder targetFolder)
    {
        await ExecuteWithLock(async () =>
        {
            try
            {
                var originalUser = ResolveUser(user.Id);
                var originalView = ResolveView(view.Space.Project.Id, view.Space.Id, view.Id);

                Folder originalFolder = originalView.FindFolder(folder.Id);
                if (originalFolder == null)
                    throw new Exception($"Folder {folder.Id} not found");

                Folder oldParent = originalFolder.ParentFolder;

                Folder target = null;
                if (targetFolder != null)
                {
                    target = originalView.FindFolder(targetFolder.Id);
                    if (target == null)
                        throw new Exception($"Target folder {targetFolder.Id} not found");
                }

                originalView.MoveFolderToFolder(originalFolder, target);

                oldParent?.UpdateLayout(originalView.Space);
                target?.UpdateLayout(originalView.Space);
                oldParent?.UpdateDiagramLayout(originalView.Space);
                target?.UpdateDiagramLayout(originalView.Space);
                originalView.UpdateDiagramLayout(originalView.Space);

                TouchAndSave(originalView, originalUser);
            }
            catch (Exception e)
            {
                Log.ForContext<DataManager>().Error(e, "Failed to move folder {FolderId} in view {ViewId} by user {UserId}", folder.Id, view.Id, user.Id);
                throw;
            }
        });

        await ViewUpdated.InvokeHierarchy(view, GroupName.ViewHierarchy(view.Space.Project.Id, view.Space.Id, view.Id));
    }
}
