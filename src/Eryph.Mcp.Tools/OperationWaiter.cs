using Eryph.ComputeClient.Models;

namespace Eryph.Mcp.Tools;

public sealed record OperationSummary(
    string OperationId,
    string Status,
    string? StatusMessage,
    bool Finished,
    IReadOnlyList<string> RecentLog,
    IReadOnlyList<ResourceRef> Resources);

public sealed record ResourceRef(string Type, string Id);

// eryph's API never blocks: mutating calls return an operation that has to be
// polled. The waiter polls until the operation is terminal or the wait budget is
// used up; in the latter case the caller returns the operation id so the model
// can continue with get_operation instead of the tool call timing out.
public sealed class OperationWaiter(IEryphApi api, EryphMcpOptions options)
{
    private const int LogLines = 25;

    public async Task<Operation> WaitAsync(string operationId, TimeSpan budget, CancellationToken cancellation)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        var operations = api.Operations();
        while (true)
        {
            Operation operation;
            try
            {
                operation = (await operations.GetAsync(
                    operationId, expand: "logs,resources", cancellationToken: cancellation)).Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && DateTimeOffset.UtcNow < deadline)
            {
                // A transient poll failure must not abort a long-running wait.
                await Task.Delay(options.OperationPollInterval, cancellation);
                continue;
            }

            if (IsTerminal(operation) || DateTimeOffset.UtcNow >= deadline)
                return operation;

            await Task.Delay(options.OperationPollInterval, cancellation);
        }
    }

    public static bool IsTerminal(Operation operation) =>
        operation.Status == OperationStatus.Completed
        || operation.Status == OperationStatus.Failed
        || operation.Status == OperationStatus.Cancelled;

    public static OperationSummary Summarize(Operation operation) => new(
        operation.Id,
        operation.Status.ToString(),
        CleanMessage(operation.StatusMessage),
        IsTerminal(operation),
        (operation.LogEntries ?? [])
            .OrderBy(e => e.Timestamp)
            .TakeLast(LogLines)
            .Select(e => $"{e.Timestamp:HH:mm:ss} {e.Message}")
            .ToList(),
        (operation.Resources ?? [])
            .Select(r => new ResourceRef(r.ResourceType.ToString(), r.ResourceId))
            .ToList());

    // eryph status messages of failed operations can carry a full .NET stack
    // trace. It is noise for the model, so only the message part is kept.
    public static string? CleanMessage(string? message)
    {
        if (message is null)
            return null;

        var lines = message.ReplaceLineEndings("\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("at ", StringComparison.Ordinal)
                        && !l.Contains("--- End of stack trace", StringComparison.Ordinal));
        return string.Join('\n', lines).Trim();
    }

    // Throws a tool error for a failed or cancelled operation. Operation failures
    // are reported as content errors: they are almost always caused by the catlet
    // definition (unknown gene, invalid fodder, bad variable) rather than state.
    public static void EnsureSucceeded(Operation operation, string what)
    {
        if (operation.Status == OperationStatus.Completed)
            return;

        if (!IsTerminal(operation))
            throw new ToolException(
                $"{what} is still running (operation {operation.Id}). Continue with get_operation.",
                ErrorCategory.Transient);

        var log = Summarize(operation).RecentLog;
        throw new ToolException(
            log.Count == 0
                ? $"{what} failed: {CleanMessage(operation.StatusMessage)}"
                : $"{what} failed: {CleanMessage(operation.StatusMessage)}\nRecent operation log:\n{string.Join('\n', log)}",
            ErrorCategory.Content);
    }
}
