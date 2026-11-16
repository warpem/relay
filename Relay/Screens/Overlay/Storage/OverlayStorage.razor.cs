using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Refund.Components;
using Refund.DataModel;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Services.Storage;
using Refund.Utils;

namespace Relay.Screens.Overlay.Storage;

/// <summary>
/// The storage assistant: how much every job the user can see keeps on disk (bytes and files), as a cushion treemap, a
/// breakdown and a table, with clearing and intermediate-file cleaning. Measurements come from the storage service and
/// arrive while the page is open.
/// </summary>
public partial class OverlayStorage : ComponentBase, IAsyncDisposable
{
    [Inject] private DataManager DataManager { get; set; } = default!;
    [Inject] private RelaySession Session { get; set; } = default!;
    [Inject] private StorageService Storage { get; set; } = default!;
    [Inject] private INotificationService Toasts { get; set; } = default!;
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private sealed record Column(string Key, string Label, string Class, string Help = null);

    private static readonly Column[] Columns =
    [
        new("job", "Job", "c-job"), new("type", "Type", "c-type"), new("location", "Location", "c-location"),
        new("person", "Person", "c-person", "Who last ran the job (or created it)"),
        new("modified", "Modified", "c-num c-modified", "Time since anything in the job's folder last changed"),
        new("size", "Size", "c-num", "Bytes on disk, which is what the storage quota counts"),
        new("files", "Files", "c-num", "Files, folders and links: what the file-count quota counts"),
        new("cleanable", "Intermediate", "c-num", "What cleaning intermediate files would remove"),
        new("usedby", "Used by", "c-num", "Jobs that read this job's files"),
        new("measured", "Measured", "c-measured"),
    ];

    private static readonly SelectOption<string>[] StatusOptions =
    [
        new() { Value = "", Text = "Any status" }, new() { Value = nameof(StorageStatus.Finished), Text = "Finished" },
        new() { Value = nameof(StorageStatus.FailedOrAborted), Text = "Failed or aborted" },
        new() { Value = nameof(StorageStatus.Active), Text = "Queued or running" }, new() { Value = nameof(StorageStatus.NeverRun), Text = "Not run" },
    ];

    private static readonly SelectOption<string>[] AgeOptions =
    [
        new() { Value = "0", Text = "Modified any time" }, new() { Value = "1", Text = "Untouched for a month" },
        new() { Value = "3", Text = "Untouched for 3 months" }, new() { Value = "6", Text = "Untouched for 6 months" },
        new() { Value = "12", Text = "Untouched for a year" },
    ];

    private ElementReference _root, _tableScroll;
    private readonly StorageFilter _filter = new();
    private List<StorageRow> _rows = [], _shown = [];
    private List<StorageLeftoverRow> _leftovers = [], _leftoversShown = [];
    private List<StorageBreakdownLine> _byType = [], _byPerson = [], _typesInScope = [];
    private StorageTotals _totals;
    private TreemapPayload _payload;
    private Dictionary<int, byte> _owners = [];
    private StorageProgress _progress;
    private IReadOnlyList<FileSystemUsage> _fileSystems = [];
    private readonly HashSet<(int, int, int)> _selected = [];
    private StorageRow _hover, _focused;
    private (int, int, int)? _focusedKey;
    private StorageLeftoverRow _confirmLeftover;
    private string _metric = "bytes", _colorBy = "age", _breakdown = "types", _tab = "jobs", _sort = "size", _confirm;
    private bool _descending = true, _dark, _dirty, _disposed;
    private readonly HashSet<string> _deleting = [];
    private int _pendingRefresh;
    private (double W, double H) _area = (1200, 400);
    private DateTime _now = DateTime.UtcNow, _lastRebuild = DateTime.MinValue;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<GroupEventSubscription> _subscriptions = [];
    private IJSObjectReference _module;

    private bool IsAdmin => Session.User?.Role == UserRole.Admin;
    private static (int, int, int) Key(StorageRow r) => (r.Space.Project.Id, r.Space.Id, r.Job.Id);

