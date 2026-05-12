using Microsoft.AspNetCore.Components;
using Refund.Services.Core.DataManager;
using Refund.Services.Core.Session;

namespace Relay.Screens.Overlay.Job;

public partial class OverlayJob : ComponentBase, IDisposable
{
    [Inject]
    RelaySession Session { get; set; }
    
    [Inject]
    DataManager DataManager { get; set; }

    protected override void OnInitialized()
    {
        base.OnInitialized();
        
        Session.OnJobChanged += HandleJobChanged;
    }

    private async Task HandleJobChanged()
    {
        await InvokeAsync(StateHasChanged);
    }

    private async Task OnOverlayClose()
    {
        await Session.NavigateToAsync(new()
        {
            ProjectId = Session.ProjectId,
            SpaceId = Session.SpaceId,
            ViewId = Session.ViewId,
            FolderId = Session.FolderId,
            JobId = null,
            Overlay = Session.CurrentOverlay
        });
    }

    private async Task HandleFinishInteractiveClicked()
    {
        await DataManager.UpdateJob(Session.User, Session.Job, originalJob =>
        {
            originalJob.IsInteractiveFinished = true;
        });
    }
    
    public void Dispose()
    {
        Session.OnJobChanged -= HandleJobChanged;
    }
}