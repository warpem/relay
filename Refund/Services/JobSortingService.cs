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
/// Scoped service that holds the current sort state for the ViewScreen.
/// Sort preference is ephemeral (per-session, not persisted).
/// </summary>
public class JobSortingService
{
    public JobSortCriterion Criterion { get; private set; } = JobSortCriterion.Custom;
    public bool IsAscending { get; private set; } = true;

    public event Func<Task> OnSortChanged;

    public async Task SetCriterion(JobSortCriterion criterion)
    {
        if (Criterion == criterion)
            return;

        Criterion = criterion;
        await OnSortChanged.InvokeAllAsync();
    }

    public async Task SetAscending(bool ascending)
    {
        if (IsAscending == ascending)
            return;

        IsAscending = ascending;
        await OnSortChanged.InvokeAllAsync();
    }

    public async Task ToggleDirection()
    {
        IsAscending = !IsAscending;
        await OnSortChanged.InvokeAllAsync();
    }
}
