using System.Diagnostics;
using Serilog;
using Refund.DataModel.ReadOnly;

namespace Refund.Services.Core.DataManager
{
    /// <summary>
    /// Implements a hierarchical event system that allows subscribers to listen for events related to 
    /// specific groups of objects. This is used extensively throughout the DataManager to notify 
    /// UI components and other services about changes to data.
    /// 
    /// The group-based approach allows subscribers to listen only for events relevant to their context,
    /// using wildcard patterns to match multiple objects (e.g., "P1_S*" to listen for all spaces in project 1).
    /// </summary>
    /// <typeparam name="T">The type of object that this event system will handle</typeparam>
    public class GroupEvent<T>
    {
        /// <summary>
        /// Maps group name patterns to their associated event handlers.
        /// The keys are string patterns like "P1_S2_J*" (all jobs in space 2 of project 1).
        /// The values are multicast delegates that will be invoked when an event matches the pattern.
        /// </summary>
        private Dictionary<string, Func<GroupEventArgs<T>, Task>> _groups = new();

        /// <summary>
        /// Subscribes to events for a specific group pattern.
        /// </summary>
        /// <param name="groupName">The group name pattern to subscribe to, e.g., "P1_S2_J*"</param>
        /// <param name="action">The async action to invoke when an event matching the pattern occurs</param>
        /// <returns>A subscription object that can be used to unsubscribe from the event</returns>
        /// <remarks>
        /// Group name patterns use a hierarchical format:
        /// - "P*" - All projects
        /// - "P1" - Project with ID 1
        /// - "P1_S*" - All spaces in project 1
        /// - "P1_S2" - Space with ID 2 in project 1
        /// - "P1_S2_J*" - All jobs in space 2 of project 1
        /// - "P1_S2_J3" - Job with ID 3 in space 2 of project 1
        /// And so on for edges, views, users, and queues.
        /// </remarks>
        public GroupEventSubscription Add(string groupName, Func<GroupEventArgs<T>, Task> action)
        {
            lock (_groups)
            {
                if (!_groups.TryAdd(groupName, action))
                    _groups[groupName] += action;

                if (Debugger.IsAttached)
                    Log.ForContext<GroupEvent<T>>().Debug("Added {MethodName} to {GroupName}", action.Method.Name, groupName);
                
                return new GroupEventSubscription(() => Remove(groupName, action));
            }
        }

        /// <summary>
        /// Unsubscribes from events for a specific group pattern.
        /// This is typically called indirectly via the GroupEventSubscription.Unsubscribe method.
        /// </summary>
        /// <param name="groupName">The group name pattern to unsubscribe from</param>
        /// <param name="action">The action to remove from the event's invocation list</param>
        public void Remove(string groupName, Func<GroupEventArgs<T>, Task> action)
        {
            lock (_groups)
            {
                if (!_groups.ContainsKey(groupName))
                    return;

                _groups[groupName] -= action;
                
                if (Debugger.IsAttached)
                    Log.ForContext<GroupEvent<T>>().Debug("Removed {MethodName} from {GroupName}", action.Method.Name, groupName);
            }
        }

        /// <summary>
        /// Invokes all event handlers subscribed to the specified group name.
        /// This is called by the DataManager when a data change occurs.
        /// </summary>
        /// <param name="groupName">The exact group name (not a pattern) to invoke handlers for</param>
        /// <param name="data">The event arguments containing the object that changed</param>
        /// <returns>A task that completes when all event handlers have been invoked</returns>
        /// <remarks>
        /// Handlers are invoked sequentially, but in a thread-safe manner that prevents
        /// changes to the subscription list during iteration.
        /// </remarks>
        public async Task InvokeHierarchy(T obj, string[] groupNames)
        {
            var args = new GroupEventArgs<T>(obj);
            foreach (var group in groupNames)
                await Invoke(group, args);
        }

        public async Task Invoke(string groupName, GroupEventArgs<T> data)
        {
            Delegate[] invocationList;
            lock (_groups)
            {
                if (!_groups.TryGetValue(groupName, out var handler) || handler == null)
                    return;
                invocationList = handler.GetInvocationList().ToArray();
            }

            foreach (var del in invocationList)
            {
                try
                {
                    await ((Func<GroupEventArgs<T>, Task>)del)(data);
                }
                catch (Exception ex)
                {
                    Log.ForContext<GroupEvent<T>>().Error(ex, "Error invoking subscriber for group {GroupName}", groupName);
                }
            }
        }
    }