    protected override void OnInitialized()
    {
        _dark = Session.ColorTheme == ColorTheme.Dark;
        Session.OnThemeChanged += ThemeChanged;
        void Dirty() { _dirty = true; }
        _subscriptions.Add(DataManager.StorageUpdated.Add(GroupName.Space(null, null), _ => { Dirty(); return Task.CompletedTask; }));
        _subscriptions.Add(DataManager.JobUpdated.Add(GroupName.Job(null, null, null), _ => { Dirty(); return Task.CompletedTask; }));
        _subscriptions.Add(DataManager.JobDeleted.Add(GroupName.Job(null, null, null), _ => { Dirty(); return Task.CompletedTask; }));
        _subscriptions.Add(DataManager.JobCreated.Add(GroupName.Job(null, null, null), _ => { Dirty(); return Task.CompletedTask; }));
        Rebuild();
        Poll();
        _ = RefreshLoop();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try { _module = await Js.InvokeAsync<IJSObjectReference>("import", "./Screens/Overlay/Storage/OverlayStorage.razor.js"); }
        catch (JSDisconnectedException) { }
    }

    private Task ThemeChanged()
    {
        _dark = Session.ColorTheme == ColorTheme.Dark;
        return InvokeAsync(StateHasChanged);
    }

    // Progress and capacity every 2 s; new measurements every 5 s while nothing is selected (rows mustn't move under
    // someone choosing what to clear: then a Refresh button collects them).
    private async Task RefreshLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
                await InvokeAsync(() =>
                {
                    Poll();
                    if (_dirty && DateTime.UtcNow - _lastRebuild > TimeSpan.FromSeconds(5))
                    {
                        if (_selected.Count == 0 && _confirm == null && _confirmLeftover == null) Rebuild();
                        else _pendingRefresh = Math.Max(1, CountChanged());
                    }
                    StateHasChanged();
                });
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void Poll()
    {
        _progress = Storage.GetProgress();
        var spaces = DataManager.GetUserProjects(Session.User).SelectMany(p => p.Spaces).ToList();
        _fileSystems = Storage.GetFileSystems(spaces);
    }

    private int CountChanged()
    {
        int changed = 0;
        foreach (var row in _rows)
            if (!ReferenceEquals(row.Job.Storage, row.Storage)) changed++;
        return changed;
    }

    private void Refresh()
    {
        Rebuild();
        StateHasChanged();
    }

    private void Rebuild()
    {
        _now = DateTime.UtcNow;
        _lastRebuild = DateTime.UtcNow;
        _dirty = false;
        _pendingRefresh = 0;
        _rows = StorageView.Rows(DataManager, Session.User);
        _leftovers = StorageView.Leftovers(DataManager, Session.User);
        // Selections and focus are kept by key: rows are rebuilt from the live model.
        var keys = _rows.Select(Key).ToHashSet();
        _selected.IntersectWith(keys);
        if (_focusedKey is { } fk) _focused = _rows.FirstOrDefault(r => Key(r) == fk);
        _hover = null;
        _owners = TreemapPayload.OwnerSlots(_rows);
        Apply();
    }

    private void Apply()
    {
        // The type filter lists the types in scope regardless of the other filters, so choices never vanish.
        var scopeOnly = new StorageFilter { ProjectId = _filter.ProjectId, SpaceId = _filter.SpaceId, Folders = _filter.Folders };
        _typesInScope = StorageView.ByType(StorageView.Apply(_rows, scopeOnly, _now));
        _shown = StorageView.Apply(_rows, _filter, _now).ToList();
        SortRows();
        _totals = StorageView.Totals(_shown);
        var withoutType = _filter.Copy(); withoutType.Types = [];
        var withoutPerson = _filter.Copy(); withoutPerson.PersonId = null;
        _byType = StorageView.ByType(StorageView.Apply(_rows, withoutType, _now));
        _byPerson = StorageView.ByPerson(StorageView.Apply(_rows, withoutPerson, _now));
        _leftoversShown = _leftovers.Where(l => (_filter.ProjectId is not int p || l.Space.Project.Id == p) && (_filter.SpaceId is not int s || l.Space.Id == s)).ToList();
        BuildPayload();
    }

