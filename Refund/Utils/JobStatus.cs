using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Utils;

/// <summary>
/// Provides extension methods for job status visualization, including icons and colors.
/// </summary>
public static class JobStatusExtensions
{
    /// <summary>
    /// Gets an appropriate icon representing the job's current status.
    /// </summary>
    /// <param name="job">The job whose status should be represented.</param>
    /// <param name="size">The desired icon size (16px, 20px, or 24px).</param>
    /// <returns>A Fluent UI icon representing the job's status.</returns>
    /// <remarks>
    /// Icons are selected based on both the job status and whether the job is resumable.
    /// For example, aborted jobs will show a pause icon if they can be resumed, or a stop icon if not.
    /// </remarks>
    public static Icon GetStatusIcon(this ReadOnlyJob job, IconSize size = IconSize.Size16)
    {
        return size switch
        {
            IconSize.Size16 =>
                job.Status switch
                {
                    JobStatus.Building => new Icons.Filled.Size12.Edit(),
                    JobStatus.Waiting => new Icons.Filled.Size16.Clock(),
                    JobStatus.Running => new Icons.Filled.Size16.Rocket(),
                    JobStatus.Staging => new Icons.Filled.Size16.ArrowExportUp(),
                    JobStatus.Finalizing => new Icons.Filled.Size16.CheckmarkCircle(),
                    JobStatus.Finished => new Icons.Filled.Size16.CheckmarkCircle(),
                    JobStatus.Aborting => job.IsResumable ?
                                              new Icons.Regular.Size16.HandRight() :
                                              new Icons.Regular.Size16.HandRight(),
                    JobStatus.Aborted => job.IsResumable ?
                                             new Icons.Regular.Size16.PauseCircle() :
                                             new Icons.Filled.Size16.RecordStop(),
                    JobStatus.Failed => new Icons.Filled.Size16.ErrorCircle(),
                    JobStatus.Deleted => new Icons.Filled.Size16.Delete(),
                    JobStatus.Clearing => new Icons.Filled.Size16.Broom(),
                    _ => new Icons.Filled.Size16.QuestionCircle()
                },

            IconSize.Size20 =>
                job.Status switch
                {
                    JobStatus.Building => new Icons.Filled.Size16.Edit(),
                    JobStatus.Waiting => new Icons.Filled.Size20.Clock(),
                    JobStatus.Running => new Icons.Filled.Size20.Rocket(),
                    JobStatus.Staging => new Icons.Filled.Size20.ArrowExportUp(),
                    JobStatus.Finalizing => new Icons.Filled.Size20.CheckmarkCircle(),
                    JobStatus.Finished => new Icons.Filled.Size20.CheckmarkCircle(),
                    JobStatus.Aborting => job.IsResumable ?
                                              new Icons.Regular.Size20.HandRight() :
                                              new Icons.Regular.Size20.HandRight(),
                    JobStatus.Aborted => job.IsResumable ?
                                             new Icons.Regular.Size20.PauseCircle() :
                                             new Icons.Filled.Size20.RecordStop(),
                    JobStatus.Failed => new Icons.Filled.Size20.ErrorCircle(),
                    JobStatus.Deleted => new Icons.Filled.Size20.Delete(),
                    JobStatus.Clearing => new Icons.Filled.Size20.Broom(),
                    _ => new Icons.Filled.Size20.QuestionCircle()
                },

            IconSize.Size24 =>
                job.Status switch
                {
                    JobStatus.Building => new Icons.Filled.Size20.Edit(),
                    JobStatus.Waiting => new Icons.Filled.Size24.Clock(),
                    JobStatus.Running => new Icons.Filled.Size24.Rocket(),
                    JobStatus.Staging => new Icons.Filled.Size24.ArrowExportUp(),
                    JobStatus.Finalizing => new Icons.Filled.Size24.CheckmarkCircle(),
                    JobStatus.Finished => new Icons.Filled.Size24.CheckmarkCircle(),
                    JobStatus.Aborting => job.IsResumable ?
                                              new Icons.Regular.Size24.HandRight() :
                                              new Icons.Regular.Size24.HandRight(),
                    JobStatus.Aborted => job.IsResumable ?
                                             new Icons.Regular.Size24.PauseCircle() :
                                             new Icons.Filled.Size24.RecordStop(),
                    JobStatus.Failed => new Icons.Filled.Size24.ErrorCircle(),
                    JobStatus.Deleted => new Icons.Filled.Size24.Delete(),
                    JobStatus.Clearing => new Icons.Filled.Size24.Broom(),
                    _ => new Icons.Filled.Size24.QuestionCircle()
                }
        };
    }

