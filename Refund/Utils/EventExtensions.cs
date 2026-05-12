namespace Refund.Utils;

/// <summary>
/// Provides extension methods for asynchronous event handling and subscription management,
/// allowing for controlled invocation of multiple async event handlers.
/// </summary>
/// <remarks>
/// Commonly used in the RelaySession to handle application-wide events like theme changes,
/// window resizing, and authentication state changes in a predictable sequential manner.
/// </remarks>
public static class EventExtensions
{
    /// <summary>
    /// Asynchronously invokes all delegate methods in the invocation list of an event handler.
    /// </summary>
    /// <param name="handler">The event handler with multiple subscribers to invoke.</param>
    /// <returns>A task that completes when all subscribers have been invoked.</returns>
    /// <remarks>
    /// Unlike standard multicast delegate invocation which calls handlers synchronously,
    /// this method awaits each handler before moving to the next, ensuring they are executed
    /// in sequence without blocking the calling thread.
    /// 
    /// Primarily used in session-level event handling such as theme changes (OnThemeChanged),
    /// window resize events (OnWindowResized), and authentication state changes (OnStateChanged)
    /// in the RelaySession class.
    /// </remarks>
    public static async Task InvokeAllAsync(this Func<Task> handler)
    {
        if (handler != null)
            foreach (var invocation in handler.GetInvocationList())
                await ((Func<Task>)invocation).Invoke();
    }
    
    /// <summary>
    /// Asynchronously invokes all delegate methods in the invocation list of an event handler,
    /// passing a single argument to each handler.
    /// </summary>
    /// <typeparam name="T">The type of the argument to pass to each handler.</typeparam>
    /// <param name="handler">The event handler with multiple subscribers to invoke.</param>
    /// <param name="arg">The argument to pass to each handler.</param>
    /// <returns>A task that completes when all subscribers have been invoked.</returns>
    public static async Task InvokeAllAsync<T>(this Func<T, Task> handler, T arg)
    {
        if (handler != null)
            foreach (var invocation in handler.GetInvocationList())
                await ((Func<T, Task>)invocation).Invoke(arg);
    }
    
    /// <summary>
    /// Asynchronously invokes all delegate methods in the invocation list of an event handler,
    /// passing two arguments to each handler.
    /// </summary>
    /// <typeparam name="T1">The type of the first argument.</typeparam>
    /// <typeparam name="T2">The type of the second argument.</typeparam>
    /// <param name="handler">The event handler with multiple subscribers to invoke.</param>
    /// <param name="arg1">The first argument to pass to each handler.</param>
    /// <param name="arg2">The second argument to pass to each handler.</param>
    /// <returns>A task that completes when all subscribers have been invoked.</returns>
    public static async Task InvokeAllAsync<T1, T2>(this Func<T1, T2, Task> handler, T1 arg1, T2 arg2)
    {
        if (handler != null)
            foreach (var invocation in handler.GetInvocationList())
                await ((Func<T1, T2, Task>)invocation).Invoke(arg1, arg2);
    }
    
    /// <summary>
    /// Asynchronously invokes all delegate methods in the invocation list of an event handler,
    /// passing three arguments to each handler.
    /// </summary>
    /// <typeparam name="T1">The type of the first argument.</typeparam>
    /// <typeparam name="T2">The type of the second argument.</typeparam>
    /// <typeparam name="T3">The type of the third argument.</typeparam>
    /// <param name="handler">The event handler with multiple subscribers to invoke.</param>
    /// <param name="arg1">The first argument to pass to each handler.</param>
    /// <param name="arg2">The second argument to pass to each handler.</param>
    /// <param name="arg3">The third argument to pass to each handler.</param>
    /// <returns>A task that completes when all subscribers have been invoked.</returns>
    public static async Task InvokeAllAsync<T1, T2, T3>(this Func<T1, T2, T3, Task> handler, T1 arg1, T2 arg2, T3 arg3)
    {
        if (handler != null)
            foreach (var invocation in handler.GetInvocationList())
                await ((Func<T1, T2, T3, Task>)invocation).Invoke(arg1, arg2, arg3);
    }
    
    /// <summary>
    /// Subscribes a callback to an event handler and returns an IDisposable that can be used to unsubscribe.
    /// </summary>
    /// <param name="eventHandler">The event handler to subscribe to.</param>
    /// <param name="callback">The callback to execute when the event is raised.</param>
    /// <returns>
    /// An IDisposable object that, when disposed, will unsubscribe the callback from the event handler.
    /// </returns>
    /// <remarks>
    /// This method allows for a more RAII-like subscription model, where the subscription lifetime
    /// can be tied to the lifetime of another object through the returned IDisposable.
    /// </remarks>
    public static IDisposable Subscribe(this Func<Task> eventHandler, Func<Task> callback)
    {
        eventHandler += callback;
        return new EventSubscription(() => eventHandler -= callback);
    }
}

/// <summary>
/// Represents a subscription to an event that can be disposed to unsubscribe from the event.
/// </summary>
/// <remarks>
/// This class implements the disposable pattern to enable automatic cleanup of event subscriptions,
/// preventing memory leaks caused by forgotten event handler references.
/// It's typically created through the <see cref="EventExtensions.Subscribe"/> method.
/// </remarks>
public class EventSubscription : IDisposable
{
    private readonly Action _unsubscribe;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventSubscription"/> class.
    /// </summary>
    /// <param name="unsubscribe">The action to execute when this subscription is disposed.</param>
    public EventSubscription(Action unsubscribe)
    {
        _unsubscribe = unsubscribe;
    }

    /// <summary>
    /// Unsubscribes from the event by executing the unsubscribe action.
    /// </summary>
    public void Dispose()
    {
        _unsubscribe();
    }
}