    private void BuildPayload()
    {
        // Tiles smaller than about 3 px² fold into "smaller items".
        double minShare = 3.0 / Math.Max(10_000, _area.W * _area.H);
        bool anyFilter = _filter.Types.Count > 0 || _filter.PersonId != null || _filter.Statuses.Count > 0 || _filter.OlderThanMonths > 0 || _filter.Search.Length > 0;
        _payload = TreemapPayload.Build(_shown, anyFilter ? [] : _leftoversShown, _filter, _owners, _now, minShare);
    }

    private void SetFilter(Action<StorageFilter> change)
    {
        change(_filter);
        Apply();
    }

    private void SetMetric(string metric)
    {
        _metric = metric;
        if (_sort is "size" or "files") _sort = metric == "files" ? "files" : "size";
        SortRows();
    }

    private void Sort(string key)
    {
        if (_sort == key) _descending = !_descending;
        else { _sort = key; _descending = key is "size" or "files" or "cleanable" or "usedby" or "modified"; }
        SortRows();
    }

    private void SortRows()
    {
        Comparison<StorageRow> cmp = _sort switch
        {
            "job" => (a, b) => string.Compare(a.Job.QualifiedName, b.Job.QualifiedName, StringComparison.OrdinalIgnoreCase),
            "type" => (a, b) => string.Compare(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase),
            "location" => (a, b) => string.Compare(a.Location, b.Location, StringComparison.OrdinalIgnoreCase),
            "person" => (a, b) => string.Compare(a.Person?.Name, b.Person?.Name, StringComparison.OrdinalIgnoreCase),
            // "Modified" ascending means most recent first; descending puts the oldest on top.
            "modified" => (a, b) => Nullable.Compare(b.Modified, a.Modified),
            "files" => (a, b) => a.Entries.CompareTo(b.Entries),
            "cleanable" => (a, b) => a.Cleanable.Allocated.CompareTo(b.Cleanable.Allocated),
            "usedby" => (a, b) => a.UsedBy.CompareTo(b.UsedBy),
            "measured" => (a, b) => Nullable.Compare(a.Storage?.MeasuredAt, b.Storage?.MeasuredAt),
            _ => (a, b) => a.Allocated != b.Allocated ? a.Allocated.CompareTo(b.Allocated) : a.Entries.CompareTo(b.Entries),
        };
        // Unmeasured jobs stay at the bottom whichever way numbers sort.
        _shown.Sort((a, b) =>
        {
            if (_sort is "size" or "files" or "cleanable" && a.Measured != b.Measured) return a.Measured ? -1 : 1;
            int c = cmp(a, b);
            if (_descending) c = -c;
            return c != 0 ? c : a.Job.Id.CompareTo(b.Job.Id);
        });
    }

    // ---- Treemap ----

    private Task MapResized((double Width, double Height) size)
    {
        double before = _area.W * _area.H, after = size.Width * size.Height;
        _area = (size.Width, size.Height);
        if (after > 0 && (after > before * 1.4 || after < before / 1.4))
        {
            BuildPayload();
            StateHasChanged();
        }
        return Task.CompletedTask;
    }

    private async Task SelectFromMap(TreemapTarget target)
    {
        if (target.Row == null) return;
        _tab = "jobs";
        _focused = target.Row;
        _focusedKey = Key(target.Row);
        int index = _shown.FindIndex(r => Key(r) == _focusedKey);
        StateHasChanged();
        if (index >= 0 && _module != null)
            try { await _module.InvokeVoidAsync("scrollToRow", _tableScroll, index, 34); }
            catch (JSDisconnectedException) { }
    }

    private void ZoomFromMap(TreemapTarget target) => Zoom(new StorageFilter { ProjectId = target.ProjectId, SpaceId = target.SpaceId, Folders = target.Folders });

    private void Zoom(StorageFilter scope)
    {
        _filter.ProjectId = scope.ProjectId;
        _filter.SpaceId = scope.SpaceId;
        _filter.Folders = scope.Folders;
        Apply();
    }

