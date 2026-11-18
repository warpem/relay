using Microsoft.JSInterop;
using Refund.Services;

namespace Refund.Tests.Services;

public class JobSortingServiceTests
{
    [Fact]
    public async Task RestoresCriterionAndDirectionInANewSession()
    {
        var browser = new BrowserStorage();
        var firstSession = new JobSortingService(browser);
        await firstSession.SetCriterion(JobSortCriterion.Name);
        await firstSession.ToggleDirection();

        var nextSession = new JobSortingService(browser);
        int notifications = 0;
        nextSession.OnSortChanged += () => { notifications++; return Task.CompletedTask; };
        await nextSession.InitializeAsync();
        await nextSession.InitializeAsync();

        Assert.Equal(JobSortCriterion.Name, nextSession.Criterion);
        Assert.False(nextSession.IsAscending);
        Assert.Equal(1, notifications);

        // Returning to defaults must replace the saved non-default preference.
        await nextSession.SetCriterion(JobSortCriterion.Custom);
        await nextSession.SetAscending(true);
        var resetSession = new JobSortingService(browser);
        await resetSession.InitializeAsync();
        Assert.Equal(JobSortCriterion.Custom, resetSession.Criterion);
        Assert.True(resetSession.IsAscending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("999:False")]
    [InlineData("Name:invalid")]
    public async Task MissingOrInvalidPreferencesKeepDefaults(string? saved)
    {
        var service = new JobSortingService(new BrowserStorage { Saved = saved });
        await service.InitializeAsync();
        Assert.Equal(JobSortCriterion.Custom, service.Criterion);
        Assert.True(service.IsAscending);
    }

    [Fact]
    public async Task UnavailableStorageDoesNotPreventSortingOrNotifications()
    {
        var service = new JobSortingService(new BrowserStorage { Unavailable = true });
        int notifications = 0;
        service.OnSortChanged += () => { notifications++; return Task.CompletedTask; };

        await service.InitializeAsync();
        await service.SetCriterion(JobSortCriterion.Status);
        await service.ToggleDirection();

        Assert.Equal(JobSortCriterion.Status, service.Criterion);
        Assert.False(service.IsAscending);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public async Task ChangingDirectionPreservesPreviouslySavedCriterion()
    {
        var browser = new BrowserStorage { Saved = "Color:False" };
        var service = new JobSortingService(browser);
        await service.ToggleDirection();

        Assert.Equal(JobSortCriterion.Color, service.Criterion);
        Assert.True(service.IsAscending);
        Assert.Equal("Color:True", browser.Saved);
    }

    private sealed class BrowserStorage : IJSRuntime
    {
        public string? Saved { get; set; }
        public bool Unavailable { get; init; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken,
                                                   object?[]? args)
        {
            if (Unavailable)
                throw new JSException("Storage is blocked");

            Assert.Equal("relay.jobSort", args![0]);
            if (identifier == "localStorage.getItem")
                return ValueTask.FromResult((TValue)(object)Saved!);

            Assert.Equal("localStorage.setItem", identifier);
            Saved = (string)args[1]!;
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
