using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services;
using static Refund.Tests.Services.JobActivityTests;

namespace Refund.Tests.Services;

[Collection("JobRegistry")]
public class ScopedSelectionKeyTests
{
    private readonly User _user = new() { Id = 1, Username = "me" };

    public ScopedSelectionKeyTests() => JobRegistry.EnsurePopulated();

    [Fact]
    public void SameJobIdInDifferentSpacesGivesDistinctScopedKeys()
    {
        var p1 = CreateProject(1, _user);
        var p2 = CreateProject(2, _user);
        var a = AddJob(AddSpace(p1, 0), 7).AsReadOnly();
        var b = AddJob(AddSpace(p1, 1), 7).AsReadOnly();
        var c = AddJob(AddSpace(p2, 0), 7).AsReadOnly();

        var keys = new[] { SelectionKey.ForJob(a), SelectionKey.ForJob(b), SelectionKey.ForJob(c) };

        Assert.All(keys, k => Assert.True(k.IsScoped));
        Assert.Equal(3, keys.Distinct().Count());
        Assert.Equal(new SelectionKey(ItemType.Job, 7, 1, 1), keys[1]);
    }

    [Fact]
    public void LegacyKeysStayUnscopedAndDistinctFromScopedOnes()
    {
        var job = AddJob(AddSpace(CreateProject(3, _user), 4), 7).AsReadOnly();

        Assert.Equal(new SelectionKey(ItemType.Job, 7), SelectionKey.ForJob(7));
        Assert.False(SelectionKey.ForJob(7).IsScoped);
        Assert.NotEqual(SelectionKey.ForJob(7), SelectionKey.ForJob(job));
        Assert.NotEqual(SelectionKey.ForFolder(7), SelectionKey.ForJob(7));
    }

    [Fact]
    public void ResolvingScopedKeysFindsJobsAcrossSpacesInSelectionOrder()
    {
        var p1 = CreateProject(1, _user);
        var p2 = CreateProject(2, _user);
        var a = AddJob(AddSpace(p1, 0), 7).AsReadOnly();
        var c = AddJob(AddSpace(p2, 0), 7).AsReadOnly();
        var projects = new[] { p1.AsReadOnly(), p2.AsReadOnly() };

        var resolved = CardSelectionService.ResolveJobs(
            [SelectionKey.ForJob(c), SelectionKey.ForProject(1), SelectionKey.ForJob(a), SelectionKey.ForJob(c),
             SelectionKey.ForJob(1, 0, 99), SelectionKey.ForJob(9, 0, 7)],
            key => Find(projects, key),
            contextSpace: null);

        Assert.Equal([c, a], resolved);
    }

    [Fact]
    public void LegacyKeysResolveOnlyAgainstTheContextSpace()
    {
        var project = CreateProject(1, _user);
        var s0 = AddSpace(project, 0);
        var s1 = AddSpace(project, 1);
        var inS0 = AddJob(s0, 7).AsReadOnly();
        AddJob(s1, 7);

        Assert.Equal([inS0], CardSelectionService.ResolveJobs([SelectionKey.ForJob(7)], _ => null, s0.AsReadOnly()));
        Assert.Empty(CardSelectionService.ResolveJobs([SelectionKey.ForJob(7)], _ => null, contextSpace: null));
    }

    private static ReadOnlyJob? Find(IEnumerable<ReadOnlyProject> projects, SelectionKey key) =>
        projects.FirstOrDefault(p => p.Id == key.ProjectId)?
                .Spaces.FirstOrDefault(s => s.Id == key.SpaceId)?
                .FindJob(key.Id);
}
