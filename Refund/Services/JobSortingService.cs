using Microsoft.JSInterop;
using Refund.Utils;

namespace Refund.Services;

public enum JobSortCriterion
{
    Custom,
    Id,
    LastModified,
    Status,
    Type,
    Name,
    Color
}

/// <summary>
/// Scoped sort state shared by all views and their folders, persisted in this browser.
/// </summary>
public class JobSortingService(IJSRuntime js)
{
    private const string StorageKey = "relay.jobSort";
    private Task _initialization;

    public JobSortCriterion Criterion { get; private set; } = JobSortCriterion.Custom;
    /// <summary>
    /// Grid direction, not sort order: items are always sorted ascending. True (default)
    /// lays them out from bottom-left to top-right; false from top-left to bottom-right.
    /// </summary>
    public bool IsAscending { get; private set; } = true;

    public event Func<Task> OnSortChanged;

    // Call after the first interactive render, when browser storage is available.
    public Task InitializeAsync() => _initialization ??= RestoreAsync();

    private async Task RestoreAsync()
    {
        try
        {
            var saved = await js.InvokeAsync<string>("localStorage.getItem", StorageKey);
            var parts = saved?.Split(':');
            if (parts?.Length != 2 ||
                !Enum.TryParse<JobSortCriterion>(parts[0], out var criterion) ||
                !Enum.IsDefined(criterion) ||
                !bool.TryParse(parts[1], out var ascending))
                return;

            Criterion = criterion;
            IsAscending = ascending;
        }
        catch (JSException)
        {
            // Storage may be blocked or the browser may have disconnected.
            return;
        }

        await OnSortChanged.InvokeAllAsync();
    }

    private async Task SaveAsync()
    {
        try
        {
            await js.InvokeVoidAsync("localStorage.setItem", StorageKey, $"{Criterion}:{IsAscending}");
        }
        catch (JSException)
        {
            // Keep sorting usable even when browser storage is unavailable.
        }
    }

    public async Task SetCriterion(JobSortCriterion criterion)
    {
        await InitializeAsync();
        if (Criterion == criterion)
            return;

        Criterion = criterion;
        await SaveAsync();
        await OnSortChanged.InvokeAllAsync();
    }

    public async Task SetAscending(bool ascending)
    {
        await InitializeAsync();
        if (IsAscending == ascending)
            return;

        IsAscending = ascending;
        await SaveAsync();
        await OnSortChanged.InvokeAllAsync();
    }

    public async Task ToggleDirection()
    {
        await InitializeAsync();
        await SetAscending(!IsAscending);
    }
}
