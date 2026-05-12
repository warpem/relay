using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;
using Refund.Utils;

namespace Refund.DataModel;

/// <summary>
/// A materialized factory definition with real sub-jobs in the space.
/// Can appear in views and folders alongside regular jobs.
/// </summary>
public class FactoryInstance : RelayBase, IFolderContent
{
    private static readonly ConditionalWeakTable<FactoryInstance, ReadOnlyFactoryInstance> ReadOnlyCache = new();

    public Space Space { get; set; }

    [RelayProperty]
    public int Id { get; set; } = -1;

    [RelayProperty]
    public int DefinitionId { get; set; } = -1;

    [RelayProperty]
    public string Alias { get; set; } = "";

    [RelayProperty]
    public string ColorTag { get; set; } = "";

    [RelayProperty]
    public string Notes { get; set; } = "";

    [RelayProperty]
    public DateTime UpdateDate { get; set; }

    public User UpdatedBy { get; set; }

    public List<int> SubJobIds { get; set; } = new();

    public DiagramLayout? DiagramLayout { get; set; }

    public List<JobEvent> Events { get; set; } = new();

    public string QualifiedName => string.IsNullOrWhiteSpace(Alias)
        ? $"FI{Id}"
        : $"FI{Id}: {Alias}";

    /// <summary>
    /// Resolves the factory definition from the space.
    /// </summary>
    public FactoryDefinition Definition => Space?.FindFactoryDefinition(DefinitionId);

    /// <summary>
    /// Resolves sub-jobs from the space. Returns empty if Space is not set.
    /// </summary>
    public IReadOnlyList<Job> SubJobs =>
        Space != null
            ? SubJobIds.Select(id => Space.FindJob(id)).Where(j => j != null).ToList()
            : Array.Empty<Job>();

    /// <summary>
    /// Computes aggregate status from sub-job statuses (worst wins).
    /// Priority: Failed > Aborting > Aborted > Running > Finalizing > Staging > Waiting > Clearing > Building > Finished
    /// Returns Building when there are no sub-jobs or Space is not set.
    /// </summary>
    public JobStatus AggregateStatus
    {
        get
        {
            if (SubJobIds.Count == 0 || Space == null)
                return JobStatus.Building;

            var worst = JobStatus.Finished;
            int worstPriority = 0;

            foreach (var id in SubJobIds)
            {
                var job = Space.FindJob(id);
                if (job != null)
                {
                    int priority = StatusPriority(job.Status);
                    if (priority > worstPriority)
                    {
                        worst = job.Status;
                        worstPriority = priority;
                    }
                }
            }

            // If no sub-job IDs resolved to actual jobs, treat as Building
            if (worstPriority == 0)
                return JobStatus.Building;

            return worst;
        }
    }

    private static int StatusPriority(JobStatus s) => s switch
    {
        JobStatus.Deleted    => 11,
        JobStatus.Failed     => 10,
        JobStatus.Aborting   => 9,
        JobStatus.Aborted    => 8,
        JobStatus.Running    => 7,
        JobStatus.Finalizing => 6,
        JobStatus.Staging    => 5,
        JobStatus.Waiting    => 4,
        JobStatus.Clearing   => 3,
        JobStatus.Building   => 2,
        JobStatus.Finished   => 1,
        _                    => 0
    };

    public void UpdateDiagramLayout(Space space)
    {
        DiagramLayout = DiagramLayoutComputer.ComputeLayout(this, space, DiagramLayout);
    }

    public void ResetDiagramLayout(Space space)
    {
        DiagramLayout = DiagramLayoutComputer.ComputeLayout(this, space, null);
    }

    public void AddEvent(EventType type, User author = null)
    {
        Events.Add(new JobEvent(type, DateTime.Now, author));
    }

    public ReadOnlyFactoryInstance AsReadOnly()
    {
        return ReadOnlyCache.GetValue(this, inst => new ReadOnlyFactoryInstance(inst));
    }

    public override void WriteToJson(JsonNode writer)
    {
        base.WriteToJson(writer);

        writer["UpdatedBy"] = UpdatedBy?.Id;

        writer["SubJobIds"] = new JsonArray(
            SubJobIds.Select(id => JsonValue.Create(id)).ToArray<JsonNode>());

        if (DiagramLayout != null)
            writer["DiagramLayout"] = FactoryDefinition.SerializeDiagramLayout(DiagramLayout);

        writer["Events"] = new JsonArray(Events.Select(e =>
        {
            var eventNode = new JsonObject
            {
                ["Type"] = e.Type.ToString(),
                ["Timestamp"] = e.Timestamp.ToString("s", System.Globalization.CultureInfo.InvariantCulture)
            };
            if (e.Author != null)
                eventNode["Author"] = e.Author.Id;
            return (JsonNode)eventNode;
        }).ToArray());
    }

    /// <summary>
    /// Deserializes without user resolution (for standalone tests).
    /// </summary>
    public override void ReadFromJson(JsonNode reader)
    {
        base.ReadFromJson(reader);
        ReadCollectionsFromJson(reader, null);
    }

    /// <summary>
    /// Deserializes with user resolution.
    /// </summary>
    public void ReadFromJson(JsonNode reader, ReadOnlyCollection<User> users)
    {
        base.ReadFromJson(reader);
        ReadCollectionsFromJson(reader, users);
    }

    private void ReadCollectionsFromJson(JsonNode reader, ReadOnlyCollection<User> users)
    {
        if (reader["UpdatedBy"] != null && users != null)
            UpdatedBy = users.FirstOrDefault(u => u.Id == reader["UpdatedBy"].Deserialize<int>());

        SubJobIds.Clear();
        if (reader["SubJobIds"] != null)
            SubJobIds.AddRange(reader["SubJobIds"].Deserialize<int[]>());

        if (reader["DiagramLayout"] is JsonObject layoutJson)
            DiagramLayout = FactoryDefinition.DeserializeDiagramLayout(layoutJson);
        else
            DiagramLayout = null;

        Events.Clear();
        if (reader["Events"] is JsonArray eventsArray)
        {
            foreach (var en in eventsArray)
            {
                var type = Enum.Parse<EventType>(en["Type"]?.GetValue<string>() ?? "Created");
                var timestamp = en["Timestamp"] != null
                    ? DateTime.ParseExact(en["Timestamp"].GetValue<string>(), "s", System.Globalization.CultureInfo.InvariantCulture)
                    : DateTime.MinValue;
                User author = null;
                if (en["Author"] != null && users != null)
                    author = users.FirstOrDefault(u => u.Id == en["Author"].GetValue<int>());
                Events.Add(new JobEvent(type, timestamp, author));
            }
        }
    }
}