    /// <summary>
    /// Applies an appropriate color to an icon based on the job's status.
    /// </summary>
    /// <param name="icon">The icon to apply the color to.</param>
    /// <param name="job">The job whose status determines the color.</param>
    /// <returns>The icon with the appropriate status color applied.</returns>
    /// <remarks>
    /// Colors are semantically mapped to job statuses:
    /// - Green: Successful completion (Finished)
    /// - Blue: In-progress (Running, Finalizing)
    /// - Gray: Waiting (Waiting, Staging)
    /// - Red: Problems (Failed, Aborted)
    /// - Orange: Transitional states (Aborting, Clearing)
    /// - Default UI color: Building
    /// </remarks>
    public static Icon WithStatusColor(this Icon icon, ReadOnlyJob job)
    {
        return job.Status switch
        {
            JobStatus.Building => icon.WithColor("var(--neutral-foreground-rest)"), // Black/White
            JobStatus.Waiting => icon.WithColor("#aaa"),            // Gray
            JobStatus.Staging => icon.WithColor("#aaa"),            // Gray
            JobStatus.Running => icon.WithColor("#1b8ce3"),         // Blue
            JobStatus.Finalizing => icon.WithColor("#1b8ce3"),      // Blue
            JobStatus.Finished => icon.WithColor("#13a10e"),        // Green
            JobStatus.Aborting => icon.WithColor("#e9835e"),        // Orange
            JobStatus.Aborted => icon.WithColor("#d13438"),         // Red
            JobStatus.Failed => icon.WithColor("#d13438"),          // Red
            JobStatus.Clearing => icon.WithColor("#e9835e"),        // Orange
            _ => icon
        };
    }

    /// <summary>
    /// Gets an appropriate icon for a JobStatus value (without a job instance).
    /// Uses non-resumable variants for Aborting/Aborted.
    /// </summary>
    public static Icon GetStatusIcon(JobStatus status, IconSize size = IconSize.Size16)
    {
        return size switch
        {
            IconSize.Size16 =>
                status switch
                {
                    JobStatus.Building => new Icons.Filled.Size12.Edit(),
                    JobStatus.Waiting => new Icons.Filled.Size16.Clock(),
                    JobStatus.Running => new Icons.Filled.Size16.Rocket(),
                    JobStatus.Staging => new Icons.Filled.Size16.ArrowExportUp(),
                    JobStatus.Finalizing => new Icons.Filled.Size16.CheckmarkCircle(),
                    JobStatus.Finished => new Icons.Filled.Size16.CheckmarkCircle(),
                    JobStatus.Aborting => new Icons.Regular.Size16.HandRight(),
                    JobStatus.Aborted => new Icons.Filled.Size16.RecordStop(),
                    JobStatus.Failed => new Icons.Filled.Size16.ErrorCircle(),
                    JobStatus.Deleted => new Icons.Filled.Size16.Delete(),
                    JobStatus.Clearing => new Icons.Filled.Size16.Broom(),
                    _ => new Icons.Filled.Size16.QuestionCircle()
                },
            IconSize.Size20 =>
                status switch
                {
                    JobStatus.Building => new Icons.Filled.Size16.Edit(),
                    JobStatus.Waiting => new Icons.Filled.Size20.Clock(),
                    JobStatus.Running => new Icons.Filled.Size20.Rocket(),
                    JobStatus.Staging => new Icons.Filled.Size20.ArrowExportUp(),
                    JobStatus.Finalizing => new Icons.Filled.Size20.CheckmarkCircle(),
                    JobStatus.Finished => new Icons.Filled.Size20.CheckmarkCircle(),
                    JobStatus.Aborting => new Icons.Regular.Size20.HandRight(),
                    JobStatus.Aborted => new Icons.Filled.Size20.RecordStop(),
                    JobStatus.Failed => new Icons.Filled.Size20.ErrorCircle(),
                    JobStatus.Deleted => new Icons.Filled.Size20.Delete(),
                    JobStatus.Clearing => new Icons.Filled.Size20.Broom(),
                    _ => new Icons.Filled.Size20.QuestionCircle()
                },
            _ =>
                status switch
                {
                    JobStatus.Building => new Icons.Filled.Size20.Edit(),
                    JobStatus.Waiting => new Icons.Filled.Size24.Clock(),
                    JobStatus.Running => new Icons.Filled.Size24.Rocket(),
                    JobStatus.Staging => new Icons.Filled.Size24.ArrowExportUp(),
                    JobStatus.Finalizing => new Icons.Filled.Size24.CheckmarkCircle(),
                    JobStatus.Finished => new Icons.Filled.Size24.CheckmarkCircle(),
                    JobStatus.Aborting => new Icons.Regular.Size24.HandRight(),
                    JobStatus.Aborted => new Icons.Filled.Size24.RecordStop(),
                    JobStatus.Failed => new Icons.Filled.Size24.ErrorCircle(),
                    JobStatus.Deleted => new Icons.Filled.Size24.Delete(),
                    JobStatus.Clearing => new Icons.Filled.Size24.Broom(),
                    _ => new Icons.Filled.Size24.QuestionCircle()
                }
        };
    }

