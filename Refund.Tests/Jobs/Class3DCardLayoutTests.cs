using Refund.DataModel;
using Refund.Jobs.Refinement.Classes3D.BeamClass3D;
using Refund.Jobs.Refinement.Classes3D.Class3D;
using Refund.Jobs.Refinement.Classes3D.Class3DSelect;
using Refund.Jobs.Refinement.InitialModel.BeamInit3D;
using Refund.Jobs.Refinement.InitialModel.InitialReference3D;
using Refund.Jobs.M.Refine;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class Class3DCardLayoutTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(5, 5)]
    [InlineData(6, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 3)]
    [InlineData(12, 3)]
    [InlineData(13, 4)]
    [InlineData(16, 4)]
    [InlineData(17, 5)]
    [InlineData(20, 5)]
    [InlineData(21, 5)]
    [InlineData(1000, 5)]
    public void MultiVolumeCardsMatchRendererAspectRatio(int count, int width)
    {
        JobRegistry.EnsurePopulated();
        Job[] jobs = [
            new BeamClass3D { ClassCount = count },
            new BeamInit3D { NClasses = count },
            new Class3D { NClasses = count },
            new InitialReference { NClasses = count },
            new Class3DSelect { SelectedClasses = Enumerable.Range(1, count).ToArray() },
            new Refine { SpeciesCount = count }
        ];
        foreach (var job in jobs)
        {
            Assert.Equal(width, job.CardSquareCount.X);
            Assert.Equal(1, job.CardSquareCount.Y);
        }
    }
}
