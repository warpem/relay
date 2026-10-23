using System.Reflection;
using System.Text.Json.Nodes;
using Refund.DataModel;
using Refund.JobResources;
using Refund.Jobs.Ts.Ctf;
using Refund.Jobs.Ts.Import.ImportDataSetTs;
using Refund.UIFields;

namespace Refund.Tests.Jobs;

[Collection("JobRegistry")]
public class TiltSeriesImportParametersTests
{
    public TiltSeriesImportParametersTests() => JobRegistry.EnsurePopulated();

    [Fact]
    public void DefaultImport_KeepsOriginalTiltAngles()
    {
        var job = CreateImport();
        var arguments = job.ComposeCommandArguments();

        Assert.Equal(TiltZeroMode.ManualOffset, job.ZeroTiltMode);
        Assert.Null(job.TiltOffset);
        Assert.False(arguments.ContainsKey("tilt_offset"));
        Assert.False(arguments.ContainsKey("auto_zero"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3.5)]
    public void SwitchingZeroMode_HidesAndOmitsManualOffsetWithoutLosingIt(double offset)
    {
        var job = CreateImport();
        job.TiltOffset = (decimal)offset;
        var offsetProperty = typeof(ImportDataSetTs).GetProperty(nameof(ImportDataSetTs.TiltOffset))!;
        Assert.IsType<UiDecimalNullable>(offsetProperty.GetCustomAttribute<UiFieldBase>());

        job.ZeroTiltMode = TiltZeroMode.AutoZero;
        Assert.False(Job.IsPropertyVisible(typeof(ImportDataSetTs), offsetProperty, job.AsReadOnly()));
        var automatic = job.ComposeCommandArguments();
        Assert.True(automatic.ContainsKey("auto_zero"));
        Assert.False(automatic.ContainsKey("tilt_offset"));

        job.ZeroTiltMode = TiltZeroMode.ManualOffset;
        Assert.True(Job.IsPropertyVisible(typeof(ImportDataSetTs), offsetProperty, job.AsReadOnly()));
        var manual = job.ComposeCommandArguments();
        Assert.Equal((decimal)offset, job.TiltOffset);
        Assert.Equal(job.TiltOffset.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), manual["tilt_offset"]);
        Assert.False(manual.ContainsKey("auto_zero"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroMode_UsesExistingSavedSettingAndSurvivesRoundTrip(bool automatic)
    {
        var job = CreateImport();
        job.ReadFromJson(new JsonObject { ["AutoZeroTilt"] = automatic, ["TiltOffset"] = 2.5 });
        Assert.Equal(automatic ? TiltZeroMode.AutoZero : TiltZeroMode.ManualOffset, job.ZeroTiltMode);

        job.ZeroTiltMode = automatic ? TiltZeroMode.ManualOffset : TiltZeroMode.AutoZero;
        var restored = CreateImport();
        restored.ReadFromJson(job.ToJson());
        Assert.Equal(job.ZeroTiltMode, restored.ZeroTiltMode);
        Assert.Equal(job.TiltOffset, restored.TiltOffset);
        Assert.Equal(!automatic, restored.ComposeCommandArguments().ContainsKey("auto_zero"));
        Assert.Equal(automatic, restored.ComposeCommandArguments().ContainsKey("tilt_offset"));
    }

    [Fact]
    public void ThicknessFit_OnlyEmitsFlagWhenEnabled()
    {
        var job = new Ctf { Id = 2, Space = new Space { RootDirectory = "/tmp/relay-parameter-tests" } };
        Assert.False(job.ComposeCommandArguments().ContainsKey("fit_thickness"));
        job.ThicknessEnable = true;
        Assert.Equal("", job.ComposeCommandArguments()["fit_thickness"]);
        job.ThicknessEnable = false;
        Assert.False(job.ComposeCommandArguments().ContainsKey("fit_thickness"));
    }

    private static ImportDataSetTs CreateImport()
    {
        var job = new ImportDataSetTs
        {
            Id = 1,
            Space = new Space { RootDirectory = "/tmp/relay-parameter-tests" },
            MdocDirectory = "mdocs"
        };
        var resource = new MicrographSet { DataSetFs = new DataSetFs { OverallExposure = 40 } };
        var input = job.PortsIn[ImportDataSetTs.PortInMicrographs];
        input.Edges.Add(new Edge
        {
            Source = new PortOut(job, typeof(MicrographSet), "source", "source", _ => resource),
            Target = input
        });
        return job;
    }
}
