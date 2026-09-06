using System.Text.RegularExpressions;
using Refund.DataModel;
using Refund.JobExecution;

namespace Refund.JobQueues;

internal static class ClusterSchedulerProtocol
{
    public static string DefaultTerminalStatusTemplate(ClusterScheduler scheduler) => scheduler switch
    {
        ClusterScheduler.Slurm => "sacct -j {{job_id}} --noheader --parsable2 --format=State",
        _ => null
    };

    public static BackendObservation ParseObservation(ClusterQueue queue, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return new BackendObservation(
                BackendObservationKind.AbsentFromActiveView,
                "The job is absent from the scheduler response.");

        return queue.SchedulerType switch
        {
            ClusterScheduler.Slurm => ParseSlurm(output),
            ClusterScheduler.Lsf => ParseLsf(output),
            ClusterScheduler.Pbs => ParsePbs(output),
            ClusterScheduler.Sge => ParseSge(output),
            ClusterScheduler.Flux => ParseFlux(output),
            ClusterScheduler.Custom => ParseCustom(queue, output),
            ClusterScheduler.Managed => new BackendObservation(
                BackendObservationKind.Unparseable,
                "Managed execution has no scheduler protocol."),
            _ => Unparseable(output)
        };
    }

    private static BackendObservation ParseSlurm(string output)
    {
        var states = Tokens(output)
            .Select(token => token.TrimEnd('+'))
            .ToArray();

        if (states.Any(state => state is "CANCELLED" or "CA"))
            return new BackendObservation(BackendObservationKind.Canceled);
        if (states.Any(state => state is
                "BOOT_FAIL" or "DEADLINE" or "FAILED" or "F" or "NODE_FAIL" or
                "OUT_OF_MEMORY" or "PREEMPTED" or "REVOKED" or "SPECIAL_EXIT" or
                "TIMEOUT" or "TO"))
            return new BackendObservation(BackendObservationKind.Failed, string.Join(", ", states));
        if (states.Length > 0 && states.All(state => state is "COMPLETED" or "CD"))
            return new BackendObservation(BackendObservationKind.Succeeded);
        if (states.Any(state => state is "RUNNING" or "R" or "COMPLETING" or "CG" or "STAGE_OUT"))
            return new BackendObservation(BackendObservationKind.Running);
        if (states.Any(state => state is
                "PENDING" or "PD" or "CONFIGURING" or "CF" or "REQUEUED" or
                "RESIZING" or "SUSPENDED"))
            return new BackendObservation(BackendObservationKind.Pending);

        return Unparseable(output);
    }

    private static BackendObservation ParseLsf(string output)
    {
        var states = Tokens(output).ToArray();

        if (states.Any(state => state is "DONE"))
            return new BackendObservation(BackendObservationKind.Succeeded);
        if (states.Any(state => state is "EXIT" or "ZOMBI"))
            return new BackendObservation(BackendObservationKind.Failed);
        if (states.Any(state => state is "RUN"))
            return new BackendObservation(BackendObservationKind.Running);
        if (states.Any(state => state is "PEND" or "WAIT" or "PROV"))
            return new BackendObservation(BackendObservationKind.Pending);

        return Unparseable(output);
    }

    private static BackendObservation ParsePbs(string output)
    {
        var states = Tokens(output).ToArray();

        if (states.Any(state => state is "COMPLETED" or "SUCCEEDED"))
            return new BackendObservation(BackendObservationKind.Succeeded);
        if (states.Any(state => state is "FAILED" or "EXITING"))
            return new BackendObservation(BackendObservationKind.Failed);
        if (states.Any(state => state is "R" or "E"))
            return new BackendObservation(BackendObservationKind.Running);
        if (states.Any(state => state is "Q" or "H" or "W" or "T"))
            return new BackendObservation(BackendObservationKind.Pending);

        return Unparseable(output);
    }

    private static BackendObservation ParseSge(string output)
    {
        var states = Tokens(output).ToArray();

        if (states.Any(state => state is "DONE" or "COMPLETED" or "SUCCEEDED"))
            return new BackendObservation(BackendObservationKind.Succeeded);
        if (states.Any(state => state is "EQW" or "FAILED"))
            return new BackendObservation(BackendObservationKind.Failed);
        if (states.Any(state => state is "R" or "T"))
            return new BackendObservation(BackendObservationKind.Running);
        if (states.Any(state => state is "QW" or "H" or "S"))
            return new BackendObservation(BackendObservationKind.Pending);

        return Unparseable(output);
    }

    private static BackendObservation ParseFlux(string output)
    {
        var states = Tokens(output).ToArray();

        if (states.Any(state => state is "CANCELED" or "CA"))
            return new BackendObservation(BackendObservationKind.Canceled);
        if (states.Any(state => state is "FAILED" or "F" or "TIMEOUT" or "TO"))
            return new BackendObservation(BackendObservationKind.Failed);
        if (states.Length > 0 && states.All(state => state is "COMPLETED" or "CD"))
            return new BackendObservation(BackendObservationKind.Succeeded);
        if (states.Any(state => state is "RUN" or "R" or "CLEANUP" or "C"))
            return new BackendObservation(BackendObservationKind.Running);
        if (states.Any(state => state is "DEPEND" or "D" or "PRIORITY" or "P" or "SCHED" or "S"))
            return new BackendObservation(BackendObservationKind.Pending);

        return Unparseable(output);
    }

    private static BackendObservation ParseCustom(ClusterQueue queue, string output)
    {
        if (Matches(output, queue.JobStatusParseTemplateCanceled))
            return new BackendObservation(BackendObservationKind.Canceled);
        if (Matches(output, queue.JobStatusParseTemplateFailed))
            return new BackendObservation(BackendObservationKind.Failed);
        if (Matches(output, queue.JobStatusParseTemplateSucceeded))
            return new BackendObservation(BackendObservationKind.Succeeded);
        if (Matches(output, queue.JobStatusParseTemplateRunning))
            return new BackendObservation(BackendObservationKind.Running);
        if (Matches(output, queue.JobStatusParseTemplatePending))
            return new BackendObservation(BackendObservationKind.Pending);

        return Unparseable(output);
    }

    private static IEnumerable<string> Tokens(string output) =>
        Regex.Split(output.ToUpperInvariant(), @"[^A-Z0-9_]+")
            .Where(token => token.Length > 0);

    private static bool Matches(string output, string pattern) =>
        !string.IsNullOrWhiteSpace(pattern) &&
        output.Contains(pattern, StringComparison.OrdinalIgnoreCase);

    private static BackendObservation Unparseable(string output) => new(
        BackendObservationKind.Unparseable,
        $"The scheduler response did not contain a recognized state: {output.Trim()}");
}
