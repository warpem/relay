using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Refund.DataModel.ReadOnly;

namespace Refund.DataModel
{
    /// <summary>
    /// Abstract base class for job execution queues.
    /// JobQueue implementations manage the submission, monitoring, and control of jobs 
    /// to various execution environments (local machine, compute cluster, etc.).
    /// </summary>
    public abstract class JobQueue : RelayBase
    {
        /// <summary>
        /// Cache of read-only wrappers for job queues, using weak references to avoid memory leaks.
        /// </summary>
        private static readonly ConditionalWeakTable<JobQueue, ReadOnlyJobQueue> ReadOnlyCache = new();
        
        /// <summary>
        /// Unique identifier for this queue.
        /// </summary>
        [RelayProperty]
        public int Id { get; set; } = -1;

        /// <summary>
        /// User-defined name for the queue.
        /// This provides a human-readable identifier for the queue.
        /// </summary>
        [RelayProperty]
        public string Alias { get; set; }
        
        /// <summary>
        /// Gets a fully qualified name for the queue, including its ID and alias.
        /// This is used in places where a unique, human-readable identifier is needed.
        /// </summary>
        public string QualifiedName => $"Q{Id}: {Alias}";

        /// <summary>
        /// The type of queue, indicating what kinds of jobs it can process (local, CPU, GPU, etc.).
        /// </summary>
        [RelayProperty]
        public JobQueueType QueueType { get; set; }

        /// <summary>
        /// Internal list of jobs currently in the queue.
        /// </summary>
        private readonly List<Job> _QueuedJobs = new();
        
        /// <summary>
        /// Read-only view of jobs currently in the queue.
        /// </summary>
        public ReadOnlyCollection<Job> QueuedJobs => _QueuedJobs.AsReadOnly();
        
        /// <summary>
        /// Indicates whether the queue is empty (contains no jobs).
        /// </summary>
        public bool IsEmpty => _QueuedJobs.Count == 0;

        /// <summary>
        /// Callback function used to update job state and notify subscribers.
        /// The callback takes a job to update and an action to perform on that job.
        /// </summary>
        protected readonly Action<Job, Action<Job>> JobUpdateCallback;

        /// <summary>
        /// Creates a new JobQueue with the specified job update callback.
        /// </summary>
        /// <param name="jobUpdateCallback">Callback function used to update job state and notify subscribers</param>
        protected JobQueue(Action<Job, Action<Job>> jobUpdateCallback)
        {
            JobUpdateCallback = jobUpdateCallback;
        }
        
        /// <summary>
        /// Returns a read-only wrapper for this job queue.
        /// The read-only wrapper provides a safe view that prevents accidental modification.
        /// The same wrapper instance is reused for each queue to minimize object creation.
        /// </summary>
        /// <returns>A read-only wrapper for this job queue</returns>
        public virtual ReadOnlyJobQueue AsReadOnly()
        {
            return ReadOnlyCache.GetValue(this, queue => new ReadOnlyJobQueue(queue));
        }

        /// <summary>
        /// Adds a job to this queue.
        /// The job will be scheduled for execution according to the queue's policies.
        /// </summary>
        /// <param name="job">The job to enqueue</param>
        public void Enqueue(Job job)
        {
            _QueuedJobs.Add(job);
        }

        /// <summary>
        /// Removes a job from this queue.
        /// This can be used to cancel jobs that haven't started yet.
        /// </summary>
        /// <param name="job">The job to dequeue</param>
        public void Dequeue(Job job)
        {
            _QueuedJobs.Remove(job);
        }

        /// <summary>
        /// Deserializes this job queue from a JSON node.
        /// This loads the queue's properties and the list of jobs it contains.
        /// </summary>
        /// <param name="reader">The JSON node to read from</param>
        /// <param name="findJob">Function to find jobs by project, space, and job ID</param>
        public virtual void ReadFromJson(JsonNode reader, Func<int, int, int, Job> findJob)
        {
            base.ReadFromJson(reader);

            string[] jobs = reader["Jobs"].Deserialize<string[]>();
            _QueuedJobs.Clear();

            foreach (string address in jobs)
            {
                string[] parts = address.Split('.');
                int projectId = int.Parse(parts[0]);
                int spaceId = int.Parse(parts[1]);
                int jobId = int.Parse(parts[2]);

                Job job = findJob(projectId, spaceId, jobId);

                if (job != null && (job.Status.IsUnsettled() || job.Status == JobStatus.Waiting))
                    _QueuedJobs.Add(job);
            }
        }

        /// <summary>
        /// Serializes this job queue to a JSON node.
        /// This saves the queue's properties and the list of jobs it contains.
        /// Jobs with unsettled or waiting status are included in the serialization
        /// so that queue assignments survive application restarts.
        /// </summary>
        /// <param name="writer">The JSON node to write to</param>
        public override void WriteToJson(JsonNode writer)
        {
            base.WriteToJson(writer);

            writer["Jobs"] = new JsonArray(_QueuedJobs.Where(j => j.Status.IsUnsettled() || j.Status == JobStatus.Waiting)
                                                      .Select(j => JsonValue.Create($"{j.Space.Project.Id}.{j.Space.Id}.{j.Id}")).ToArray<JsonNode>());
        }

        /// <summary>
        /// Submits a job to this queue for execution.
        /// This is a virtual method that queue implementations should override to handle actual job submission.
        /// </summary>
        /// <param name="job">The job to submit</param>
        /// <param name="customValues">Optional dictionary of custom values to include in the job submission</param>
        public virtual void SubmitJob(Job job, Dictionary<string, string> customValues = null) {  }
        
