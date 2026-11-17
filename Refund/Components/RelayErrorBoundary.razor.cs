#nullable enable
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Refund.Services.Core.Session;

namespace Refund.Components;

/// <summary>
/// Contains a failing part of the UI so it shows an error message instead of tearing down the whole circuit.
/// </summary>
/// <remarks>
/// Like any <see cref="ErrorBoundaryBase"/>, this only catches exceptions thrown by the render, lifecycle
/// methods and event handlers of its <em>descendants</em>. Place it in the parent or host of the component
/// to protect. It cannot catch <c>async void</c> methods, fire-and-forget tasks or timer callbacks.
///
/// A tripped boundary stays in its error state until it recovers. It recovers when the user clicks Retry,
/// when <see cref="ResetKey"/> changes, or, with <see cref="ResetOnNavigation"/>, when the session navigates
/// to another project, space, view, folder or factory.
/// </remarks>
public partial class RelayErrorBoundary : ErrorBoundaryBase, IDisposable
{
    [Inject] private ILogger<RelayErrorBoundary> Logger { get; set; } = default!;
    [Inject] private IServiceProvider Services { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <summary>Names the protected region in the message and the log, e.g. "Job card".</summary>
    [Parameter] public string? Region { get; set; }

    /// <summary>Recovers from an error whenever this value changes (compared with <see cref="object.Equals(object?, object?)"/>).</summary>
    [Parameter] public object? ResetKey { get; set; }

    /// <summary>Recovers from an error when the session navigates to another project, space, view, folder or factory.</summary>
    [Parameter] public bool ResetOnNavigation { get; set; }

    /// <summary>Renders a one-line message with a small Retry button and no stack trace, for cards and fields.</summary>
    [Parameter] public bool Compact { get; set; }

    /// <summary>Additional buttons shown next to Retry in the default error UI, e.g. "Close".</summary>
    [Parameter] public RenderFragment? ErrorActions { get; set; }

    /// <summary>Inline style for the default error UI, e.g. to give a card-sized fallback fixed dimensions.</summary>
    [Parameter] public string? Style { get; set; }

    private object? _lastResetKey;
    private bool _hasResetKey;
    private object? _lastNavigationKey;
    private bool _subscribedToNavigation;
    /// <summary>Resolved only for <see cref="ResetOnNavigation"/>, so the boundary also works outside Relay's shell.</summary>
    private RelaySession? _session;

    protected override void OnParametersSet()
    {
        base.OnParametersSet();

        if (_hasResetKey && !Equals(_lastResetKey, ResetKey))
            Recover();
        _lastResetKey = ResetKey;
        _hasResetKey = true;

        if (ResetOnNavigation && !_subscribedToNavigation)
        {
            _session ??= Services.GetService<RelaySession>();
            if (_session != null)
            {
                _lastNavigationKey = NavigationKey(_session);
                _session.OnStateChanged += HandleSessionStateChanged;
                _subscribedToNavigation = true;
            }
        }
        else if (!ResetOnNavigation && _subscribedToNavigation)
        {
            _session!.OnStateChanged -= HandleSessionStateChanged;
            _subscribedToNavigation = false;
        }
    }

    protected override Task OnErrorAsync(Exception exception)
    {
        Logger.LogError(exception, "UI region {Region} failed at {Uri}", Region ?? "(unnamed)", Navigation.Uri);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Session handlers run one after another and the first throw aborts the rest, so this one never throws.
    /// </summary>
    private async Task HandleSessionStateChanged()
    {
        try
        {
            await InvokeAsync(() =>
            {
                var key = NavigationKey(_session!);
                if (Equals(key, _lastNavigationKey))
                    return;
                _lastNavigationKey = key;
                Recover();
            });
        }
        catch (Exception exc)
        {
            Logger.LogWarning(exc, "Couldn't reset UI region {Region} after navigation", Region ?? "(unnamed)");
        }
    }

    /// <summary>The location the boundary resets on. Jobs and overlays are left out so opening one doesn't reset the main screen.</summary>
    private static object NavigationKey(RelaySession s) => (s.CurrentMain, s.ProjectId, s.SpaceId, s.ViewId,
                                                            s.FolderId, s.FactoryDefinitionId, s.FactoryInstanceId);

    public void Dispose()
    {
        if (_subscribedToNavigation)
            _session!.OnStateChanged -= HandleSessionStateChanged;
        _subscribedToNavigation = false;
    }
}
