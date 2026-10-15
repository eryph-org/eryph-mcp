using System.ComponentModel;
using Eryph.ComputeClient.Models;
using ModelContextProtocol.Server;

namespace Eryph.Mcp.Tools;

public sealed record ProvisioningResult(
    string CatletId,
    string State,
    string? GuestServicesStatus,
    string? GuestServicesVersion,
    string? Shell,
    bool Finished,
    string? Hint,
    IReadOnlyList<string> FailedSteps);

public sealed record ProvisioningLog(
    string CatletId,
    string? Log,
    bool Truncated,
    IReadOnlyList<string> FailedSteps);

[McpServerToolType]
public sealed class ProvisioningTools(IEryphApi api, OperationWaiter waiter, EryphMcpOptions options)
{
    private const int MaxLogChars = 60_000;
    private static readonly TimeSpan StatusBudget = TimeSpan.FromSeconds(60);

    [McpServerTool(Name = "wait_for_provisioning", ReadOnly = true)]
    [Description("""
        Waits until the guest of a started catlet has finished provisioning (all fodder applied).
        Guest services reporting 'available' is not enough - this waits for the provisioning
        state 'completed' (or 'failed'). On failure the failed provisioning steps are returned;
        use get_provisioning_log for the full log. If provisioning never starts or hangs, an
        earlier fodder item most likely failed: simplify the fodder instead of debugging the network.
        """)]
    public Task<ProvisioningResult> WaitForProvisioning(
        [Description("The catlet id.")] string catletId,
        [Description("Seconds to wait (default 900).")] int timeoutSeconds = 900,
        CancellationToken cancellation = default) =>
        ToolErrors.Guard(async () =>
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 1));
            GuestServicesStatusOperationResult? status = null;
            while (true)
            {
                var catlet = (await api.Catlets().GetAsync(catletId, cancellation)).Value;
                if (catlet.Status == CatletStatus.Stopped)
                    return new ProvisioningResult(catletId, "stopped", null, null, null, true,
                        "The catlet is stopped. Call start_catlet first.", []);

                status = await TryGetStatusAsync(catletId, cancellation) ?? status;
                var state = status?.ProvisioningState;

                if (state == "completed")
                    return ToResult(catletId, status, true, null, []);

                if (state == "failed")
                    return ToResult(catletId, status, true,
                        "Provisioning failed. Inspect the failed steps / get_provisioning_log and fix the fodder.",
                        await GetFailedStepsAsync(catletId, cancellation));

                if (DateTimeOffset.UtcNow >= deadline)
                    return ToResult(catletId, status, false,
                        status?.GuestServicesStatus is null
                            ? "Guest services never reported. Either the guest is still booting or an early "
                              + "fodder/cloud-init step failed and blocked guest services."
                            : "Provisioning is still running. Call wait_for_provisioning again or check get_provisioning_log.",
                        []);

                await Task.Delay(options.ProvisioningPollInterval, cancellation);
            }
        });

    [McpServerTool(Name = "get_provisioning_log", ReadOnly = true, Idempotent = true)]
    [Description("Gets the provisioning log of a catlet guest (fodder / cloud-init execution), including failed steps.")]
    public Task<ProvisioningLog> GetProvisioningLog(
        [Description("The catlet id.")] string catletId,
        CancellationToken cancellation = default) =>
        ToolErrors.Guard(async () =>
        {
            var result = await GetLogAsync(catletId, cancellation);

            // The rendered log has no line separators; render one line per event.
            var log = result.Events is { Count: > 0 } events
                ? string.Join('\n', events.Select(e =>
                    $"{e.Timestamp:HH:mm:ss} {e.Type} {e.Name}{(e.Result is null ? "" : $" [{e.Result}]")} {e.Message}"))
                : result.RenderedLog;
            var truncated = log is { Length: > MaxLogChars };
            if (truncated)
                log = log![^MaxLogChars..];

            return new ProvisioningLog(catletId, log, truncated, FailedSteps(result));
        });

    private async Task<GuestServicesStatusOperationResult?> TryGetStatusAsync(
        string catletId,
        CancellationToken cancellation)
    {
        // The status is read live from the guest's KVP pool through an operation.
        // While the guest is still booting this fails; that is not an error here.
        try
        {
            var started = await api.Catlets().GetGuestServicesAsync(catletId, cancellation);
            var operation = await waiter.WaitAsync(started.Value.Id, StatusBudget, cancellation);
            return operation.Result as GuestServicesStatusOperationResult;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status != 404)
        {
            return null;
        }
    }

    private async Task<ProvisioningLogOperationResult> GetLogAsync(string catletId, CancellationToken cancellation)
    {
        var started = await api.Catlets().GetProvisioningLogAsync(catletId, cancellation);
        var operation = await waiter.WaitAsync(started.Value.Id, StatusBudget, cancellation);
        OperationWaiter.EnsureSucceeded(operation, "Reading the provisioning log");
        return operation.Result as ProvisioningLogOperationResult
            ?? throw new ToolException("The provisioning log is not available.", ErrorCategory.State);
    }

    private async Task<IReadOnlyList<string>> GetFailedStepsAsync(string catletId, CancellationToken cancellation)
    {
        try
        {
            return FailedSteps(await GetLogAsync(catletId, cancellation));
        }
        catch (ToolException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> FailedSteps(ProvisioningLogOperationResult result) =>
        (result.Events ?? [])
            .Where(e => e.Result is not null
                        && !e.Result.Equals("success", StringComparison.OrdinalIgnoreCase)
                        && !e.Result.Equals("succeeded", StringComparison.OrdinalIgnoreCase))
            .Select(e => $"{e.Timestamp:HH:mm:ss} {e.Name} [{e.Result}] {e.Message}")
            .ToList();

    private static ProvisioningResult ToResult(
        string catletId,
        GuestServicesStatusOperationResult? status,
        bool finished,
        string? hint,
        IReadOnlyList<string> failedSteps) => new(
        catletId,
        status?.ProvisioningState ?? "unknown",
        status?.GuestServicesStatus,
        status?.GuestServicesVersion,
        status?.Shell,
        finished,
        hint,
        failedSteps);
}
