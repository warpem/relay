using Microsoft.AspNetCore.Components;
using Refund.Services.Core.Session;

namespace Refund.Tests.Services;

public class RelaySessionNavigationTests
{
    [Fact]
    public async Task NavigatingToANewUrlAddsOneEntryButRepeatingItAddsNone()
    {
        var navigation = new RecordingNavigationManager("/P1");
        await using var session = new RelaySession(navigation, null!, null!, null!);

        await session.NavigateToAsync(new NavigationRequest());
        await session.NavigateToAsync(new NavigationRequest());

        Assert.Equal(["https://relay.test/"], navigation.Navigations);
    }

    [Fact]
    public async Task StateOnlyNavigationStillUpdatesTheOverlayWithoutWritingHistory()
    {
        var navigation = new RecordingNavigationManager("/");
        await using var session = new RelaySession(navigation, null!, null!, null!);
        var overlays = new List<OverlayScreenType>();
        int stateChanges = 0;
        session.OnStateChanged += () =>
        {
            stateChanges++;
            return Task.CompletedTask;
        };
        session.OnOverlayChanged += () =>
        {
            overlays.Add(session.CurrentOverlay);
            return Task.CompletedTask;
        };

        await session.NavigateToAsync(new NavigationRequest { Overlay = OverlayScreenType.Queues });
        await session.NavigateToAsync(new NavigationRequest());

        Assert.Empty(navigation.Navigations);
        Assert.Equal(2, stateChanges);
        Assert.Equal([OverlayScreenType.Queues, OverlayScreenType.None], overlays);
    }

    private sealed class RecordingNavigationManager : NavigationManager
    {
        public List<string> Navigations { get; } = [];

        public RecordingNavigationManager(string path) => Initialize("https://relay.test/", "https://relay.test" + path);

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).AbsoluteUri;
            Navigations.Add(Uri);
            NotifyLocationChanged(isInterceptedLink: false);
        }
    }
}
