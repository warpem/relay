using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.Services.Core.Repositories;

namespace Refund.Tests.Services;

public sealed class SpaceStartupTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("null")]
    [InlineData("invalid-id")]
    public void LoadingFailsByDefaultAndRecoveryDropsOnlyUnloadableSpaces(string failure)
    {
        string directory = Path.Combine(Path.GetTempPath(), "relay-startup-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var user = new User { Id = 1 };
            var users = new List<User> { user }.AsReadOnly();
            string projectsPath = Path.Combine(directory, "projects.relay");
            string failedPath = Path.Combine(directory, "failed.relay");
            string goodPath = Path.Combine(directory, "space.relay");
            var goodSpace = new Space { Id = 1, RootDirectory = directory };
            File.WriteAllText(goodPath, goodSpace.ToJson().ToJsonString());
            if (failure != "missing")
            {
                var invalidSpace = new Space { Id = -1, RootDirectory = directory };
                File.WriteAllText(failedPath, failure switch
                {
                    "corrupt" => "{invalid",
                    "null" => "null",
                    _ => invalidSpace.ToJson().ToJsonString()
                });
            }

            var project = new Project { Id = 1, Alias = "Test project", Owner = user }.ToJson();
            project["Spaces"] = new JsonArray(failedPath, goodPath);
            var otherProject = new Project { Id = 2, Owner = user }.ToJson();
            otherProject["Spaces"] = new JsonArray(goodPath);
            string original = new JsonObject { ["Projects"] = new JsonArray(project, otherProject) }.ToJsonString();
            File.WriteAllText(projectsPath, original);

            using (var repository = new DataRepository(projectsPath))
            {
                var error = Assert.Throws<InvalidOperationException>(() => repository.LoadAll(users));
                Assert.Contains(failedPath, error.Message);
                Assert.Contains("Test project", error.Message);
                Assert.Contains("--drop-unloadable-spaces", error.Message);
                Assert.NotNull(error.InnerException);
                Assert.Equal(new[] { failedPath, goodPath }, repository.Projects[0].ToJson()["Spaces"]!
                    .AsArray().Select(path => path!.GetValue<string>()));
            }
            Assert.Equal(original, File.ReadAllText(projectsPath));

            using (var repository = new DataRepository(projectsPath))
            {
                repository.LoadAll(users, dropUnloadableSpaces: true);
                Assert.Equal(2, repository.Projects.Count);
                foreach (var loadedProject in repository.Projects)
                {
                    Assert.Equal(1, Assert.Single(loadedProject.Spaces).Id);
                    Assert.Equal(goodPath, Assert.Single(loadedProject.ToJson()["Spaces"]!.AsArray())!.GetValue<string>());
                }
                repository.UpdateProject(user, repository.Projects[0], p => p.Alias = "Edited");
            }
            var saved = JsonNode.Parse(File.ReadAllText(projectsPath))!;
            Assert.Equal(goodPath, Assert.Single(saved["Projects"]![0]!["Spaces"]!.AsArray())!.GetValue<string>());
            Assert.True(failure == "missing" || File.Exists(failedPath));

            using var reloaded = new DataRepository(projectsPath);
            reloaded.LoadAll(users);
            Assert.All(reloaded.Projects, p => Assert.Single(p.Spaces));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
