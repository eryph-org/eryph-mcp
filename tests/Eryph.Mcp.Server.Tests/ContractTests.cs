using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eryph.Mcp.Testing;
using ModelContextProtocol;

namespace Eryph.Mcp.Server.Tests;

// Level 2: the MCP contract as a client sees it. Needs no eryph installation.
public class ContractTests : IAsyncLifetime
{
    private EryphMcpServer _server = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _server = await EryphMcpServer.StartAsync(cancellation: Ct);

    public async ValueTask DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public void Server_identifies_itself_and_sends_the_workflow_instructions()
    {
        Assert.Equal("eryph", _server.Client.ServerInfo.Name);
        Assert.Contains("validate_catlet -> deploy_catlet", _server.Client.ServerInstructions);
        Assert.Contains("[content error]", _server.Client.ServerInstructions);
    }

    // The tool names, descriptions and schemas are the API the model sees. Any
    // change shows up as a snapshot diff and has to be accepted deliberately
    // (set UPDATE_SNAPSHOTS=1 and review the diff).
    [Fact]
    public async Task Tool_definitions_match_the_snapshot()
    {
        var tools = await _server.Client.ListToolsAsync(cancellationToken: Ct);
        var actual = JsonSerializer.Serialize(
            tools.Select(t => t.ProtocolTool).OrderBy(t => t.Name),
            new JsonSerializerOptions(McpJsonUtilities.DefaultOptions) { WriteIndented = true })
            .ReplaceLineEndings("\n");

        var snapshot = Path.Combine(SourceDirectory(), "Snapshots", "tools.json");
        var received = Path.ChangeExtension(snapshot, ".received.json");
        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
            await File.WriteAllTextAsync(snapshot, actual, Ct);

        var expected = File.Exists(snapshot) ? (await File.ReadAllTextAsync(snapshot, Ct)).ReplaceLineEndings("\n") : "";
        if (expected != actual)
        {
            await File.WriteAllTextAsync(received, actual, Ct);
            Assert.Fail($"Tool definitions differ from {snapshot}. Review {received} and rerun with UPDATE_SNAPSHOTS=1 to accept.");
        }

        File.Delete(received);
    }

    [Fact]
    public async Task Every_tool_and_parameter_is_described()
    {
        var tools = await _server.Client.ListToolsAsync(cancellationToken: Ct);

        Assert.All(tools, tool =>
        {
            Assert.True(tool.Description.Length >= 20, $"{tool.Name} needs a meaningful description");
            var properties = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!["properties"]!.AsObject();
            Assert.All(properties, p =>
                Assert.False(string.IsNullOrWhiteSpace(p.Value?["description"]?.GetValue<string>()),
                    $"{tool.Name}.{p.Key} has no description"));
        });
    }

    [Theory]
    [InlineData("validate_catlet", true, false)]
    [InlineData("get_catlet", true, false)]
    [InlineData("list_catlets", true, false)]
    [InlineData("get_operation", true, false)]
    [InlineData("wait_for_provisioning", true, false)]
    [InlineData("get_provisioning_log", true, false)]
    [InlineData("remove_catlet", false, true)]
    public async Task Tools_are_annotated(string name, bool readOnly, bool destructive)
    {
        var tools = await _server.Client.ListToolsAsync(cancellationToken: Ct);
        var annotations = tools.Single(t => t.Name == name).ProtocolTool.Annotations;

        Assert.Equal(readOnly, annotations?.ReadOnlyHint ?? false);
        Assert.Equal(destructive, annotations?.DestructiveHint ?? !readOnly);
    }

    [Fact]
    public async Task Invalid_yaml_is_reported_as_content_error()
    {
        var result = await _server.CallAsync("validate_catlet", new { config = "name: [unclosed" }, Ct);

        Assert.True(result.IsError);
        Assert.Contains("[content error]", EryphMcpServer.Text(result));
    }

    [Fact]
    public async Task A_missing_eryph_connection_is_reported_as_state_error()
    {
        await using var server = await EryphMcpServer.StartAsync(
            ["--eryph:configuration", "mcp-contract-test-missing"], Ct);

        var result = await server.CallAsync("list_catlets", cancellation: Ct);

        Assert.True(result.IsError);
        Assert.Contains("[state error] No eryph connection found", EryphMcpServer.Text(result));
    }

    private static string SourceDirectory([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;
}
