using Refund.Services.Warp;

namespace Refund.Jobs.Fs.Warp;

/// <summary>The session figures the Warp view's header and the job card both show, worded the same way, from its summary.</summary>
public static class WarpSessionText
{
    /// <summary>How long ago, e.g. "3 min ago"; "waiting" before the first event.</summary>
    public static string Age(DateTime? timestamp)
    {
        if (!timestamp.HasValue) return "waiting";
        var age = DateTime.UtcNow - timestamp.Value;
        return age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes} min ago" : $"{Math.Max(0, (int)age.TotalSeconds)} s ago";
    }

    // Run state lives elsewhere (the run label, the card's status icon); this reports only the backlog, keeping
    // "caught up" for automation.
    public static string Queue(WarpSessionSummary session) =>
        !session.Running ? $"{session.Pending:N0} waiting" + (session.Outdated > 0 ? $", {session.Outdated:N0} outdated" : "")
        : session.Pending == 0 && session.Outdated == 0 ? "caught up" : $"{session.Pending + session.Outdated:N0} waiting";

    public static string Denoiser(WarpSessionSummary session) =>
        !session.Denoise ? "Off"
        : session.Training ? $"Training {session.TrainingPercent}%"
        : session.HasModel ? $"Model v{session.Model}"
        : $"Collecting {Math.Min(session.TrainingMoviesReady, session.TrainAfter):N0}/{session.TrainAfter:N0}";

    /// <summary>Movies processed per hour, e.g. "1,290 / h"; null without an estimate.</summary>
    public static string Rate(WarpSessionSummary session) => session.ProcessingRate is { } rate ? $"{rate:N0} / h" : null;
}
