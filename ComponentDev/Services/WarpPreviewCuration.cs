using Refund.Components.ThumbnailPanel;

namespace ComponentDev.Services;

public enum PreviewDisposition { Auto, Keep, Exclude }

public sealed partial class WarpSessionPreview
{
    public double MinCtf { get; private set; }
    public double MinMotion { get; private set; }
    public double MinIce { get; private set; }
    public double MaxIce { get; private set; } = 2000;
    public int MinParticles { get; private set; }
    public int MaxParticles { get; private set; } = int.MaxValue;
    public double MaxAstigmatism { get; private set; } = 1;
    public static readonly string[] StatusNames = ["Unprocessed", "Partial", "Ready", "Outdated", "Filtered", "Kept", "Excluded", "Failed"];

    public bool Passes(PreviewMovie m) => !m.Failed && !m.Excluded && m.Disposition != PreviewDisposition.Exclude &&
        (m.Disposition == PreviewDisposition.Keep || PassesFilters(m));
    public bool PassesFilters(PreviewMovie m) =>
        (!m.Done.HasFlag(PreviewStage.Ctf) || m.Ctf >= MinCtf && m.Ctf <= MaxCtf && m.Ice >= MinIce && m.Ice <= MaxIce && m.Astigmatism <= MaxAstigmatism) &&
        (!m.Done.HasFlag(PreviewStage.Motion) || m.Motion >= MinMotion && m.Motion <= MaxMotion) &&
        (MinParticles == 0 && MaxParticles == int.MaxValue || m.Done.HasFlag(PreviewStage.Pick) && m.Picks >= MinParticles && m.Picks <= MaxParticles);

    public string Status(PreviewMovie m) => m.Failed ? "Failed" : m.Excluded || m.Disposition == PreviewDisposition.Exclude ? "Excluded" :
        m.Done == PreviewStage.None ? "Unprocessed" : ActiveStale(m) != 0 ? "Outdated" : m.Disposition == PreviewDisposition.Keep ? "Kept" :
        !PassesFilters(m) ? "Filtered" : (m.Done & RequiredStages) == RequiredStages ? "Ready" : "Partial";
    public PreviewStage ActiveStale(PreviewMovie m) => m.Stale & RequiredStages;
    private PreviewStage RequiredStages => Settings.PickingEnabled ? PreviewStage.All : PreviewStage.Motion | PreviewStage.Ctf | PreviewStage.Enhance;
    public ProcessingStatus ThumbnailStatus(PreviewMovie m) => Status(m) switch {
        "Failed" => ProcessingStatus.Failed, "Kept" => ProcessingStatus.Kept, "Excluded" => ProcessingStatus.Deselected,
        "Outdated" => ProcessingStatus.Outdated, "Filtered" => ProcessingStatus.FilteredOut,
        "Ready" => ProcessingStatus.Processed, "Partial" => ProcessingStatus.Partial, _ => ProcessingStatus.Unprocessed
    };
    public string StatusColor(PreviewMovie m) => StatusColorFor(Status(m));
    public static string StatusColorFor(string status) => StatusColorMapping.GetColor(status switch {
        "Failed" => ProcessingStatus.Failed, "Kept" => ProcessingStatus.Kept, "Excluded" => ProcessingStatus.Deselected,
        "Outdated" => ProcessingStatus.Outdated, "Filtered" => ProcessingStatus.FilteredOut,
        "Ready" => ProcessingStatus.Processed, "Partial" => ProcessingStatus.Partial, _ => ProcessingStatus.Unprocessed
    });
    public void FilterMetric(string metric, double value) => Change(() => {
        if (!double.IsFinite(value)) return;
        switch (metric) {
            case "ctf-min": MinCtf = Math.Clamp(value, 0, MaxCtf); break;
            case "ctf":
            case "ctf-max": MaxCtf = Math.Clamp(value, Math.Max(1, MinCtf), 100); break;
            case "motion-min": MinMotion = Math.Clamp(value, 0, MaxMotion); break;
            case "motion":
            case "motion-max": MaxMotion = Math.Clamp(value, MinMotion, 100); break;
            case "ice-min": MinIce = Math.Clamp(value, 0, MaxIce); break;
            case "ice":
            case "ice-max": MaxIce = Math.Clamp(value, MinIce, 10000); break;
            case "particles":
            case "particles-min": MinParticles = (int)Math.Clamp(value, 0, MaxParticles); break;
            case "particles-max": MaxParticles = (int)Math.Clamp(value, MinParticles, int.MaxValue); break;
            case "astigmatism": MaxAstigmatism = Math.Clamp(value, 0, 10); break;
        }
        Recolor();
    });
    public void SetDisposition(IEnumerable<int> indices, PreviewDisposition disposition) => Change(() => {
        int count = 0;
        foreach (int index in indices.Distinct()) {
            if (index < 0 || index >= Movies.Count) continue;
            Movies[index].Disposition = disposition;
            Movies[index].Excluded = disposition == PreviewDisposition.Exclude;
            count++;
        }
        Recolor();
        Notice = disposition switch { PreviewDisposition.Keep => $"{count:N0} movies kept regardless of filters", PreviewDisposition.Exclude => $"{count:N0} movies excluded", _ => $"Filter rules restored for {count:N0} movies" };
    });
    public void Retry(IEnumerable<int> indices) => Change(() => {
        int count = 0;
        foreach (int index in indices.Distinct()) {
            if (index < 0 || index >= Movies.Count || !Movies[index].Failed) continue;
            Movies[index].Failed = false;
            Movies[index].Stale |= Movies[index].Done;
            count++;
        }
        Recolor(); Notice = $"{count:N0} failed movies queued for retry";
    });
    public string DenoiserStatus => !Settings.Denoise ? "Off" : Training ? $"Training · {TrainingPercent}%" : Model == 0 ?
        $"Waiting for {Settings.TrainCount} micrographs · {StageCounts[1]} ready" : $"Model v{Model}";
    public int AwaitingDenoiser => Settings.Denoise && Model == 0 ? Movies.Count(m => m.Done.HasFlag(PreviewStage.Ctf) && !m.Done.HasFlag(PreviewStage.Enhance) && !m.Failed) : 0;
}
