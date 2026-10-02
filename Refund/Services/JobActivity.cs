using Refund.DataModel;
using Refund.DataModel.ReadOnly;

namespace Refund.Services;

/// <summary>
/// Which jobs a recent-activity listing shows.
/// </summary>
public enum ActivityScope
{
    /// <summary>
    /// Jobs the user last submitted, or created if never submitted.
    /// </summary>
    Mine,

    /// <summary>
    /// Jobs in accessible projects that another user last submitted or created.
    /// </summary>
    Shared
}

/// <summary>
/// A job's most recent meaningful activity: when and by whom it was last submitted, or created if
/// it has never been submitted.
/// </summary>
/// <remarks>
/// Only user actions count. Execution progress, status transitions after submission, and metadata edits
/// don't move a job's activity, so an ordering built from it stays stable while jobs run.
/// </remarks>
public readonly record struct JobActivity(ReadOnlyJob Job, int ProjectId, int SpaceId, DateTime Timestamp, ReadOnlyUser Author)
{
    /// <summary>
    /// Scoped selection key for the job, unique across projects and spaces.
    /// </summary>
    public SelectionKey Key => SelectionKey.ForJob(ProjectId, SpaceId, Job.Id);

    /// <summary>
    /// Whether the activity was authored by the given user.
    /// </summary>
    public bool IsBy(ReadOnlyUser user) => user != null && Author != null && Author.Id == user.Id;

    /// <summary>
    /// Describes the latest submission (WaitingStarted) of a job, falling back to its creation.
    /// </summary>
    /// <returns>The activity, or null if the job isn't attached to a project space.</returns>
    public static JobActivity? Describe(ReadOnlyJob job)
    {
        var space = job?.Space;
        if (space?.Project == null)
            return null;

        var activityEvent = job.GetMostRecentEvent(EventType.WaitingStarted) ??
                            job.GetMostRecentEvent(EventType.Created);

        // Jobs saved before audit events existed have neither event; their last update is the only record
        return activityEvent != null
            ? new JobActivity(job, space.Project.Id, space.Id, activityEvent.Timestamp, activityEvent.Author)
            : new JobActivity(job, space.Project.Id, space.Id, job.UpdateDate, job.UpdatedBy);
    }

    /// <summary>
    /// Whether the activity belongs in the given scope for the user. Activity without a known author
    /// belongs to neither scope.
    /// </summary>
    public bool IsInScope(ActivityScope scope, ReadOnlyUser user)
    {
        if (Author == null || user == null)
            return false;

        return scope == ActivityScope.Mine ? IsBy(user) : !IsBy(user);
    }

    /// <summary>
    /// Builds the activity listing for a user: jobs from the given projects in the requested scope,
    /// newest first. Ties are broken by project, space and job ID (descending) so the order is total.
    /// </summary>
    /// <param name="projects">Projects the user can access.</param>
    /// <param name="user">The user whose activity scope is evaluated.</param>
    /// <param name="scope">Own or shared activity.</param>
    public static List<JobActivity> Snapshot(IEnumerable<ReadOnlyProject> projects, ReadOnlyUser user, ActivityScope scope)
    {
        var result = new List<JobActivity>();
        if (user == null)
            return result;

        foreach (var project in projects)
            foreach (var space in project.Spaces)
                foreach (var job in space.Jobs)
                    if (Describe(job) is { } activity && activity.IsInScope(scope, user))
                        result.Add(activity);

        result.Sort(NewestFirst);
        return result;
    }

    /// <summary>
    /// Orders activities newest first with a deterministic ID tie-break.
    /// </summary>
    public static int NewestFirst(JobActivity a, JobActivity b)
    {
        int c = b.Timestamp.CompareTo(a.Timestamp);
        if (c == 0) c = b.ProjectId.CompareTo(a.ProjectId);
        if (c == 0) c = b.SpaceId.CompareTo(a.SpaceId);
        if (c == 0) c = b.Job.Id.CompareTo(a.Job.Id);
        return c;
    }
}