    /// <summary>
    /// Represents a subscription to a GroupEvent, providing a way to cleanly unsubscribe.
    /// This helps prevent memory leaks by allowing components to easily clean up their event subscriptions.
    /// </summary>
    /// <param name="unsubscribe">The action to execute when unsubscribing from the event</param>
    public class GroupEventSubscription(Action unsubscribe)
    {
        private Action _unsubscribe = unsubscribe;

        /// <summary>
        /// Unsubscribes from the event this subscription was created for.
        /// This should be called when the subscriber no longer needs to receive notifications,
        /// typically in component Dispose methods.
        /// </summary>
        public void Unsubscribe()
        {
            _unsubscribe?.Invoke();
            _unsubscribe = null;
        }
    }

    /// <summary>
    /// Provides extension methods for working with collections of GroupEventSubscription objects.
    /// </summary>
    public static class GroupEventSubscriptionExtensions
    {
        /// <summary>
        /// Unsubscribes from all subscriptions in the list and then clears the list.
        /// This is a convenience method for components that need to manage multiple subscriptions.
        /// </summary>
        /// <param name="list">The list of subscriptions to unsubscribe and clear</param>
        public static void UnsubscribeAndClear(this List<GroupEventSubscription> list)
        {
            foreach (var sub in list)
                sub.Unsubscribe();
            list.Clear();
        }
    }

    /// <summary>
    /// Provides utility methods for creating standardized group names used in the event system.
    /// These methods ensure consistent formatting and hierarchical relationships between group names.
    /// </summary>
    public static class GroupName
    {
        /// <summary>
        /// Creates a group name for a project with the given ID.
        /// </summary>
        /// <param name="projectId">The project ID, or null to use a wildcard</param>
        /// <returns>A formatted group name string like "P1" or "P*"</returns>
        public static string Project(int? projectId) => $"P{projectId?.ToString() ?? "*"}";
        
        /// <summary>
        /// Creates a group name for a specific project.
        /// </summary>
        /// <param name="p">The project to create a group name for</param>
        /// <returns>A formatted group name string like "P1"</returns>
        public static string SpecificProject(ReadOnlyProject p) => Project(p.Id);
        
        /// <summary>
        /// Creates a group name for a space with the given project and space IDs.
        /// </summary>
        /// <param name="projectId">The project ID, or null to use a wildcard</param>
        /// <param name="spaceId">The space ID, or null to use a wildcard</param>
        /// <returns>A formatted group name string like "P1_S2" or "P1_S*"</returns>
        /// <exception cref="ArgumentException">Thrown if a space ID is provided without a project ID</exception>
        public static string Space(int? projectId, int? spaceId)
        {
            if (projectId == null && spaceId.HasValue)
                throw new ArgumentException("Project ID must be provided if space ID is provided");
            
            return $"P{projectId?.ToString() ?? "*"}_S{spaceId?.ToString() ?? "*"}";
        }
        
        /// <summary>
        /// Creates a group name for a specific space.
        /// </summary>
        /// <param name="s">The space to create a group name for</param>
        /// <returns>A formatted group name string like "P1_S2"</returns>
        public static string SpecificSpace(ReadOnlySpace s) => Space(s.Project.Id, s.Id);

        /// <summary>
        /// Creates a group name for a job with the given project, space, and job IDs.
        /// </summary>
        /// <param name="projectId">The project ID, or null to use a wildcard</param>
        /// <param name="spaceId">The space ID, or null to use a wildcard</param>
        /// <param name="jobId">The job ID, or null to use a wildcard</param>
        /// <returns>A formatted group name string like "P1_S2_J3" or "P1_S2_J*"</returns>
        /// <exception cref="ArgumentException">Thrown if hierarchy is violated (e.g., job ID provided without space ID)</exception>
        public static string Job(int? projectId, int? spaceId, int? jobId)
        {
            if (projectId == null && (spaceId.HasValue || jobId.HasValue))
                throw new ArgumentException("Project ID must be provided if space ID or job ID is provided");
            
            if (spaceId == null && jobId.HasValue)
                throw new ArgumentException("Space ID must be provided if job ID is provided");
            
            return $"P{projectId?.ToString() ?? "*"}_S{spaceId?.ToString() ?? "*"}_J{jobId?.ToString() ?? "*"}";
        }
        
        /// <summary>
        /// Creates a group name for a specific job.
        /// </summary>
        /// <param name="j">The job to create a group name for</param>
        /// <returns>A formatted group name string like "P1_S2_J3"</returns>
        public static string SpecificJob(ReadOnlyJob j) => Job(j.Space.Project.Id, j.Space.Id, j.Id);

