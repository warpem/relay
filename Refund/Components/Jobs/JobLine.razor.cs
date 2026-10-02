using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.DataModel;
using Refund.DataModel.ReadOnly;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;
using Refund.Utils;
using IconSize = Refund.Utils.IconSize;

namespace Refund.Components.Jobs;

/// <summary>
/// Reusable component for displaying job information in a compact line format.
/// Provides job status, navigation, and preview functionality.
/// </summary>
public partial class JobLine : IDisposable, IHandleEvent
{
    /// <summary>
    /// The read-only job to display. Required.
    /// </summary>
    [Parameter, EditorRequired]
    public ReadOnlyJob Job { get; set; }
    private ReadOnlyJob _job;

    /// <summary>
    /// Controls whether clicking the job line will navigate to the job details.
    /// </summary>
    [Parameter]
    public bool EnableNavigation { get; set; } = true;
    
    /// <summary>
    /// Controls whether the job status icon is displayed.
    /// </summary>
    [Parameter]
    public bool ShowStatus { get; set; } = true;
    
    /// <summary>
    /// Controls whether hovering over the job line shows a preview tooltip.
    /// </summary>
    [Parameter]
    public bool ShowPreview { get; set; } = true;
    
    /// <summary>
    /// Determines the position of the preview tooltip relative to the job line.
    /// </summary>
    [Parameter]
    public TooltipPosition PreviewPosition { get; set; } = TooltipPosition.Right;

    /// <summary>
    /// Font size for text in the job line.
    /// </summary>
    [Parameter]
    public string FontSize { get; set; } = "0.75rem";
    
    /// <summary>
    /// Size of the status icon.
    /// </summary>
    [Parameter]
    public IconSize IconSize { get; set; } = IconSize.Size16;

    /// <summary>
    /// Width of the job name section.
    /// </summary>
    [Parameter]
    public string NameWidth { get; set; } = "100%";

    /// <summary>
    /// Allow the job name to wrap to multiple lines.
    /// </summary>
    [Parameter]
    public bool AllowMultiline { get; set; } = false;

    /// <summary>
    /// Controls whether to include the project name in the job display.
    /// </summary>
    [Parameter]
    public bool IncludeProject { get; set; } = false;

    /// <summary>
    /// Controls whether to include the space name in the job display.
    /// </summary>
    [Parameter]
    public bool IncludeSpace { get; set; } = false;
    
    /// <summary>
    /// Data manager for job updates and status changes.
    /// </summary>
    [Inject]
    private DataManager DataManager { get; set; }
    
    /// <summary>
    /// Session service for navigation.
    /// </summary>
    [Inject]
    private RelaySession Session { get; set; }
    
    /// <summary>
    /// Unique identifier for this component instance.
    /// </summary>
    private readonly string _id = Guid.NewGuid().ToString();

    /// <summary>
    /// Navigation URL for this job line, computed when navigation is enabled.
    /// </summary>
    private string _navigationUrl;
    
    /// <summary>
    /// Subscription for job update events.
    /// </summary>
    private GroupEventSubscription _subscription;
    
    /// <summary>
    /// Open state of the job card preview; the card is only rendered while it's open.
    /// </summary>
    private readonly HoverPreview<ReadOnlyJob> _preview;

    /// <summary>
    /// Keeps the preview card's DOM IDs distinct from the job's own card and other lines' previews.
    /// </summary>
    private string PreviewDomIdSuffix => $"-preview-{_id}";

    public JobLine()
    {
        _preview = new(() => InvokeAsync(StateHasChanged),
                       job => ShowPreview && job == _job && job.Space?.Jobs.Contains(job) != false);
    }

    /// <summary>
    /// Sets up or updates the job subscription when the job parameter changes.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (_job != Job)
        {
            _subscription?.Unsubscribe();

            _job = Job;
            _preview.Reset();

            if (_job != null && _job.Space != null)
            {
                _subscription = DataManager.JobUpdated.Add(GroupName.SpecificJob(_job), async _ => await InvokeAsync(StateHasChanged));

                if (EnableNavigation)
                    _navigationUrl = RelaySession.BuildUrl(new NavigationRequest
                    {
                        ProjectId = Job.Space.Project.Id,
                        SpaceId = Job.Space.Id,
                        ViewId = Job.Space.Views.FirstOrDefault(v => v.Jobs.Contains(Job))?.Id,
                        JobId = Job.Id
                    });
            }
        }

