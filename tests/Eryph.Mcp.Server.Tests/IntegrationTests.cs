using System.Text.Json;
using Eryph.Mcp.Testing;

namespace Eryph.Mcp.Server.Tests;

// Level 3: the server against a real eryph-zero on this host. These tests create
// and remove catlets, so they only run when explicitly enabled:
//   ERYPH_MCP_INTEGRATION=1   Linux catlets (a few minutes)
//   ERYPH_MCP_WINDOWS=1       additionally Windows catlets (slow)
public static class Integration
{
    public const string LinuxParent = "dbosoft/ubuntu-24.04/starter";
    public const string WindowsParent = "dbosoft/winsrv2022-standard/starter";

    public static void RequireEnabled(string variable = "ERYPH_MCP_INTEGRATION") =>
        Assert.SkipUnless(Environment.GetEnvironmentVariable(variable) == "1", $"Set {variable}=1 to run.");

    public static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..6]}";

    public static string Catlet(string name, string parent, string? fodder = null) =>
        $"name: {name}\nparent: {parent}\ncpu: 2\nmemory: 2048\n" + (fodder is null ? "" : $"fodder:\n{fodder}");

    public static async Task<string> DeployAndProvisionAsync(
        EryphMcpServer server,
        string config,
        int provisioningTimeoutSeconds,
        CancellationToken cancellation)
    {
        var deployed = await server.CallOkAsync("deploy_catlet", new { config, waitSeconds = 1800 }, cancellation);
        var catletId = deployed.GetProperty("catletId").GetString()!;
        await server.CallOkAsync("start_catlet", new { catletId }, cancellation);
        return catletId;
    }

    public static async Task RemoveAsync(EryphMcpServer server, string? catletId)
    {
        if (catletId is not null)
            await server.CallAsync("remove_catlet", new { catletId });
    }
}

public sealed class LinuxCatletFixture : IAsyncLifetime
{
    public EryphMcpServer Server { get; private set; } = null!;

    public string Name { get; } = Integration.UniqueName("mcp-it-linux");

    public string? CatletId { get; private set; }

    public JsonElement Provisioning { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("ERYPH_MCP_INTEGRATION") != "1")
            return;

        Server = await EryphMcpServer.StartAsync();
        CatletId = await Integration.DeployAndProvisionAsync(Server, Integration.Catlet(Name, Integration.LinuxParent, """
            - name: marker
              type: shellscript
              content: |
                #!/bin/bash
                echo fodder-ran > /tmp/mcp-it-marker
            """), 900, CancellationToken.None);
        Provisioning = await Server.CallOkAsync("wait_for_provisioning", new { catletId = CatletId, timeoutSeconds = 900 });
    }

    public async ValueTask DisposeAsync()
    {
        if (Server is null)
            return;

        await Integration.RemoveAsync(Server, CatletId);
        await Server.DisposeAsync();
    }
}

