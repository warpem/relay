using Warp;
using Warp.Workers;
using Warp.Workers.Queue;

namespace Refund.Tests.Warp;

public class WorkerCommandContractTests
{
    [Fact]
    public void TrainingAndEnhancementCommandsSurviveDiskQueueRoundTrip()
    {
        var task = new TaskItem
        {
            Main =
            [
                WorkerCommands.MoviePrepareDenoising("a folder/movie.eer", 1.35f, .85f, .25f),
                WorkerCommands.TrainMovieDenoiser(["first.eer", "second.eer"], "models/2.pt", "models/1.pt", 8, .9f, .1f),
                WorkerCommands.MovieDenoise("first.eer", "models/2.pt"),
                WorkerCommands.MicrographPickBlobs("average/denoised/first.mrc", 12, minimumSeparation: 19.2f),
                WorkerCommands.MovieExportParticlesFromStar("first.eer",
                    new ProcessingOptionsParticleExport { BoxSize = 256, PixelSize = 1.35m },
                    "average/denoised/matching/first_blob.star", 5)
            ]
        };
        var decoded = TaskItem.FromJson(task.ToJson());
        Assert.Equal(task.Main.Select(c => c.Name), decoded.Main.Select(c => c.Name));
        Assert.Equal("a folder/movie.eer", decoded.Main[0].Content[0]);
        Assert.Equal(1.35f, decoded.Main[0].Content[1]);
        Assert.Equal(.85f, decoded.Main[0].Content[2]);
        Assert.Equal(.25f, decoded.Main[0].Content[3]);
        Assert.Equal(new[] { "first.eer", "second.eer" }, Assert.IsType<string[]>(decoded.Main[1].Content[0]));
        Assert.Equal(8, decoded.Main[1].Content[3]);
        Assert.Equal(.9f, decoded.Main[1].Content[4]);
        Assert.Equal(.1f, decoded.Main[1].Content[5]);
        Assert.Equal(19.2f, decoded.Main[3].Content[3]);
        var export = Assert.IsType<ProcessingOptionsParticleExport>(decoded.Main[4].Content[1]);
        Assert.Equal(256, export.BoxSize);
        Assert.Equal(1.35m, export.PixelSize);
        Assert.Equal(5f, decoded.Main[4].Content[3]);
    }

    [Fact]
    public void InitialTrainingDoesNotSerializeNullCommandArguments()
    {
        var task = new TaskItem { Main = [WorkerCommands.TrainMovieDenoiser(["a.eer"], "first.pt", null!, 8, .9f, .1f)] };
        Assert.Equal("", TaskItem.FromJson(task.ToJson()).Main[0].Content[2]);
    }
}
