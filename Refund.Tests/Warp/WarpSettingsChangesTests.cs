using Refund.Services.Warp;

namespace Refund.Tests.Warp;

public sealed class WarpSettingsChangesTests
{
    [Fact]
    public void ReportsOnlyEditedSettings()
    {
        var applied = new WarpSettings { OutputPath = "/session/processing" };
        var draft = applied with { TotalDose = 50, Denoise = false };
        Assert.Equal(["Denoise", "TotalDose"], WarpSession.ChangedSettings(draft, applied).Order());
    }

    [Fact]
    public void IgnoresValuesTheSessionDerivesItself()
    {
        var applied = new WarpSettings { OutputPath = "/session/processing", TrainAfter = 100 };
        var draft = applied with { OutputPath = "", TrainAfter = 4, BinTimes = 1 };
        Assert.Empty(WarpSession.ChangedSettings(draft, applied));
    }

    [Fact]
    public void EqualDecimalsWithDifferentScaleAreUnchanged()
    {
        var applied = new WarpSettings { CtfMax = 4 };
        Assert.Empty(WarpSession.ChangedSettings(applied with { CtfMax = 4.0m }, applied));
    }
}
