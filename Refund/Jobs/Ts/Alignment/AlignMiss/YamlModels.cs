using VYaml.Annotations;

namespace Refund.Jobs.Ts.Alignment.AlignMiss;

[YamlObject(NamingConvention.SnakeCase)]
public partial class MissAlignmentConfig
{
    public MissGeneralConfig General { get; set; }
    public MissModelTrainingConfig ModelTraining { get; set; }
    public MissDataLoadingConfig DataLoading { get; set; }
    public MissShiftGenerationConfig ShiftGeneration { get; set; }
    public MissTiltSeriesAlignmentConfig TiltSeriesAlignment { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public partial class MissGeneralConfig
{
    public string TrainingDirectory { get; set; }
    public bool ApplyCtf { get; set; }
    public List<MissIterationSetting> IterationSettings { get; set; }
    public int Seed { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public partial class MissIterationSetting
{
    public int Downsample { get; set; }
    public object Alignment { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public partial class MissModelTrainingConfig
{
    public string ModelArchitecture { get; set; }
    public string ModelCheckpoint { get; set; }
    public double LossMargin { get; set; }
    public double LearningRate { get; set; }
    public double WeightDecay { get; set; }
    public int MaxEpochsPerIteration { get; set; }
    public int WarmupSteps { get; set; }
    public MissLrSchedulerConfig MultistepLrScheduler { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public partial class MissLrSchedulerConfig
{
    public int[] Milestones { get; set; }
    public double Gamma { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public partial class MissDataLoadingConfig
{
    public int BatchSize { get; set; }
    public int PatchSize { get; set; }
    public int StepsPerEpoch { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public partial class MissShiftGenerationConfig
{
    public double TrajectoryProbability { get; set; }
    public double TrajectoryMaxShift { get; set; }
    public double JitterProbability { get; set; }
    public double JitterMaxStd { get; set; }
    public double OutlierProbability { get; set; }
    public double OutlierMaxShift { get; set; }
    public double FractureProbability { get; set; }
    public double FractureMaxShift { get; set; }
}

[YamlObject(NamingConvention.SnakeCase)]
public partial class MissTiltSeriesAlignmentConfig
{
    public int PatchSize { get; set; }
    public double PatchOverlap { get; set; }
    public int BatchSize { get; set; }
}