using Microsoft.AspNetCore.Components;
using Refund.Components;
using Warp.Tools;

namespace Refund.Tests.Components;

public class InputBindingTests
{
    [Fact]
    public async Task SupersededQueuedDraftsAreNotPersisted()
    {
        var input = new TestInput<string>();
        input.Receive("");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        input.ValueChanged = EventCallback.Factory.Create<string>(new object(), async value =>
        {
            calls.Add(value);
            if (calls.Count == 1) await first.Task;
            input.Receive(value);
        });
        var a = input.Edit("a");
        var ab = input.Edit("ab");
        var abc = input.Edit("abc");
        first.SetResult();
        await Task.WhenAll(a, ab, abc);
        Assert.Equal(new[] { "a", "abc" }, calls);
        Assert.Equal("abc", input.Draft);
    }

    [Fact]
    public async Task CodeEditorAwaitsExactlyOneCallbackPerChange()
    {
        var editor = new Refund.Components.CodeEditor.CodeEditor { Value = "old" };
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        editor.ValueChanged = EventCallback.Factory.Create<string>(new object(), async value =>
        {
            calls.Add(value);
            await saved.Task;
        });
        var changing = editor.OnValueChanged("new");
        Assert.Equal(new[] { "new" }, calls);
        Assert.False(changing.IsCompleted);
        saved.SetResult();
        await changing;
        await editor.OnValueChanged("new");
        Assert.Single(calls);
    }

    [Fact]
    public async Task VectorDraftPreservesOtherEditsWhileCallbacksAndModelUpdatesLag()
    {
        var input = new TestInput<int3>();
        input.Receive(new(10, 20, 30));
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new List<int3>();
        input.ValueChanged = EventCallback.Factory.Create<int3>(new object(), async value =>
        {
            received.Add(value);
            await (received.Count == 1 ? first.Task : second.Task);
            input.Receive(value);
        });

        var xEdit = input.Edit(new(11, input.Draft.Y, input.Draft.Z));
        var yEdit = input.Edit(new(input.Draft.X, 21, input.Draft.Z));
        Assert.Single(received);
        input.Receive(new(10, 20, 30)); // A render of the still-unsaved model.
        Assert.Equal(new int3(11, 21, 30), input.Draft);

        first.SetResult();
        await xEdit;
        Assert.Equal(new int3(11, 21, 30), input.Draft);
        second.SetResult();
        await yEdit;
        Assert.Equal(new[] { new int3(11, 20, 30), new int3(11, 21, 30) }, received);
        Assert.Equal(new int3(11, 21, 30), input.Draft);
    }

    [Fact]
    public async Task RejectedEditReturnsToModelAndAnExternalResetIsAccepted()
    {
        var input = new TestInput<string>();
        input.Receive("stored");
        input.ValueChanged = EventCallback.Factory.Create<string>(new object(), _ => input.Receive("stored"));
        await input.Edit("rejected");
        Assert.Equal("stored", input.Draft);
        input.Receive("reset");
        Assert.Equal("reset", input.Draft);
    }

    [Fact]
    public async Task FailedCallbackDoesNotBlockLaterEdits()
    {
        var input = new TestInput<string>();
        input.Receive("stored");
        input.ValueChanged = EventCallback.Factory.Create<string>(new object(), _ => Task.FromException(new InvalidOperationException()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => input.Edit("failed"));
        input.ValueChanged = EventCallback.Factory.Create<string>(new object(), value => input.Receive(value));
        await input.Edit("accepted");
        Assert.Equal("accepted", input.Draft);
    }

    [Fact]
    public async Task DisposingAnEditorSkipsQueuedCallbacks()
    {
        var input = new TestInput<string>();
        input.Receive("");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        input.ValueChanged = EventCallback.Factory.Create<string>(new object(), async value =>
        {
            calls.Add(value);
            await first.Task;
        });
        var saving = input.Edit("first");
        var queued = input.Edit("second");
        input.Dispose();
        first.SetResult();
        await Task.WhenAll(saving, queued);
        Assert.Equal(new[] { "first" }, calls);
    }

    [Fact]
    public void NullTextIsNormalizedWithoutDroppingOtherParameters()
    {
        var parameters = TextInputParameters.Normalize(new object(), ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Value"] = null,
            ["Immediate"] = true
        }));
        Assert.Equal("", parameters.GetValueOrDefault<string>("Value"));
        Assert.True(parameters.GetValueOrDefault<bool>("Immediate"));
    }

    private sealed class TestInput<T> : BufferedInputBase<T>
    {
        public T Draft => EditingValue;
        public Task Edit(T value) => SetValueAsync(value);
        public void Receive(T value) { Value = value; OnParametersSet(); }
    }
}