    /// <summary>
    /// Applies an appropriate color to an icon based on a JobStatus value.
    /// </summary>
    public static Icon WithStatusColor(this Icon icon, JobStatus status)
    {
        return status switch
        {
            JobStatus.Building => icon.WithColor("var(--neutral-foreground-rest)"),
            JobStatus.Waiting => icon.WithColor("#aaa"),
            JobStatus.Staging => icon.WithColor("#aaa"),
            JobStatus.Running => icon.WithColor("#1b8ce3"),
            JobStatus.Finalizing => icon.WithColor("#1b8ce3"),
            JobStatus.Finished => icon.WithColor("#13a10e"),
            JobStatus.Aborting => icon.WithColor("#e9835e"),
            JobStatus.Aborted => icon.WithColor("#d13438"),
            JobStatus.Failed => icon.WithColor("#d13438"),
            JobStatus.Clearing => icon.WithColor("#e9835e"),
            _ => icon
        };
    }

    public static string GetStatusHexColor(JobStatus status) => status switch
    {
        JobStatus.Building => "#888",
        JobStatus.Waiting => "#aaa",
        JobStatus.Staging => "#aaa",
        JobStatus.Running => "#1b8ce3",
        JobStatus.Finalizing => "#1b8ce3",
        JobStatus.Finished => "#13a10e",
        JobStatus.Aborting => "#e9835e",
        JobStatus.Aborted => "#d13438",
        JobStatus.Failed => "#d13438",
        JobStatus.Clearing => "#e9835e",
        _ => "#888"
    };
}

/// <summary>
/// Defines standard icon sizes used throughout the application.
/// </summary>
/// <remarks>
/// This enum is used extensively in JobLine components to specify the size of job status icons.
/// Components can specify the desired icon size through the IconSize parameter, with Size16
/// being the default. The consistent use of these standard sizes helps maintain visual coherence
/// across the application.
/// </remarks>
public enum IconSize
{
    /// <summary>
    /// Small 16x16 pixel icon, suitable for dense UIs and inline with text.
    /// </summary>
    Size16 = 16,
    
    /// <summary>
    /// Medium 20x20 pixel icon, suitable for buttons and list items.
    /// </summary>
    Size20 = 20,
    
    /// <summary>
    /// Large 24x24 pixel icon, suitable for headers and primary actions.
    /// </summary>
    Size24 = 24
}