    private IEnumerable<(string Label, StorageFilter Scope, bool Last)> Crumbs()
    {
        var crumbs = new List<(string, StorageFilter)> { ("All projects", new StorageFilter()) };
        var project = _filter.ProjectId is int p ? DataManager.Projects.FirstOrDefault(x => x.Id == p) : null;
        if (project != null) crumbs.Add((project.QualifiedName, new StorageFilter { ProjectId = project.Id }));
        var space = project != null && _filter.SpaceId is int s ? project.Spaces.FirstOrDefault(x => x.Id == s) : null;
        if (space != null) crumbs.Add((space.QualifiedName, new StorageFilter { ProjectId = project.Id, SpaceId = space.Id }));
        for (int i = 0; i < _filter.Folders.Length && space != null; i++)
            crumbs.Add((_filter.Folders[i], new StorageFilter { ProjectId = project.Id, SpaceId = space.Id, Folders = _filter.Folders[..(i + 1)] }));
        return crumbs.Select((c, i) => (c.Item1, c.Item2, i == crumbs.Count - 1));
    }

    private IEnumerable<(string Color, string Label)> Legend()
    {
        IEnumerable<(string, string)> keys = _colorBy switch
        {
            "stage" => StorageStages.Names.Select((n, i) => (i < 3 ? $"var(--st-stage-{i})" : "var(--st-neutral)", n)),
            "owner" => _owners.OrderBy(o => o.Value).Select(o => ($"var(--st-stage-{o.Value})", PersonName(o.Key)))
                .Append(("var(--st-neutral)", _owners.Count == 0 ? "Everyone" : "Everyone else")),
            _ => new[] { "Under a month", "1–3 months", "3–6 months", "6–12 months", "Over a year" }.Select((n, i) => ($"var(--st-age-{i})", n)),
        };
        return keys.Append(("var(--st-leftover)", "Folders of deleted jobs"));
    }

    // ---- Text ----

    private string TotalsText()
    {
        var t = _totals;
        string text = $"{StorageFormat.Bytes(t.Allocated)} · {StorageFormat.Count(t.Entries)} files in {t.Jobs:N0} {(t.Jobs == 1 ? "job" : "jobs")}";
        if (t.Cleanable.Entries > 0) text += $" · {StorageFormat.Bytes(t.Cleanable.Allocated)} in intermediate files";
        if (t.Jobs > t.Measured) text += $" · {t.Jobs - t.Measured:N0} not measured yet";
        return text;
    }

    private string StatusText(StorageProgress p)
    {
        if (!p.Enabled) return "Storage measurements are switched off on this server.";
        if (p.Jobs == 0) return "No jobs to measure yet.";
        int pending = p.Pending.Where(kv => kv.Key != StorageClass.Background).Sum(kv => kv.Value);
        string text = p.Measured >= p.Jobs && pending == 0
            ? $"All {p.Jobs:N0} jobs measured."
            : $"Measured {p.Measured:N0} of {p.Jobs:N0} jobs";
        if (p.Paused) return text + (IsAdmin ? " · Paused; resume it in Settings › State." : " · Paused by an administrator.");
        if (p.Walking.Count > 0 || pending > 0)
        {
            text = text.TrimEnd('.');
            if (p.EntriesPerSecond >= 1) text += $" · {p.EntriesPerSecond:N0} files/s";
            if (p.Remaining is { } r && p.Measured < p.Jobs) text += $" · about {Duration(r)} left";
        }
        return text;
    }

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes < 1.5 ? "a minute" : t.TotalHours < 1.5 ? $"{t.TotalMinutes:0} minutes" : t.TotalDays < 2 ? $"{t.TotalHours:0} hours" : $"{t.TotalDays:0} days";

    private string MeasuredText(StorageRow row)
    {
        if (_progress?.Walking.Any(w => w.Target.IsJob && w.Target.JobId == row.Job.Id && w.Target.SpaceId == row.Space.Id && w.Target.ProjectId == row.Space.Project.Id) == true)
            return "Measuring…";
        if (DataManager.IsCleaning(row.Job))
            return "Cleaning…";
        return row.Storage is not { } s ? "Not yet" : _now - s.MeasuredAt < TimeSpan.FromMinutes(1) ? "just now" : StorageFormat.Age(_now - s.MeasuredAt) + " ago";
    }

    private long Value(StorageBreakdownLine line) => _metric == "files" ? line.Entries : line.Allocated;

    private string PersonName(int id) => DataManager.Users.FirstOrDefault(u => u.Id == id)?.Name ?? "Someone";