        /// <summary>
        /// Aborts a running job.
        /// This is a virtual method that queue implementations should override to handle job abortion.
        /// </summary>
        /// <param name="job">The job to abort</param>
        public virtual void AbortJob(Job job) {  }

        /// <summary>
        /// Checks the status of a job on the execution platform.
        /// This is a virtual method that queue implementations should override to query job status.
        /// </summary>
        /// <param name="job">The job to check</param>
        /// <returns>The current status of the job on the execution platform</returns>
        public virtual async Task<(ClusterJobStatus status, string output)> CheckStatus(Job job) => (ClusterJobStatus.Unknown, "");

        /// <summary>
        /// Whether this queue can start <paramref name="job"/> right now.
        /// Queues backed by an external scheduler always admit — the scheduler does the arbitration.
        /// </summary>
        public virtual AdmissionResult CanAdmit(Job job) => AdmissionResult.Admitted;

        /// <summary>
        /// Clears all jobs from this queue.
        /// This removes all jobs from the queue but does not affect their execution status.
        /// </summary>
        public void Clear()
        {
            _QueuedJobs.Clear();
        }
    }

    /// <summary>
    /// Defines the types of job queues available in the system.
    /// This is a flags enum, so values can be combined to represent queues that support multiple types.
    /// </summary>
    [Flags]
    public enum JobQueueType
    {
        /// <summary>
        /// Queue for jobs that run on the local machine.
        /// </summary>
        Local = 1 << 0,
        
        /// <summary>
        /// Queue for jobs that run on CPU nodes in a cluster.
        /// </summary>
        CPU = 1 << 1,
        
        /// <summary>
        /// Queue for jobs that run on GPU nodes in a cluster.
        /// </summary>
        GPU = 1 << 2,
        
        /// <summary>
        /// Queue for jobs that run on both CPU and GPU nodes in a cluster.
        /// </summary>
        Mixed = (1 << 1) | (1 << 2)
    }

    /// <summary>
    /// Identifies which cluster scheduler a <see cref="Refund.JobQueues.ClusterQueue"/> talks to,
    /// selecting the parsers used to read job IDs and job states out of scheduler output.
    /// </summary>
    /// <remarks>
    /// Slurm is deliberately value 0: queues saved before this field existed deserialize to the
    /// default, and every one of them was already being parsed with the SLURM patterns (the SLURM
    /// status parser returned Unknown for unrecognised output, which short-circuited the
    /// try-each-parser-in-turn loop before any other parser was reached). Defaulting to Slurm
    /// therefore preserves existing behaviour exactly.
    ///
    /// The consequence for a pre-existing non-SLURM queue is that its scheduler must now be
    /// selected explicitly — job ID parsing no longer falls through to the other schedulers.
    /// </remarks>
    public enum ClusterScheduler
    {
        /// <summary>SLURM: sbatch / squeue / scancel.</summary>
        Slurm = 0,

        /// <summary>IBM Spectrum LSF: bsub / bjobs / bkill.</summary>
        Lsf = 1,

        /// <summary>PBS / Torque: qsub / qstat / qdel.</summary>
        Pbs = 2,

        /// <summary>Sun Grid Engine and derivatives: qsub / qstat / qdel.</summary>
        Sge = 3,

        /// <summary>Flux: flux batch / flux jobs / flux cancel.</summary>
        Flux = 4,

        /// <summary>
        /// Anything else. Job IDs are read with <see cref="Refund.JobQueues.ClusterQueue.JobIdParseRegex"/>
        /// and states with the JobStatusParseTemplate* patterns.
        /// </summary>
        Custom = 5
    }

    /// <summary>
    /// The outcome of asking a queue whether it can start a job right now.
    /// </summary>
    /// <remarks>
    /// A boolean cannot express the difference that matters. "No, resources are busy" must leave the
    /// job Waiting so the daemon retries; "no, this can never run here" must fail it once, with a
    /// reason. Throwing is not an alternative — HandleWaitingState's catch logs and returns without
    /// changing job status, so an exception would leave the job Waiting and re-log every tick.
    /// </remarks>
    public abstract record AdmissionResult
    {
        private AdmissionResult() { }

        /// <summary>Start the job now.</summary>
        public sealed record Admit : AdmissionResult;

        /// <summary>Resources are in use. Leave the job Waiting; the daemon will ask again.</summary>
        public sealed record Busy : AdmissionResult;

        /// <summary>The job can never run on this queue. Fail it once with this reason.</summary>
        public sealed record Reject(string Reason) : AdmissionResult;

        /// <summary>Shared instance — returned for every waiting job on every tick.</summary>
        public static readonly AdmissionResult Admitted = new Admit();

        /// <summary>Shared instance — returned for every waiting job on every tick.</summary>
        public static readonly AdmissionResult IsBusy = new Busy();
    }

    /// <summary>
    /// Defines the possible status values for jobs running on a cluster.
    /// This is used to track the state of jobs on the cluster scheduler.
    /// </summary>
    public enum ClusterJobStatus
    {
        /// <summary>
        /// Job status is unknown or unable to be determined.
        /// </summary>
        Unknown = 0,
        
        /// <summary>
        /// Job is pending execution, waiting for resources.
        /// </summary>
        Pending = 1,
        
        /// <summary>
        /// Job is currently running on the cluster.
        /// </summary>
        Running = 2,
        
        /// <summary>
        /// Job has finished successfully.
        /// </summary>
        Finished = 3,
        
        /// <summary>
        /// Job has failed or was terminated with an error.
        /// </summary>
        Failed = 4
    }
}
