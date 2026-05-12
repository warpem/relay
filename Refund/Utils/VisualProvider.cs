using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components;
using Refund.DataModel;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Refund.Utils;

/// <summary>
/// Provides visual assets and layout calculations for UI components,
/// particularly for job status icons and job card sizing constants.
/// </summary>
/// <remarks>
/// This class centralizes visual styling decisions to maintain consistency across
/// the application UI, especially for job status representation and card layout.
/// 
/// The class defines critical UI constants like JabCardContentSquareSideLength that are used
/// for proper scaling and layout of job cards and their previews throughout the application.
/// </remarks>
public static class VisualProvider
{
    /// <summary>
    /// Gets the CSS class name for the icon representing a job status.
    /// </summary>
    /// <param name="status">The job status to get an icon class name for.</param>
    /// <returns>A CSS class name for the corresponding status icon.</returns>
    /// <remarks>
    /// These class names correspond to defined CSS styles in the application's stylesheet.
    /// Some statuses are currently missing icon implementations as noted in the TODO comments.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if an unrecognized job status is provided.</exception>
    public static string GetJobStatusIcon(JobStatus status) =>
        status switch
        {
            // TODO: missing statuses
            //DataModelNew.JobStatus.Resuming => "resuming-icon", //TODO: post MVP
            JobStatus.Waiting => "waiting-icon",
            JobStatus.Staging => "staging-icon",
            JobStatus.Running => "running-icon",
            JobStatus.Finished => "finished-icon",
            JobStatus.Aborted => "aborted-icon",
            JobStatus.Failed => "failed-icon",
            JobStatus.Building => "building-icon",
            JobStatus.Finalizing => "", // TODO: additional statuses
            JobStatus.Aborting => "",   // TODO: additional statuses
            JobStatus.Deleted => "",    // TODO: additional statuses
            JobStatus.Clearing => "clearing-icon",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

    /// <summary>
    /// Gets a Fluent UI icon component for a job status.
    /// </summary>
    /// <param name="status">The job status to get an icon for.</param>
    /// <returns>An icon component appropriate for the job status.</returns>
    /// <remarks>
    /// This extension method maps job statuses to appropriate icon components, which can be
    /// directly used in Blazor components. For some transitional statuses, temporary placeholder
    /// icons are used until proper icons are implemented.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if an unrecognized job status is provided.</exception>
    public static Icon GetIcon(this JobStatus status) =>
        status switch
        {
            //JobStatus.Resuming => new ResumingIcon(),     //TODO: post MVP
            JobStatus.Waiting => new WaitingIcon(),
            JobStatus.Staging => new StagingIcon(),
            JobStatus.Running => new RunningIcon(),
            JobStatus.Finished => new FinishedIcon(),
            JobStatus.Aborted => new AbortedIcon(),
            JobStatus.Failed => new FailedIcon(),
            JobStatus.Building => new BuildingIcon(),
            JobStatus.Finalizing => new Icons.Regular.Size16.CircleHint(), // Temporary icon until a specific one is created
            JobStatus.Aborting => new Icons.Regular.Size16.CircleHint(),   // Temporary icon until a specific one is created
            JobStatus.Deleted => new Icons.Regular.Size16.CircleHint(),    // Temporary icon until a specific one is created
            JobStatus.Clearing => new ClearingIcon(),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
        };

    #region JobCard
    /// <summary>
    /// The standard dimension of a square content section in a job card, in pixels.
    /// </summary>
    /// <remarks>
    /// This value (144px) corresponds to 9rem in the CSS and defines the basic unit
    /// of measurement for card content areas.
    /// 
    /// Used throughout the application for consistent card sizing and in places like
    /// JobLine.razor.cs for calculation of proper preview scaling:
    /// 
    /// ```csharp
    /// private string PreviewScaling => (_job.Status != JobStatus.Failed && _job.VisAvailableIteration >= 0) ?
    ///                                 $"transform: scale({96.0 / VisualProvider.JabCardContentSquareSideLength});" :
    ///                                 "";
    /// ```
    /// 
    /// This scaling ensures that job card previews render consistently across different contexts.
    /// </remarks>
    public const int JabCardContentSquareSideLength = 144; // 9 rem
    
    /// <summary>
    /// The default number of content squares per side in a job card.
    /// </summary>
    private const int JobCardDefaultNumberOfSquaresPerSide = 1;
    
    /// <summary>
    /// The default height for a job card's content area, in pixels.
    /// </summary>
    private const int DefaultJobCardHeight = JabCardContentSquareSideLength;
    
    /// <summary>
    /// The default width for a job card's content area, in pixels.
    /// </summary>
    private const int DefaultJobCardWidth = JabCardContentSquareSideLength;

    /// <summary>
    /// The height of a job card's header section, in pixels.
    /// </summary>
    public const int JobCardHeaderHeight = 26;
    
    /// <summary>
    /// The height of a job card's footer section, in pixels.
    /// </summary>
    public const int JobCardFooterHeight = 26;

    /// <summary>
    /// The total vertical offset to add to content height when calculating the full job card height.
    /// </summary>
    /// <remarks>
    /// This represents the sum of the header and footer heights.
    /// </remarks>
    private const int JobCardHeightOffset = JobCardHeaderHeight + JobCardFooterHeight;

    /// <summary>
    /// The total horizontal offset to add to content width when calculating the full job card width.
    /// </summary>
    /// <remarks>
    /// This represents the sum of left and right padding/margins (10px on each side).
    /// </remarks>
    public const int JobCardWidthOffset = 10 * 2; // 10 px for both left and right

    /// <summary>
    /// Gets the minimum width for a job card, in pixels.
    /// </summary>
    /// <returns>The minimum job card width, including content and offsets.</returns>
    public static int GetMinJobCardWidth() => DefaultJobCardWidth + JobCardWidthOffset;
    
    /// <summary>
    /// Gets the minimum height for a job card, in pixels.
    /// </summary>
    /// <returns>The minimum job card height, including content and offsets.</returns>
    public static int GetMinJobCardHeight() => DefaultJobCardHeight + JobCardHeightOffset;

    /// <summary>
    /// Calculates the width of a job card based on the number of content squares.
    /// </summary>
    /// <param name="numberOfSquares">The number of content squares in the horizontal dimension.</param>
    /// <returns>The calculated width of the job card in pixels.</returns>
    /// <remarks>
    /// For special cases where numberOfSquares is 0, the minimum card width is returned.
    /// Otherwise, the width is calculated as the number of squares multiplied by the standard
    /// square size, plus the fixed width offset for margins/padding.
    /// </remarks>
    public static int GetWidth(int numberOfSquares)
    {
        if(numberOfSquares == 0)
            return GetMinJobCardWidth();

        var calculatedWidth = JabCardContentSquareSideLength * numberOfSquares +
                              JobCardWidthOffset;

        return calculatedWidth;
    }

    /// <summary>
    /// Calculates the height of a job card based on the number of content squares.
    /// </summary>
    /// <param name="numberOfSquares">The number of content squares in the vertical dimension.</param>
    /// <returns>The calculated height of the job card in pixels.</returns>
    /// <remarks>
    /// For special cases where numberOfSquares is 0, the minimum card height is returned.
    /// Otherwise, the height is calculated as the number of squares multiplied by the standard
    /// square size, plus the fixed height offset for header/footer.
    /// </remarks>
    public static int GetHeight(int numberOfSquares)
    {
        if(numberOfSquares == 0)
            return GetMinJobCardHeight();

        return JabCardContentSquareSideLength * numberOfSquares + JobCardHeightOffset;
    }
    #endregion
}