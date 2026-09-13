using System.Text.RegularExpressions;
using Refund.DataModel;
using Refund.JobExecution;

namespace Refund.JobQueues;

internal static class ClusterSchedulerProtocol
{
    public static string DefaultTerminalStatusTemplate(ClusterScheduler scheduler) => scheduler switch
    {
        ClusterScheduler.Slurm => "sacct -j {{job_id}} --allocations --noheader --parsable2 --format=State",
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

    public static string ParseReceipt(ClusterQueue queue, string output)
    {
        if (queue.IsManaged)
            throw new InvalidOperationException("Managed queues do not parse scheduler receipts.");

        string receipt = queue.SchedulerType switch
        {
            ClusterScheduler.Slurm => Match(output, @"Submitted batch job (\d+)"),
            ClusterScheduler.Lsf => Match(output, @"Job <(\d+)> is submitted"),
            ClusterScheduler.Pbs => Regex.Match(output, @"\b\d+\.[A-Za-z0-9_.-]+\b") is { Success: true } pbs
                ? pbs.Value
                : null,
            ClusterScheduler.Sge => Match(output, @"Your job (\d+)"),
            ClusterScheduler.Flux => ParseBareReceipt(output),
            ClusterScheduler.Custom => string.IsNullOrWhiteSpace(queue.JobIdParseRegex)
                ? null
                : Match(output, queue.JobIdParseRegex),
            _ => null
        };

        return receipt ?? throw new InvalidOperationException(
            $"Could not parse a {queue.SchedulerType} scheduler receipt from: {output}");
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

        var exitStatus = Regex.Match(
            output,
            @"\bExit_status\s*=\s*(-?\d+)\b",
            RegexOptions.IgnoreCase);
        if (exitStatus.Success && int.TryParse(exitStatus.Groups[1].Value, out int exitCode))
            return new BackendObservation(
                exitCode == 0
                    ? BackendObservationKind.Succeeded
                    : BackendObservationKind.Failed,
                $"Scheduler exit status: {exitCode}");

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

        var failedStatus = Regex.Match(
            output,
            @"^\s*failed\s+(-?\d+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (failedStatus.Success &&
            int.TryParse(failedStatus.Groups[1].Value, out int failedCode) &&
            failedCode != 0)
            return new BackendObservation(
                BackendObservationKind.Failed,
                $"Scheduler failure status: {failedCode}");

        var exitStatus = Regex.Match(
            output,
            @"^\s*exit_status\s+(-?\d+)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
        if (exitStatus.Success && int.TryParse(exitStatus.Groups[1].Value, out int exitCode))
            return new BackendObservation(
                exitCode == 0
                    ? BackendObservationKind.Succeeded
                    : BackendObservationKind.Failed,
                $"Scheduler exit status: {exitCode}");

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
        return output.Trim().ToUpperInvariant() switch
        {
            "CANCELED" or "CA" => new BackendObservation(BackendObservationKind.Canceled),
            "FAILED" or "F" or "TIMEOUT" or "TO" =>
                new BackendObservation(BackendObservationKind.Failed),
            "COMPLETED" or "CD" => new BackendObservation(BackendObservationKind.Succeeded),
            "RUN" or "R" or "CLEANUP" or "C" =>
                new BackendObservation(BackendObservationKind.Running),
            "DEPEND" or "D" or "PRIORITY" or "P" or "SCHED" or "S" =>
                new BackendObservation(BackendObservationKind.Pending),
            _ => Unparseable(output)
        };
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

    private static string Match(string output, string pattern)
    {
        var match = Regex.Match(output, pattern);
        return match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : null;
    }

    private static string ParseBareReceipt(string output)
    {
        string receipt = output.Trim();
        return receipt.Length > 0 && !receipt.Any(char.IsWhiteSpace) ? receipt : null;
    }
}
