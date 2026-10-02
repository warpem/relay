using System.Reflection;
using Microsoft.JSInterop;
using Refund.Components.SingleAxisScatter;
using Refund.Services;

namespace Refund.Tests.Components;

public sealed class SingleAxisScatterDisposalTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DisposalReleasesCallbackReferenceEvenAfterBrowserDisconnects(
        bool disconnectOnInvoke, bool disconnectOnDispose)
    {
        var scatter = new TestScatter();
        var type = typeof(SingleAxisScatter);
        type.GetProperty("HighlightService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(scatter, new ScatterHighlightService());
        await scatter.InitializeAsync();
        var callback = (DotNetObjectReference<SingleAxisScatter>)type
            .GetField("_dotNetRef", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scatter)!;
        var module = new TestModule(disconnectOnInvoke, disconnectOnDispose);
        type.GetField("_module", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scatter, module);

        await scatter.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => callback.Value);
        Assert.Equal("disposeScatterPlot", module.InvokedIdentifier);
        if (!disconnectOnInvoke)
            Assert.True(module.DisposeAttempted);
    }

    private sealed class TestScatter : SingleAxisScatter
    {
        public Task InitializeAsync() => OnInitializedAsync();
    }

    private sealed class TestModule(bool disconnectOnInvoke, bool disconnectOnDispose) : IJSObjectReference
    {
        public string? InvokedIdentifier { get; private set; }
        public bool DisposeAttempted { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            InvokedIdentifier = identifier;
            if (disconnectOnInvoke)
                throw new JSDisconnectedException("The browser navigated away.");
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            DisposeAttempted = true;
            if (disconnectOnDispose)
                throw new JSDisconnectedException("The browser navigated away.");
            return ValueTask.CompletedTask;
        }
    }
}
