using System.Collections.ObjectModel;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;

namespace Refund.Services;

/// <summary>
/// Manages the selection state of cards (jobs, views, spaces, projects, folders) in the UI.
/// </summary>
public class CardSelectionService : IDisposable
{
    private RelaySession _session;
    private DataManager _dataManager;

    private readonly List<SelectionKey> _selected = new();

    public ReadOnlyCollection<SelectionKey> SelectedItems => new(_selected);

    private readonly List<GroupEventSubscription> _subscriptions = new();

    public event Func<Task> OnSelectionChanged;

    public CardSelectionService(RelaySession session, DataManager dataManager)
    {
        _session = session;
        _dataManager = dataManager;

        ResubscribeToMainScreen();
        _session.OnMainChanged += HandleMainScreenChanged;
        _session.OnFolderChanged += HandleFolderChanged;
        _session.OnFactoryDefinitionChanged += HandleFactoryContextChanged;
        _session.OnFactoryInstanceChanged += HandleFactoryContextChanged;
    }

    private void ResubscribeToMainScreen()
    {
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
        _subscriptions.Clear();

        switch (_session.CurrentMain)
        {
            case MainScreenType.Home:
                _subscriptions.Add(_dataManager.ProjectDeleted.Add(GroupName.Project(null),
                    async args => await Remove(SelectionKey.ForProject(args.Object.Id))));
                break;

            case MainScreenType.Project:
                _subscriptions.Add(_dataManager.SpaceDeleted.Add(GroupName.Space(_session.Project.Id, null),
                    async args => await Remove(SelectionKey.ForSpace(args.Object.Id))));
                break;

            case MainScreenType.Space:
                _subscriptions.Add(_dataManager.ViewDeleted.Add(GroupName.View(_session.Project.Id, _session.Space.Id, null),
                    async args => await Remove(SelectionKey.ForView(args.Object.Id))));
                _subscriptions.Add(_dataManager.FactoryDefinitionDeleted.Add(
                    GroupName.FactoryDefinition(_session.Project.Id, _session.Space.Id, null),
                    async args => await Remove(SelectionKey.ForFactoryDefinition(args.Object.Id))));
                break;

            case MainScreenType.View:
                _subscriptions.Add(_dataManager.JobDeleted.Add(GroupName.Job(_session.Project.Id, _session.Space.Id, null),
                    async args => await Remove(SelectionKey.ForJob(args.Object.Id))));
                _subscriptions.Add(_dataManager.FactoryInstanceDeleted.Add(
                    GroupName.FactoryInstance(_session.Project.Id, _session.Space.Id, null),
                    async args => await Remove(SelectionKey.ForFactoryInstance(args.Object.Id))));
                break;
        }
    }

    private async Task HandleMainScreenChanged()
    {
        ResubscribeToMainScreen();
        await Clear();
    }

    private async Task HandleFolderChanged()
    {
        await Clear();
    }

    private async Task HandleFactoryContextChanged()
    {
        await Clear();
    }

    public async Task AddRange(IEnumerable<SelectionKey> keys)
    {
        var toAdd = keys.Distinct().Where(k => !_selected.Contains(k)).ToList();
        if (toAdd.Count > 0)
        {
            _selected.AddRange(toAdd);
            await OnSelectionChanged.InvokeAllAsync();
        }
    }

    public async Task Remove(SelectionKey key)
    {
        if (_selected.Remove(key))
            await OnSelectionChanged.InvokeAllAsync();
    }

    public async Task RemoveRange(IEnumerable<SelectionKey> keys)
    {
        var set = keys.ToHashSet();
        int removed = _selected.RemoveAll(k => set.Contains(k));
        if (removed > 0)
            await OnSelectionChanged.InvokeAllAsync();
    }

    public async Task Replace(IEnumerable<SelectionKey> keys)
    {
        await Clear();
        await AddRange(keys);
    }

    public async Task Clear()
    {
        if (_selected.Count > 0)
        {
            _selected.Clear();
            await OnSelectionChanged.InvokeAllAsync();
        }
    }

    /// <summary>
    /// Checks whether the item with the given key is selected.
    /// </summary>
    public bool IsSelected(SelectionKey key) => _selected.Contains(key);

    /// <summary>
    /// Gets all selected IDs of a specific item type.
    /// </summary>
    public IEnumerable<int> IdsOfType(ItemType type) =>
        _selected.Where(k => k.Type == type).Select(k => k.Id);

    public void Dispose()
    {
        _session.OnMainChanged -= HandleMainScreenChanged;
        _session.OnFolderChanged -= HandleFolderChanged;
        _session.OnFactoryDefinitionChanged -= HandleFactoryContextChanged;
        _session.OnFactoryInstanceChanged -= HandleFactoryContextChanged;
        foreach (var sub in _subscriptions)
            sub.Unsubscribe();
    }
}
