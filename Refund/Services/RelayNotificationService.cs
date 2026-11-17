#nullable enable
using Microsoft.FluentUI.AspNetCore.Components;
using Refund.Components;

namespace Refund.Services;

/// <summary>
/// Fluent's notification service, except that error toasts stay until the user dismisses them and offer a Copy
/// button, so a message can be read in full and pasted into a bug report.
/// </summary>
/// <remarks>
/// Registered as <see cref="INotificationService"/> in place of Fluent's own. Re-declaring the interface makes the
/// <c>new</c> <see cref="ShowErrorToastAsync"/> below the interface implementation, so every existing
/// <c>ShowErrorToastAsync</c> call picks it up without changes. Fluent's toast provider only requires a
/// <see cref="NotificationService"/>, which this still is.
/// </remarks>
public sealed class RelayNotificationService : NotificationService, INotificationService
{
    public RelayNotificationService(IServiceProvider serviceProvider) : base(serviceProvider)
    {
    }

    /// <summary>
    /// Shows an error toast that stays until dismissed, unless <paramref name="lifetime"/> (seconds) is given.
    /// An identical error that is still on screen is not shown a second time, so repeated failures don't fill the
    /// toast queue (Fluent shows at most four at once and queues the rest).
    /// </summary>
    public new Task<ToastResult> ShowErrorToastAsync(string title, string? message = null, int? lifetime = null,
                                                     string? dismissLabel = null,
                                                     Func<ToastEventArgs, Task>? dismissOnClickAsync = null)
    {
        string text = string.IsNullOrEmpty(message) ? title : $"{title}\n\n{message}";

        string id = $"relay-error-{StableHash(text):x8}";
        var existing = GetToastInstance(id);
        if (existing != null)
        {
            if (existing.LifecycleStatus is ToastLifecycleStatus.Queued or ToastLifecycleStatus.Visible)
                return existing.Result;

            // The previous copy was dismissed but is still animating out under the same id.
            id += "-" + Guid.NewGuid().ToString("N")[..8];
        }

        var options = new ToastOptions
        {
            Id = id,
            Intent = ToastIntent.Error,
            Title = title,
            Message = message,
            // Complete the returned task once the toast is queued: callers that await it must not wait for the
            // user to dismiss a toast that no longer times out.
            ResultTiming = ToastResultTiming.Queued,
            Lifetime = lifetime.HasValue ? TimeSpan.FromSeconds(lifetime.Value) : TimeSpan.Zero,
            AllowDismiss = true,
            Width = "min(480px, calc(100vw - 40px))",
            Class = "relay-error-toast",
        };
        options.Parameters[nameof(ErrorToastCopyButton.Text)] = text;

        if (!string.IsNullOrEmpty(dismissLabel))
        {
            options.DismissAction.Label = dismissLabel;
            options.DismissAction.OnClickAsync = async e =>
            {
                if (dismissOnClickAsync != null)
                    await dismissOnClickAsync(e);
                await e.Instance.CloseAsync(ToastCloseReason.Dismissed);
            };
        }

        return ShowToastAsync<ErrorToastCopyButton>(options);
    }

    /// <summary>FNV-1a; string.GetHashCode is randomized per process, which is fine here but this is reproducible in logs.</summary>
    private static uint StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
            hash = (hash ^ c) * 16777619;
        return hash;
    }
}