public class LinuxCatletTests(LinuxCatletFixture fixture) : IClassFixture<LinuxCatletFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonElement> ExecAsync(string command, int timeoutSeconds = 60)
    {
        Integration.RequireEnabled();
        return await fixture.Server.CallOkAsync("catlet_exec",
            new { catletId = fixture.CatletId, command, timeoutSeconds }, Ct);
    }

    [Fact]
    public async Task Provisioning_completes_and_the_fodder_ran()
    {
        Integration.RequireEnabled();
        Assert.Equal("completed", fixture.Provisioning.GetProperty("state").GetString());

        var result = await ExecAsync("cat /tmp/mcp-it-marker");

        Assert.Equal("fodder-ran\n", result.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Exec_returns_the_exit_code_and_merged_stderr()
    {
        var result = await ExecAsync("echo out; echo err >&2; exit 7");

        Assert.Equal(7, result.GetProperty("exitCode").GetInt32());
        Assert.Contains("out", result.GetProperty("output").GetString());
        Assert.Contains("err", result.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Exec_runs_stdin_reading_commands_with_redirected_stdin()
    {
        // guest services never close a command's stdin (also not for ssh exec),
        // so the redirect the tool description asks for is what keeps it working.
        var result = await ExecAsync("cat < /dev/null; echo done", timeoutSeconds: 20);

        Assert.False(result.GetProperty("timedOut").GetBoolean());
        Assert.Equal("done\n", result.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Exec_aborts_on_timeout_and_the_session_stays_usable()
    {
        var timedOut = await ExecAsync("sleep 60", timeoutSeconds: 2);
        var next = await ExecAsync("echo alive");

        Assert.True(timedOut.GetProperty("timedOut").GetBoolean());
        Assert.Equal("alive\n", next.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Exec_truncates_large_output_but_keeps_the_end()
    {
        var result = await ExecAsync("seq 1 200000");

        Assert.True(result.GetProperty("truncated").GetBoolean());
        Assert.EndsWith("200000\n", result.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Exec_preserves_utf8()
    {
        var result = await ExecAsync("echo 'äöü €'");

        Assert.Equal("äöü €\n", result.GetProperty("output").GetString());
    }

    [Fact]
    public async Task Get_catlet_reports_running_and_provisioned()
    {
        Integration.RequireEnabled();

        var catlet = await fixture.Server.CallOkAsync("get_catlet", new { catletId = fixture.CatletId }, Ct);

        Assert.Equal("Running", catlet.GetProperty("status").GetString());
        Assert.Equal(fixture.Name, catlet.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Deploying_the_same_name_again_is_a_state_error()
    {
        Integration.RequireEnabled();

        var result = await fixture.Server.CallAsync("deploy_catlet",
            new { config = Integration.Catlet(fixture.Name, Integration.LinuxParent) }, Ct);

        Assert.True(result.IsError);
        Assert.Contains("[state error]", EryphMcpServer.Text(result));
        Assert.Contains(fixture.CatletId!, EryphMcpServer.Text(result));
    }
}

public class ValidationIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Valid_config_is_expanded_with_the_resolved_parent()
    {
        Integration.RequireEnabled();
        await using var server = await EryphMcpServer.StartAsync(cancellation: Ct);

        var result = await server.CallOkAsync("validate_catlet",
            new { config = Integration.Catlet("x", Integration.LinuxParent) }, Ct);

        Assert.True(result.GetProperty("isValid").GetBoolean());
        Assert.Contains("parent: dbosoft/ubuntu-24.04/starter-", result.GetProperty("expandedConfig").GetString());
    }

    [Theory]
    [InlineData("name: x\nparent: dbosoft/does-not-exist/latest\n", "not available")]
    [InlineData("name: x\nparent: dbosoft/ubuntu-24.04/starter\nmemory: 1000\n", "multiple of 128")]
    [InlineData("name: x\nparent: dbosoft/ubuntu-24.04/starter\nfodder:\n- source: gene:dbosoft/starter-food/latest:nope\n", "nope")]
    public async Task Invalid_configs_are_reported_with_the_reason(string config, string reason)
    {
        Integration.RequireEnabled();
        await using var server = await EryphMcpServer.StartAsync(cancellation: Ct);

        var result = await server.CallOkAsync("validate_catlet", new { config }, Ct);

        Assert.False(result.GetProperty("isValid").GetBoolean());
        Assert.Contains(reason, result.GetProperty("errors").ToString());
    }
}

public class FailingFodderTests
{
    [Fact]
    public async Task A_failing_fodder_item_is_reported_as_failed_provisioning()
    {
        Integration.RequireEnabled();
        var ct = TestContext.Current.CancellationToken;
        await using var server = await EryphMcpServer.StartAsync(cancellation: ct);
        string? catletId = null;
        try
        {
            catletId = await Integration.DeployAndProvisionAsync(server, Integration.Catlet(
                Integration.UniqueName("mcp-it-fail"), Integration.LinuxParent, """
                - name: broken
                  type: shellscript
                  content: |
                    #!/bin/bash
                    echo about to fail
                    exit 3
                """), 900, ct);

            var provisioning = await server.CallOkAsync("wait_for_provisioning", new { catletId, timeoutSeconds = 900 }, ct);
            var log = await server.CallOkAsync("get_provisioning_log", new { catletId }, ct);

            Assert.Equal("failed", provisioning.GetProperty("state").GetString());
            Assert.Contains("fodder", provisioning.GetProperty("hint").GetString());
            Assert.False(string.IsNullOrEmpty(log.GetProperty("log").GetString()));
        }
        finally
        {
            await Integration.RemoveAsync(server, catletId);
        }
    }
}

public class WindowsCatletTests
{
    [Fact]
    public async Task Windows_catlet_provisions_and_runs_powershell_commands()
    {
        Integration.RequireEnabled();
        Integration.RequireEnabled("ERYPH_MCP_WINDOWS");
        var ct = TestContext.Current.CancellationToken;
        await using var server = await EryphMcpServer.StartAsync(cancellation: ct);
        string? catletId = null;
        try
        {
            catletId = await Integration.DeployAndProvisionAsync(server, Integration.Catlet(
                Integration.UniqueName("mcp-it-win"), Integration.WindowsParent, """
                - name: marker
                  type: shellscript
                  filename: marker.ps1
                  content: |
                    Set-Content -Path C:\mcp-it-marker.txt -Value fodder-ran
                """), 1800, ct);

            var provisioning = await server.CallOkAsync("wait_for_provisioning", new { catletId, timeoutSeconds = 1800 }, ct);
            Assert.Equal("completed", provisioning.GetProperty("state").GetString());

            var marker = await server.CallOkAsync("catlet_exec",
                new { catletId, command = "Get-Content C:\\mcp-it-marker.txt" }, ct);
            Assert.Equal("fodder-ran", marker.GetProperty("output").GetString()!.Trim());

            var exitCode = await server.CallOkAsync("catlet_exec", new { catletId, command = "exit 4" }, ct);
            Assert.Equal(4, exitCode.GetProperty("exitCode").GetInt32());
        }
        finally
        {
            await Integration.RemoveAsync(server, catletId);
        }
    }
}
