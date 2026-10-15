using Eryph.ComputeClient.Models;
using NSubstitute;
using static Eryph.Mcp.Tools.Tests.FakeEryph;

namespace Eryph.Mcp.Tools.Tests;

public class ProvisioningToolsTests
{
    private readonly FakeEryph _eryph = new();
    private readonly ProvisioningTools _tools;

    public ProvisioningToolsTests()
    {
        _tools = new ProvisioningTools(_eryph.Api, _eryph.Waiter, _eryph.Options);
        SetCatletStatus(CatletStatus.Running);
        _eryph.Catlets.GetGuestServicesAsync("c-1", Arg.Any<CancellationToken>()).Returns(_ => Started("gs"));
        _eryph.Catlets.GetProvisioningLogAsync("c-1", Arg.Any<CancellationToken>()).Returns(_ => Started("log"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void SetCatletStatus(CatletStatus status) =>
        _eryph.Catlets.GetAsync("c-1", Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Ok(Catlet("c-1", "one", status))));

    private static Operation Status(string? guestServices, string? provisioning) =>
        Op("gs", OperationStatus.Completed, result: EryphComputeClientModelFactory.GuestServicesStatusOperationResult(
            guestServicesStatus: guestServices,
            guestServicesVersion: "0.7.0",
            provisioningState: provisioning,
            shell: "/bin/bash"));

    private void SetupLog(params ProvisioningLogEntry[] events) =>
        _eryph.SetupOperation("log", Op("log", OperationStatus.Completed,
            result: EryphComputeClientModelFactory.ProvisioningLogOperationResult("rendered", events)));

    [Fact]
    public async Task Wait_returns_immediately_for_a_stopped_catlet()
    {
        SetCatletStatus(CatletStatus.Stopped);

        var result = await _tools.WaitForProvisioning("c-1", cancellation: Ct);

        Assert.Equal("stopped", result.State);
        Assert.Contains("start_catlet", result.Hint);
        await _eryph.Catlets.DidNotReceive().GetGuestServicesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Wait_polls_until_provisioning_completed()
    {
        _eryph.SetupOperation("gs",
            Status(null, null),
            Status("available", "running"),
            Status("available", "completed"));

        var result = await _tools.WaitForProvisioning("c-1", cancellation: Ct);

        Assert.True(result.Finished);
        Assert.Equal("completed", result.State);
        Assert.Equal("available", result.GuestServicesStatus);
    }

    [Fact]
    public async Task Wait_returns_the_failed_steps_when_provisioning_failed()
    {
        _eryph.SetupOperation("gs", Status("available", "failed"));
        SetupLog(
            EryphComputeClientModelFactory.ProvisioningLogEntry("config-ssh", "finish", "SUCCESS", "ok"),
            EryphComputeClientModelFactory.ProvisioningLogEntry("config-scripts_user", "finish", "FAIL", "exit 3"));

        var result = await _tools.WaitForProvisioning("c-1", cancellation: Ct);

        Assert.Equal("failed", result.State);
        var step = Assert.Single(result.FailedSteps);
        Assert.Contains("config-scripts_user [FAIL] exit 3", step);
    }

    [Fact]
    public async Task Wait_hints_at_a_fodder_failure_when_guest_services_never_report()
    {
        _eryph.SetupOperation("gs", Status(null, null));

        var result = await _tools.WaitForProvisioning("c-1", timeoutSeconds: 0, cancellation: Ct);

        Assert.False(result.Finished);
        Assert.Contains("fodder", result.Hint);
    }

    [Fact]
    public async Task Log_renders_one_line_per_event()
    {
        SetupLog(
            EryphComputeClientModelFactory.ProvisioningLogEntry("a", "start", null, "first"),
            EryphComputeClientModelFactory.ProvisioningLogEntry("a", "finish", "SUCCESS", "second"));

        var log = await _tools.GetProvisioningLog("c-1", Ct);

        Assert.Equal(2, log.Log!.Split('\n').Length);
        Assert.Contains("finish a [SUCCESS] second", log.Log);
        Assert.False(log.Truncated);
    }

    [Fact]
    public async Task Log_keeps_the_end_of_an_oversized_log()
    {
        SetupLog(Enumerable.Range(0, 2_000)
            .Select(i => EryphComputeClientModelFactory.ProvisioningLogEntry($"step{i}", "finish", "SUCCESS", new string('x', 50)))
            .ToArray());

        var log = await _tools.GetProvisioningLog("c-1", Ct);

        Assert.True(log.Truncated);
        Assert.Contains("step1999", log.Log);
        Assert.DoesNotContain("step0 ", log.Log);
    }
}
