using Azure;
using Eryph.ComputeClient.Models;
using NSubstitute;
using static Eryph.Mcp.Tools.Tests.FakeEryph;

namespace Eryph.Mcp.Tools.Tests;

public class OperationWaiterTests
{
    private readonly FakeEryph _eryph = new();

    [Fact]
    public async Task WaitAsync_polls_until_the_operation_is_terminal()
    {
        _eryph.SetupOperation("op",
            Op("op", OperationStatus.Queued),
            Op("op", OperationStatus.Running),
            Op("op", OperationStatus.Completed));

        var operation = await _eryph.Waiter.WaitAsync("op", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Completed, operation.Status);
        await _eryph.Operations.Received(3).GetAsync(
            "op", Arg.Any<DateTimeOffset?>(), "logs,resources", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WaitAsync_returns_the_running_operation_when_the_budget_is_used_up()
    {
        _eryph.SetupOperation("op", Op("op", OperationStatus.Running));

        var operation = await _eryph.Waiter.WaitAsync("op", TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Running, operation.Status);
    }

    [Fact]
    public async Task WaitAsync_survives_transient_poll_failures()
    {
        var calls = 0;
        _eryph.Operations.GetAsync("op", Arg.Any<DateTimeOffset?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1
                ? throw new RequestFailedException(503, "unavailable")
                : Task.FromResult(Ok(Op("op", OperationStatus.Completed))));

        var operation = await _eryph.Waiter.WaitAsync("op", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        Assert.Equal(OperationStatus.Completed, operation.Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Summarize_keeps_the_latest_log_entries_in_order()
    {
        var start = DateTimeOffset.Parse("2026-01-01T10:00:00Z");
        var log = Enumerable.Range(0, 40)
            .Reverse()
            .Select(i => EryphComputeClientModelFactory.OperationLogEntry($"l{i}", "t", $"line {i}", start.AddSeconds(i)));

        var summary = OperationWaiter.Summarize(Op("op", OperationStatus.Running, log: log));

        Assert.Equal(25, summary.RecentLog.Count);
        Assert.EndsWith("line 15", summary.RecentLog[0]);
        Assert.EndsWith("line 39", summary.RecentLog[^1]);
        Assert.False(summary.Finished);
    }

    [Fact]
    public void EnsureSucceeded_reports_failed_operations_as_content_errors()
    {
        var ex = Assert.Throws<ToolException>(() => OperationWaiter.EnsureSucceeded(
            Op("op", OperationStatus.Failed, "gene not found"), "Creating the catlet"));

        Assert.Equal(ErrorCategory.Content, ex.Category);
        Assert.Contains("Creating the catlet failed: gene not found", ex.Message);
        Assert.DoesNotContain("Recent operation log", ex.Message);
    }

    [Fact]
    public void EnsureSucceeded_reports_running_operations_as_transient()
    {
        var ex = Assert.Throws<ToolException>(() => OperationWaiter.EnsureSucceeded(
            Op("op-1", OperationStatus.Running), "Creating the catlet"));

        Assert.Equal(ErrorCategory.Transient, ex.Category);
        Assert.Contains("op-1", ex.Message);
    }

    [Fact]
    public void CleanMessage_strips_stack_traces()
    {
        const string message = "Key could not be added.\n   at LanguageExt.Prelude.Foo()\n"
                               + "--- End of stack trace from previous location ---\n   at Bar()";

        Assert.Equal("Key could not be added.", OperationWaiter.CleanMessage(message));
    }
}