    private string StatusValue => _filter.Statuses.Count == 1 ? _filter.Statuses.First().ToString() : "";

    private void ToggleBreakdown(StorageBreakdownLine line)
    {
        if (_breakdown == "types")
            SetFilter(f => { if (!f.Types.Remove(line.Key)) f.Types.Add(line.Key); });
        else
            SetFilter(f => f.PersonId = f.PersonId?.ToString() == line.Key || line.Key == "0" ? null : int.Parse(line.Key));
    }

    // ---- Selection and actions ----

    private bool? AllState => _shown.Count == 0 || !_shown.Any(r => _selected.Contains(Key(r))) ? false : _shown.All(r => _selected.Contains(Key(r))) ? true : null;

    private void ToggleAll(bool? value)
    {
        if (value == true) foreach (var r in _shown) _selected.Add(Key(r));
        else _selected.Clear();
    }

    private void Toggle(StorageRow row, bool on)
    {
        if (on) _selected.Add(Key(row)); else _selected.Remove(Key(row));
    }

    private List<StorageRow> SelectedRows() => _rows.Where(r => _selected.Contains(Key(r))).ToList();

    private bool CanClearAny(List<StorageRow> rows) => rows.Any(r => DataManager.ClearBlocker(Session.User, r.Job) == null);
    private bool CanCleanAny(List<StorageRow> rows) => rows.Any(r => DataManager.CleanBlocker(Session.User, r.Job) == null);

    private (List<StorageRow> Ok, List<(StorageRow Row, string Reason)> Skipped) ClearPlan(List<StorageRow> rows)
    {
        var ok = new List<StorageRow>(); var skipped = new List<(StorageRow, string)>();
        foreach (var r in rows)
            if (DataManager.ClearBlocker(Session.User, r.Job) is { } reason) skipped.Add((r, reason)); else ok.Add(r);
        return (ok, skipped);
    }

    private (List<StorageRow> Ok, List<(StorageRow Row, string Reason)> Skipped) CleanPlan(List<StorageRow> rows)
    {
        var ok = new List<StorageRow>(); var skipped = new List<(StorageRow, string)>();
        foreach (var r in rows.Where(r => r.Job.IntermediateRule != null))
            if (DataManager.CleanBlocker(Session.User, r.Job) is { } reason) skipped.Add((r, reason)); else ok.Add(r);
        return (ok, skipped);
    }

