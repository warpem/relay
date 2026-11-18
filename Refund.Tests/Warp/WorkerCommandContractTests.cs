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
                WorkerCommands.MovieDenoise("a folder/movie.eer", "models/2.pt", 1.35f, .85f, .25f),
                WorkerCommands.TrainMovieDenoiser(["first.eer", "second.eer"], "models/2.pt", "models/1.pt", 8, .9f, .1f, 1.35f),
                WorkerCommands.MicrographPickBlobs("average/denoised/first.mrc", 12, minimumSeparation: 19.2f),
                WorkerCommands.MovieExportParticlesFromStar("first.eer",
                    new ProcessingOptionsParticleExport { BoxSize = 256, PixelSize = 1.35m },
                    "average/denoised/matching/first_blob.star", 5, .3, .9, .1, double.MaxValue, "particles/first_warp.star")
            ]
        };
        var decoded = TaskItem.FromJson(task.ToJson());
        Assert.Equal(task.Main.Select(c => c.Name), decoded.Main.Select(c => c.Name));
        Assert.Equal("a folder/movie.eer", decoded.Main[0].Content[0]);
        Assert.Equal("models/2.pt", decoded.Main[0].Content[1]);
        Assert.Equal(1.35f, decoded.Main[0].Content[2]);
        Assert.Equal(.85f, decoded.Main[0].Content[3]);
        Assert.Equal(.25f, decoded.Main[0].Content[4]);
        Assert.Equal(new[] { "first.eer", "second.eer" }, Assert.IsType<string[]>(decoded.Main[1].Content[0]));
        Assert.Equal(8, decoded.Main[1].Content[3]);
        Assert.Equal(.9f, decoded.Main[1].Content[4]);
        Assert.Equal(.1f, decoded.Main[1].Content[5]);
        Assert.Equal(1.35f, decoded.Main[1].Content[6]);
        Assert.Equal(19.2f, decoded.Main[2].Content[3]);
        var export = Assert.IsType<ProcessingOptionsParticleExport>(decoded.Main[3].Content[1]);
        Assert.Equal(256, export.BoxSize);
        Assert.Equal(1.35m, export.PixelSize);
        Assert.Equal(5f, decoded.Main[3].Content[3]);
        Assert.Equal(new object[] { .3, .9, .1, double.MaxValue, "particles/first_warp.star" }, decoded.Main[3].Content[4..]);
    }

    [Fact]
    public void JunkArgumentsAreAlwaysSentAndEmptyWhenThereIsNoJunk()
    {
        var task = new TaskItem
        {
            Main =
            [
                WorkerCommands.MicrographCreatePreview("average/movie.mrc", "average/previews/movie.png", 1024),
                WorkerCommands.MicrographCreatePreview("average/movie.mrc", "average/previews/movie.png", 1024, "mask/movie.tif"),
                WorkerCommands.MicrographPickBlobs("average/denoised/movie.mrc", 12),
                WorkerCommands.MovieSegmentJunk("movie.eer", 1.2f),
                WorkerCommands.MovieDenoise("movie.eer", "models/2.pt", 1.35f, .9f, .1f, eraseObjects: false),
                WorkerCommands.TrainMovieDenoiser(["movie.eer"], "models/2.pt", "", 4, .9f, .1f, 1.35f, eraseObjects: false),
                WorkerCommands.MovieDenoise("movie.eer", "models/2.pt", 1.35f, .9f, .1f),
                WorkerCommands.TrainMovieDenoiser(["movie.eer"], "models/2.pt", "", 4, .9f, .1f, 1.35f)
            ]
        };
        var decoded = TaskItem.FromJson(task.ToJson());
        Assert.Equal(new object[] { "average/movie.mrc", "average/previews/movie.png", 1024, "" }, decoded.Main[0].Content);
        Assert.Equal("mask/movie.tif", decoded.Main[1].Content[3]);
        Assert.Equal(new object[] { "", 0f }, decoded.Main[2].Content[4..]);
        Assert.Equal("", decoded.Main[3].Content[2]);
        Assert.Equal(false, decoded.Main[4].Content[5]);
        Assert.Equal(false, decoded.Main[5].Content[7]);
        Assert.Equal(true, decoded.Main[6].Content[5]);
        Assert.Equal(true, decoded.Main[7].Content[7]);
    }

    [Fact]
    public void InitialTrainingDoesNotSerializeNullCommandArguments()
    {
        var task = new TaskItem { Main = [WorkerCommands.TrainMovieDenoiser(["a.eer"], "first.pt", null!, 8, .9f, .1f, 1)] };
        Assert.Equal("", TaskItem.FromJson(task.ToJson()).Main[0].Content[2]);
    }
}