        /// <summary>
        /// Creates a group name for an edge with the given project, space, and edge IDs.
        /// </summary>
        /// <param name="projectId">The project ID, or null to use a wildcard</param>
        /// <param name="spaceId">The space ID, or null to use a wildcard</param>
        /// <param name="edgeId">The edge ID, or null to use a wildcard</param>
        /// <returns>A formatted group name string like "P1_S2_E3" or "P1_S2_E*"</returns>
        /// <exception cref="ArgumentException">Thrown if hierarchy is violated (e.g., edge ID provided without space ID)</exception>
        public static string Edge(int? projectId, int? spaceId, int? edgeId)
        {
            if (projectId == null && (spaceId.HasValue || edgeId.HasValue))
                throw new ArgumentException("Project ID must be provided if space ID or edge ID is provided");
            
            if (spaceId == null && edgeId.HasValue)
                throw new ArgumentException("Space ID must be provided if edge ID is provided");
            
            return $"P{projectId?.ToString() ?? "*"}_S{spaceId?.ToString() ?? "*"}_E{edgeId?.ToString() ?? "*"}";
        }
        
        /// <summary>
        /// Creates a group name for a specific edge.
        /// </summary>
        /// <param name="e">The edge to create a group name for</param>
        /// <returns>A formatted group name string like "P1_S2_E3"</returns>
        public static string SpecificEdge(ReadOnlyEdge e) => Edge(e.Space.Project.Id, e.Space.Id, e.Id);
        
        /// <summary>
        /// Creates a group name for a view with the given project, space, and view IDs.
        /// </summary>
        /// <param name="projectId">The project ID, or null to use a wildcard</param>
        /// <param name="spaceId">The space ID, or null to use a wildcard</param>
        /// <param name="viewId">The view ID, or null to use a wildcard</param>
        /// <returns>A formatted group name string like "P1_S2_V3" or "P1_S2_V*"</returns>
        /// <exception cref="ArgumentException">Thrown if hierarchy is violated (e.g., view ID provided without space ID)</exception>
        public static string View(int? projectId, int? spaceId, int? viewId)
        {
            if (projectId == null && (spaceId.HasValue || viewId.HasValue))
                throw new ArgumentException("Project ID must be provided if space ID or view ID is provided");
            
            if (spaceId == null && viewId.HasValue)
                throw new ArgumentException("Space ID must be provided if view ID is provided");
            
            return $"P{projectId?.ToString() ?? "*"}_S{spaceId?.ToString() ?? "*"}_V{viewId?.ToString() ?? "*"}";
        }
        
        /// <summary>
        /// Creates a group name for a specific view.
        /// </summary>
        /// <param name="v">The view to create a group name for</param>
        /// <returns>A formatted group name string like "P1_S2_V3"</returns>
        public static string SpecificView(ReadOnlyView v) => View(v.Space.Project.Id, v.Space.Id, v.Id);
        
        /// <summary>
        /// Creates a group name for a user with the given ID.
        /// </summary>
        /// <param name="userId">The user ID, or null to use a wildcard</param>
        /// <returns>A formatted group name string like "U1" or "U*"</returns>
        public static string User(int? userId) => $"U{userId?.ToString() ?? "*"}";
        
        /// <summary>
        /// Creates a group name for a specific user.
        /// </summary>
        /// <param name="u">The user to create a group name for</param>
        /// <returns>A formatted group name string like "U1"</returns>
        public static string SpecificUser(ReadOnlyUser u) => User(u.Id);
        
        /// <summary>
        /// Creates a group name for a queue with the given ID.
        /// </summary>
        /// <param name="queueId">The queue ID, or null to use a wildcard</param>
        /// <returns>A formatted group name string like "Q1" or "Q*"</returns>
        public static string Queue(int? queueId) => $"Q{queueId?.ToString() ?? "*"}";
        
        /// <summary>
        /// Creates a group name for a specific queue.
        /// </summary>
        /// <param name="q">The queue to create a group name for</param>
        /// <returns>A formatted group name string like "Q1"</returns>
        public static string SpecificQueue(ReadOnlyJobQueue q) => Queue(q.Id);

        public static string FactoryDefinition(int? projectId, int? spaceId, int? definitionId)
        {
            if (projectId == null && (spaceId.HasValue || definitionId.HasValue))
                throw new ArgumentException("Project ID must be provided if space ID or definition ID is provided");
            if (spaceId == null && definitionId.HasValue)
                throw new ArgumentException("Space ID must be provided if definition ID is provided");
            return $"P{projectId?.ToString() ?? "*"}_S{spaceId?.ToString() ?? "*"}_FD{definitionId?.ToString() ?? "*"}";
        }

