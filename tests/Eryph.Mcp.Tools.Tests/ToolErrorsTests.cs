using Azure;
using Eryph.GuestServices.Client;

namespace Eryph.Mcp.Tools.Tests;

public class ToolErrorsTests
{
    [Theory]
    [InlineData(400, ErrorCategory.Content)]
    [InlineData(422, ErrorCategory.Content)]
    [InlineData(401, ErrorCategory.State)]
    [InlineData(404, ErrorCategory.State)]
    [InlineData(409, ErrorCategory.State)]
    [InlineData(500, ErrorCategory.Transient)]
    [InlineData(503, ErrorCategory.Transient)]
    public async Task Guard_maps_http_status_to_category(int status, ErrorCategory expected)
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            ToolErrors.Guard<int>(() => throw new RequestFailedException(status, "boom")));

        Assert.Equal(expected, ex.Category);
        Assert.StartsWith($"[{expected.ToString().ToLowerInvariant()} error]", ex.Message);
    }

    [Fact]
    public async Task Guard_reports_guest_connection_failures_as_state_errors_with_a_hint()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            ToolErrors.Guard<int>(() => throw new GuestConnectionException("auth failed")));

        Assert.Equal(ErrorCategory.State, ex.Category);
        Assert.Contains("auth failed", ex.Message);
        Assert.Contains("wait_for_provisioning", ex.Message);
    }

    [Fact]
    public async Task Guard_reports_unreachable_api_as_transient()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            ToolErrors.Guard<int>(() => throw new HttpRequestException("refused")));

        Assert.Equal(ErrorCategory.Transient, ex.Category);
    }

    [Fact]
    public async Task Guard_passes_tool_exceptions_through()
    {
        var original = new ToolException("x", ErrorCategory.Content);

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            ToolErrors.Guard<int>(() => throw original));

        Assert.Same(original, ex);
    }

    [Fact]
    public async Task Guard_keeps_only_the_first_line_of_http_error_messages()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            ToolErrors.Guard<int>(() => throw new RequestFailedException(409, "Name taken\n\nStatus: 409\nHeaders: x")));

        Assert.EndsWith("Conflict (HTTP 409): Name taken", ex.Message);
    }
}
