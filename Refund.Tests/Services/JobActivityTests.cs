using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Jobs.Common.Import.ImportMap;
using Refund.Services;

namespace Refund.Tests.Services;

[Collection("JobRegistry")]
public class JobActivityTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 12, 0, 0);

    private readonly User _me = new() { Id = 1, Username = "me" };
    private readonly User _other = new() { Id = 2, Username = "other" };

    public JobActivityTests() => JobRegistry.EnsurePopulated();

    [Fact]
    public void LastSubmissionDefinesActivityAndCreationIsTheFallback()
    {
        var project = CreateProject(1, owner: _me);
        var space = AddSpace(project, 0);
        var queued = AddJob(space, 1, (EventType.Created, T0, _other),
                                      (EventType.WaitingStarted, T0.AddHours(1), _other),
                                      (EventType.WaitingStarted, T0.AddHours(3), _me),
                                      (EventType.Finished, T0.AddHours(4), _other));
        var building = AddJob(space, 2, (EventType.Created, T0.AddHours(2), _other));

        var queuedActivity = JobActivity.Describe(queued.AsReadOnly())!.Value;
        var buildingActivity = JobActivity.Describe(building.AsReadOnly())!.Value;

        Assert.Equal(T0.AddHours(3), queuedActivity.Timestamp);
        Assert.Equal(_me.Id, queuedActivity.Author.Id);
        Assert.Equal(T0.AddHours(2), buildingActivity.Timestamp);
        Assert.Equal(_other.Id, buildingActivity.Author.Id);
    }

    [Fact]
    public void OwnershipFollowsTheSubmitterNotTheProjectOwner()
    {
        // Project owned by someone else; ownership must not leak into the activity scope
        var project = CreateProject(1, owner: _other, members: [_me]);
        var space = AddSpace(project, 0);
        AddJob(space, 1, (EventType.Created, T0, _me));
        AddJob(space, 2, (EventType.Created, T0, _other), (EventType.WaitingStarted, T0.AddMinutes(5), _me));
        AddJob(space, 3, (EventType.Created, T0, _me), (EventType.WaitingStarted, T0.AddMinutes(10), _other));
        AddJob(space, 4, (EventType.Created, T0, _other));
        AddJob(space, 5, (EventType.Created, T0, null));

        var mine = JobActivity.Snapshot([project.AsReadOnly()], _me.AsReadOnly(), ActivityScope.Mine);
        var shared = JobActivity.Snapshot([project.AsReadOnly()], _me.AsReadOnly(), ActivityScope.Shared);

        Assert.Equal([2, 1], mine.Select(a => a.Job.Id));
        Assert.Equal([3, 4], shared.Select(a => a.Job.Id));
    }

    [Fact]
    public void SnapshotOrderIgnoresExecutionProgressAndMetadataEdits()
    {
        var project = CreateProject(1, owner: _me);
        var space = AddSpace(project, 0);
        var older = AddJob(space, 1, (EventType.Created, T0, _me), (EventType.WaitingStarted, T0.AddMinutes(1), _me));
        AddJob(space, 2, (EventType.Created, T0.AddMinutes(2), _me));

        var before = Keys(JobActivity.Snapshot([project.AsReadOnly()], _me.AsReadOnly(), ActivityScope.Mine));

        // A run progressing, finishing, and another user editing it all bump the audit fields
        older.Events.Add(new JobEvent(EventType.StagingStarted, T0.AddMinutes(30), _me));
        older.Events.Add(new JobEvent(EventType.RunningStarted, T0.AddMinutes(31), _me));
        older.Events.Add(new JobEvent(EventType.Finished, T0.AddHours(5), _me));
        older.UpdateDate = T0.AddHours(6);
        older.UpdatedBy = _other;

        var after = Keys(JobActivity.Snapshot([project.AsReadOnly()], _me.AsReadOnly(), ActivityScope.Mine));

        Assert.Equal(before, after);
        Assert.Equal(2, after[0].Id);
    }

    [Fact]
    public void ResubmissionMovesAJobToTheFrontOfTheNextSnapshot()
    {
        var project = CreateProject(1, owner: _me);
        var space = AddSpace(project, 0);
        var job = AddJob(space, 1, (EventType.Created, T0, _me));
        AddJob(space, 2, (EventType.Created, T0.AddMinutes(1), _me));

        job.Events.Add(new JobEvent(EventType.WaitingStarted, T0.AddMinutes(2), _me));

        var snapshot = JobActivity.Snapshot([project.AsReadOnly()], _me.AsReadOnly(), ActivityScope.Mine);
        Assert.Equal([1, 2], snapshot.Select(a => a.Job.Id));
    }

    [Fact]
    public void EqualTimestampsAreOrderedByProjectSpaceAndJobId()
    {
        // Job IDs repeat across spaces and projects; the tie-break must still be total
        var p1 = CreateProject(1, owner: _me);
        var p2 = CreateProject(2, owner: _me);
        var p1s0 = AddSpace(p1, 0);
        var p1s1 = AddSpace(p1, 1);
        var p2s0 = AddSpace(p2, 0);
        foreach (var space in new[] { p1s0, p1s1, p2s0 })
        {
            AddJob(space, 1, (EventType.Created, T0, _me));
            AddJob(space, 2, (EventType.Created, T0, _me));
        }

        var snapshot = JobActivity.Snapshot([p1.AsReadOnly(), p2.AsReadOnly()], _me.AsReadOnly(), ActivityScope.Mine);

        Assert.Equal([(2, 0, 2), (2, 0, 1), (1, 1, 2), (1, 1, 1), (1, 0, 2), (1, 0, 1)],
                     snapshot.Select(a => (a.ProjectId, a.SpaceId, a.Job.Id)));
        Assert.Equal(snapshot.Count, snapshot.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void JobsWithoutAuditEventsFallBackToTheirLastUpdate()
    {
        var project = CreateProject(1, owner: _me);
        var space = AddSpace(project, 0);
        var legacy = AddJob(space, 1);
        legacy.UpdateDate = T0;
        legacy.UpdatedBy = _me;

        var activity = JobActivity.Describe(legacy.AsReadOnly())!.Value;

        Assert.Equal(T0, activity.Timestamp);
        Assert.True(activity.IsInScope(ActivityScope.Mine, _me.AsReadOnly()));
    }

    [Fact]
    public void DetachedJobsHaveNoActivity()
    {
        Assert.Null(JobActivity.Describe(new ImportMap { Id = 1 }.AsReadOnly()));
    }

    private static List<SelectionKey> Keys(List<JobActivity> snapshot) => snapshot.Select(a => a.Key).ToList();

    internal static Project CreateProject(int id, User owner, User[]? members = null)
    {
        var project = new Project { Id = id, Owner = owner, Alias = $"Project {id}" };
        foreach (var member in members ?? [])
            project.AddMember(member);
        return project;
    }

    internal static Space AddSpace(Project project, int id)
    {
        var space = new Space { Id = id, Alias = $"Space {id}" };
        project.AddSpace(space);
        return space;
    }

    internal static Job AddJob(Space space, int id, params (EventType Type, DateTime Timestamp, User? Author)[] events)
    {
        var job = new ImportMap { Id = id };
        foreach (var (type, timestamp, author) in events)
            job.Events.Add(new JobEvent(type, timestamp, author));
        space.AddJob(job, null);
        return job;
    }
}
