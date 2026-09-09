using System.Collections.ObjectModel;
using Refund.DataModel;
using Refund.Jobs.Common.Notes.Note;

namespace Refund.Tests.DataModel;

[Collection("JobRegistry")]
public class JobStorageTests
{
    private static readonly ReadOnlyCollection<User> NoUsers = new List<User>().AsReadOnly();

    public JobStorageTests()
    {
        if (Job.Types.Count == 0)
            Job.PopulateStatic();
    }

    [Fact]
    public void MaterializedJob_AlwaysUsesNumericIdDirectory()
    {
        var space = new Space { RootDirectory = Path.Combine(Path.GetTempPath(), "relay-space") };
        var job = new Note { Space = space, Id = 42 };

        Assert.Equal("42", job.DirectoryName);
        Assert.Equal("42", job.DirectoryPathInSpace);
        Assert.Equal(Path.Combine(space.RootDirectory, "42"), job.DirectoryPath);
    }

    [Fact]
    public void Blueprint_HasNoFilesystemPaths()
    {
        var blueprint = Job.CreateBlueprint(typeof(Note));
        blueprint.Id = 1;

        Assert.True(blueprint.IsBlueprint);
        Assert.Null(blueprint.Space);
        Assert.Equal("", blueprint.DirectoryName);
        Assert.Throws<InvalidOperationException>(() => blueprint.DirectoryPath);
        Assert.Throws<InvalidOperationException>(() => blueprint.Clear());
    }

    [Fact]
    public void CreatingFromBlueprint_AssignsNewMaterializedIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "relay-space-" + Guid.NewGuid().ToString("N"));
        var space = new Space { RootDirectory = root };
        var view = space.CreateView(null);
        var blueprint = Job.CreateBlueprint(typeof(Note));
        blueprint.Id = 99;
        blueprint.Alias = "Template";

        var job = space.CreateJob(blueprint.TypeGuid, blueprint, view);

        Assert.False(job.IsBlueprint);
        Assert.Equal(1, job.Id);
        Assert.Equal("Template", job.Alias);
        Assert.Equal(Path.Combine(root, "1"), job.DirectoryPath);
    }

    [Fact]
    public void Clear_OnlyDeletesCanonicalJobDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "relay-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string rootMarker = Path.Combine(root, "keep.txt");
            string jobDirectory = Path.Combine(root, "7");
            File.WriteAllText(rootMarker, "keep");
            Directory.CreateDirectory(jobDirectory);
            File.WriteAllText(Path.Combine(jobDirectory, "delete.txt"), "delete");

            var job = new Note { Space = new Space { RootDirectory = root }, Id = 7 };
            job.Clear();

            Assert.True(File.Exists(rootMarker));
            Assert.True(Directory.Exists(jobDirectory));
            Assert.False(File.Exists(Path.Combine(jobDirectory, "delete.txt")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Deserialization_RejectsLegacyCustomDirectory()
    {
        var source = new Note { Space = new Space { RootDirectory = "/tmp/relay-space" }, Id = 7 };
        var json = Job.ToPolymorphicJson(source);
        json["Job"]!["DirectoryName"] = "custom";

        var exception = Assert.Throws<InvalidDataException>(() =>
            Job.CreateFromPolymorphicJson(
                json,
                new Space { RootDirectory = "/tmp/relay-space" },
                NoUsers));

        Assert.Contains("Expected '7'", exception.Message);
    }

    [Fact]
    public void Deserialization_MapsBlankLegacyDirectoryToNumericId()
    {
        var source = new Note { Space = new Space { RootDirectory = "/tmp/relay-space" }, Id = 7 };
        var json = Job.ToPolymorphicJson(source);
        json["Job"]!["DirectoryName"] = "";

        var restored = Job.CreateFromPolymorphicJson(
            json,
            new Space { RootDirectory = "/tmp/relay-space" },
            NoUsers);

        Assert.Equal("7", restored.DirectoryName);
        Assert.Equal(Path.Combine("/tmp/relay-space", "7"), restored.DirectoryPath);
    }

    [Fact]
    public void FactoryDefinition_DeserializesSubJobsAsBlueprints()
    {
        var blueprint = Job.CreateBlueprint(typeof(Note));
        blueprint.Id = 1;
        var definition = new FactoryDefinition { Id = 1 };
        definition.SubJobs.Add(blueprint);

        var restored = new FactoryDefinition();
        restored.ReadFromJson(
            definition.ToJson(),
            new Space { RootDirectory = "/tmp/relay-space" },
            NoUsers);

        var restoredBlueprint = Assert.Single(restored.SubJobs);
        Assert.True(restoredBlueprint.IsBlueprint);
        Assert.Null(restoredBlueprint.Space);
        Assert.Throws<InvalidOperationException>(() => restoredBlueprint.DirectoryPath);
    }
}