    // Clearing and cleaning run in the background: the jobs leave the selection at once, so more can be picked and
    // started while they work; each batch reports with a toast when it is done.
    private void ClearSelected(List<StorageRow> rows)
    {
        _confirm = null;
        foreach (var r in rows) _selected.Remove(Key(r));
        var user = Session.User;
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await DataManager.ClearJobs(user, rows.Select(r => r.Job), started: RefreshNow);
                string message = $"Cleared {result.Cleared.Count:N0} {(result.Cleared.Count == 1 ? "job" : "jobs")}";
                var cleared = StorageView.Totals(rows.Where(r => result.Cleared.Contains(r.Job)));
                if (cleared.Measured > 0)
                    message += $", freeing about {StorageFormat.Bytes(cleared.Allocated)} and {StorageFormat.Count(cleared.Entries)} files";
                if (result.Skipped.Count > 0)
                    message += $". {result.Skipped.Count:N0} skipped: {string.Join("; ", result.Skipped.GroupBy(x => x.Reason).Select(g => $"{g.Key} ({g.Count()})"))}";
                _ = result.Cleared.Count > 0 ? Toasts.ShowSuccessToastAsync(message) : Toasts.ShowWarningToastAsync(message);
                Storage.Request(result.Cleared);
            }
            catch (Exception e)
            {
                _ = Toasts.ShowErrorToastAsync($"Couldn't clear jobs: {e.Message}");
            }
            await RefreshNow();
        });
    }

    private void CleanSelected(List<StorageRow> rows)
    {
        _confirm = null;
        foreach (var r in rows) _selected.Remove(Key(r));
        var user = Session.User;
        _ = Task.Run(async () =>
        {
            int cleaned = 0; UsagePart freed = default; var failed = new List<string>();
            // Every job is marked as cleaning at once; DataManager works on a few folders at a time.
            var work = rows.Select(async row =>
            {
                try
                {
                    var result = await DataManager.CleanIntermediate(user, row.Job);
                    lock (failed) { cleaned++; freed += result.Estimated; }
                }
                catch (Exception e) { lock (failed) failed.Add($"{row.Job.QualifiedName}: {e.Message}"); }
                await RefreshNow();
            }).ToList();
            await RefreshNow();
            await Task.WhenAll(work);
            if (failed.Count > 0) _ = Toasts.ShowErrorToastAsync($"Couldn't clean {failed.Count} {(failed.Count == 1 ? "job" : "jobs")}: {string.Join("; ", failed.Take(3))}");
            if (cleaned > 0)
                _ = Toasts.ShowSuccessToastAsync($"Cleaned {cleaned:N0} {(cleaned == 1 ? "job" : "jobs")}" +
                    (freed.Entries > 0 ? $", freeing about {StorageFormat.Bytes(freed.Allocated)} and {StorageFormat.Count(freed.Entries)} files" : ""));
        });
    }

    private void DeleteLeftover(StorageLeftoverRow leftover)
    {
        _confirmLeftover = null;
        var key = LeftoverKey(leftover);
        _deleting.Add(key);
        var user = Session.User;
        _ = Task.Run(async () =>
        {
            try
            {
                await DataManager.DeleteLeftover(user, leftover.Space, leftover.Leftover.Name);
                _ = Toasts.ShowSuccessToastAsync($"Deleted {leftover.Leftover.Name} from {leftover.Space.QualifiedName}, freeing {StorageFormat.Bytes(leftover.Leftover.Total.Allocated)}");
            }
            catch (Exception e)
            {
                _ = Toasts.ShowErrorToastAsync($"Couldn't delete {leftover.Leftover.Name}: {e.Message}");
            }
            await InvokeIfAlive(() => _deleting.Remove(key));
            await RefreshNow();
        });
    }

    private static string LeftoverKey(StorageLeftoverRow l) => $"{l.Space.Project.Id}/{l.Space.Id}/{l.Leftover.Name}";
    private bool IsDeleting(StorageLeftoverRow l) => _deleting.Contains(LeftoverKey(l));

    // Rebuilds from the live model now (statuses changed), unless the page has closed meanwhile.
    private Task RefreshNow() => InvokeIfAlive(() => { Rebuild(); StateHasChanged(); });

    private async Task InvokeIfAlive(Action action)
    {
        if (_disposed) return;
        try { await InvokeAsync(() => { if (!_disposed) action(); }); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) when (_disposed) { }
    }

    private void MeasureSelected()
    {
        var rows = SelectedRows();
        Storage.Request(rows.Select(r => r.Job));
        _ = Toasts.ShowInfoToastAsync($"Measuring {rows.Count:N0} {(rows.Count == 1 ? "job" : "jobs")} next");
    }

    private void MeasureShownFirst()
    {
        var unmeasured = _shown.Where(r => !r.Measured).Select(r => r.Job).ToList();
        Storage.Request(unmeasured);
        Storage.RequestListing(_shown.Select(r => r.Space).Distinct());
        _ = Toasts.ShowInfoToastAsync($"Measuring {unmeasured.Count:N0} {(unmeasured.Count == 1 ? "job" : "jobs")} first");
    }

    // ---- Navigation ----

    private string JobHref(StorageRow row)
    {
        var r = JobNavigation.ForJob(row.Job);
        return $"/P{r.ProjectId}/S{r.SpaceId}" + (r.ViewId is int v ? $"/V{v}" : "") + (r.JobId is int j ? $"/J{j}" : "");
    }

    private async Task OpenJob(StorageRow row, MouseEventArgs e)
    {
        var request = JobNavigation.ForJob(row.Job);
        request.Overlay = OverlayScreenType.None;
        if (MouseUtils.IsNewTabClick(e)) await Session.OpenInNewTabAsync(request);
        else await Session.NavigateToAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Session.OnThemeChanged -= ThemeChanged;
        _subscriptions.UnsubscribeAndClear();
        _stop.Cancel();
        _stop.Dispose();
        try { if (_module != null) await _module.DisposeAsync(); }
        catch (JSDisconnectedException) { }
    }
}