        public static string SpecificFactoryDefinition(ReadOnlyFactoryDefinition d, ReadOnlySpace s)
            => FactoryDefinition(s.Project.Id, s.Id, d.Id);

        public static string FactoryInstance(int? projectId, int? spaceId, int? instanceId)
        {
            if (projectId == null && (spaceId.HasValue || instanceId.HasValue))
                throw new ArgumentException("Project ID must be provided if space ID or instance ID is provided");
            if (spaceId == null && instanceId.HasValue)
                throw new ArgumentException("Space ID must be provided if instance ID is provided");
            return $"P{projectId?.ToString() ?? "*"}_S{spaceId?.ToString() ?? "*"}_FI{instanceId?.ToString() ?? "*"}";
        }

        public static string SpecificFactoryInstance(ReadOnlyFactoryInstance i, ReadOnlySpace s)
            => FactoryInstance(s.Project.Id, s.Id, i.Id);

        public static string[] FactoryDefinitionHierarchy(int? projectId, int? spaceId, int? definitionId)
        {
            var list = new List<string>(4);
            if (definitionId.HasValue) list.Add(FactoryDefinition(projectId, spaceId, definitionId));
            list.Add(FactoryDefinition(projectId, spaceId, null));
            list.Add(FactoryDefinition(projectId, null, null));
            list.Add(FactoryDefinition(null, null, null));
            return list.ToArray();
        }

        public static string[] FactoryInstanceHierarchy(int? projectId, int? spaceId, int? instanceId)
        {
            var list = new List<string>(4);
            if (instanceId.HasValue) list.Add(FactoryInstance(projectId, spaceId, instanceId));
            list.Add(FactoryInstance(projectId, spaceId, null));
            list.Add(FactoryInstance(projectId, null, null));
            list.Add(FactoryInstance(null, null, null));
            return list.ToArray();
        }

        public static string[] JobHierarchy(int? projectId, int? spaceId, int? jobId)
        {
            var list = new List<string>(4);
            if (jobId.HasValue) list.Add(Job(projectId, spaceId, jobId));
            list.Add(Job(projectId, spaceId, null));
            list.Add(Job(projectId, null, null));
            list.Add(Job(null, null, null));
            return list.ToArray();
        }

        public static string[] EdgeHierarchy(int? projectId, int? spaceId, int? edgeId)
        {
            var list = new List<string>(4);
            if (edgeId.HasValue) list.Add(Edge(projectId, spaceId, edgeId));
            list.Add(Edge(projectId, spaceId, null));
            list.Add(Edge(projectId, null, null));
            list.Add(Edge(null, null, null));
            return list.ToArray();
        }

        public static string[] ViewHierarchy(int? projectId, int? spaceId, int? viewId)
        {
            var list = new List<string>(4);
            if (viewId.HasValue) list.Add(View(projectId, spaceId, viewId));
            list.Add(View(projectId, spaceId, null));
            list.Add(View(projectId, null, null));
            list.Add(View(null, null, null));
            return list.ToArray();
        }

        public static string[] SpaceHierarchy(int? projectId, int? spaceId)
        {
            var list = new List<string>(3);
            if (spaceId.HasValue) list.Add(Space(projectId, spaceId));
            list.Add(Space(projectId, null));
            list.Add(Space(null, null));
            return list.ToArray();
        }

        public static string[] ProjectHierarchy(int? projectId)
        {
            var list = new List<string>(2);
            if (projectId.HasValue) list.Add(Project(projectId));
            list.Add(Project(null));
            return list.ToArray();
        }

        public static string[] UserHierarchy(int? userId)
        {
            var list = new List<string>(2);
            if (userId.HasValue) list.Add(User(userId));
            list.Add(User(null));
            return list.ToArray();
        }

        public static string[] QueueHierarchy(int? queueId)
        {
            var list = new List<string>(2);
            if (queueId.HasValue) list.Add(Queue(queueId));
            list.Add(Queue(null));
            return list.ToArray();
        }
    }
    
    /// <summary>
    /// Encapsulates the data passed to event handlers when a GroupEvent is invoked.
    /// </summary>
    /// <typeparam name="T">The type of object contained in the event arguments</typeparam>
    /// <param name="obj">The object that triggered the event</param>
    public struct GroupEventArgs<T>(T obj)
    {
        /// <summary>
        /// Gets the object that triggered the event.
        /// </summary>
        public T Object = obj;
    }
}