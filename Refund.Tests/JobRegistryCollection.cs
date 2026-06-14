namespace Refund.Tests;

// Job.PopulateStatic() mutates process-wide static dictionaries on Job and is not
// idempotent. Test classes that populate the job-type registry share this collection so
// xUnit runs them serially rather than in parallel, preventing concurrent population races.
[CollectionDefinition("JobRegistry", DisableParallelization = true)]
public class JobRegistryCollection { }