        if (!ShowPreview)
            _preview.Reset();
    }

    /// <summary>
    /// Formats a timestamp as a human-readable relative time.
    /// For example: "5 min ago", "2 h ago", "3 d ago"
    /// </summary>
    /// <param name="timestamp">The timestamp to format</param>
    /// <returns>Human-readable relative time string</returns>
    private string FormatTimestamp(DateTime timestamp)
    {
        var timeSince = DateTime.Now - timestamp;
        
        if (timeSince.TotalMinutes < 1)
            return "just now";
        if (timeSince.TotalHours < 1)
            return $"{(int)timeSince.TotalMinutes}\u2009min ago";
        if (timeSince.TotalDays < 1)
            return $"{(int)timeSince.TotalHours}\u2009h ago";
        if (timeSince.TotalDays < 30)
            return $"{(int)timeSince.TotalDays}\u2009d ago";
            
        return timestamp.ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// Gets the most relevant timestamp for a job based on its status.
    /// Different statuses highlight different important timestamps.
    /// </summary>
    /// <param name="job">The job to get the timestamp for</param>
    /// <returns>The most relevant timestamp for the job's current status</returns>
    private DateTime? GetRelevantTimestamp(ReadOnlyJob job)
    {
        return job.GetMostRecentEvent(job.Status.ToEventType())?.Timestamp;
    }

    /// <summary>
    /// Handles clicks on the navigable <a> tag.
    /// For normal clicks, navigates via NavigateToAsync (proper async path).
    /// For modifier/middle clicks, bails so the <a> tag handles them natively.
    /// </summary>
    private async Task HandleNavigableClick(MouseEventArgs args)
    {
        if (!MouseUtils.IsPlainLeftClick(args))
            return;
        await Session.NavigateToAsync(new NavigationRequest
        {
            ProjectId = Job.Space.Project.Id,
            SpaceId = Job.Space.Id,
            ViewId = Job.Space.Views.FirstOrDefault(v => v.Jobs.Contains(Job))?.Id,
            JobId = Job.Id
        });
    }

    /// <summary>
    /// Handles job line click, navigating to the job details if enabled.
    /// </summary>
    private async Task HandleClick()
    {
        if (EnableNavigation)
            await Session.NavigateToAsync(new NavigationRequest
            {
                ProjectId = Job.Space.Project.Id,
                SpaceId = Job.Space.Id,
                ViewId = Job.Space.Views.FirstOrDefault(v => v.Jobs.Contains(Job))?.Id,
                JobId = Job.Id
            });
    }

    /// <summary>
    /// Handles mouse enter event to show the preview tooltip after a short delay.
    /// </summary>
    private Task HandleMouseEnter() => ShowPreview ? _preview.ShowAsync(_job, immediate: false) : Task.CompletedTask;

    /// <summary>
    /// Handles mouse leave event to hide the preview tooltip.
    /// </summary>
    private Task HandleMouseLeave() => _preview.HideAsync();

    /// <summary>
    /// Shows the preview tooltip immediately when the line receives keyboard focus.
    /// </summary>
    private Task HandleFocus() => ShowPreview ? _preview.ShowAsync(_job, immediate: true) : Task.CompletedTask;

    /// <summary>
    /// Closes the preview tooltip on Escape.
    /// </summary>
    private Task HandleKeyDown(KeyboardEventArgs args) => args.Key == "Escape" ? _preview.HideAsync() : Task.CompletedTask;

    /// <summary>
    /// Handles DOM events without the automatic re-render: the preview re-renders the line itself
    /// when it opens or closes, so moving the pointer across many lines doesn't re-render them.
    /// </summary>
    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object arg) => callback.InvokeAsync(arg);

    /// <summary>
    /// Cleans up subscriptions when the component is disposed.
    /// </summary>
    public void Dispose()
    {
        _subscription?.Unsubscribe();
        _preview.Dispose();
    }
}
