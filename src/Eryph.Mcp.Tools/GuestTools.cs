using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Eryph.Mcp.Tools;

[McpServerToolType]
public sealed class GuestTools(IGuestSessions sessions)
{
    [McpServerTool(Name = "catlet_exec")]
    [Description("""
        Runs a non-interactive command in a catlet guest through eryph guest services (no SSH
        setup, no admin rights, works off-host). stdout and stderr are merged into one output.
        The command runs in the guest's configured shell: on Windows guests this is PowerShell
        by default (note: 'sc' and 'type' are PowerShell aliases there - use 'sc.exe' or
        'Get-Content'), on Linux guests the default shell running as root.
        Not suitable for interactive programs: there is no TTY and no stdin input. A command that
        waits for stdin blocks until the timeout, so redirect it (Linux: '< /dev/null') and use
        non-interactive flags (e.g. apt-get -y, DEBIAN_FRONTEND=noninteractive). Interactive work
        that needs a TTY belongs to real SSH ('ssh <catlet-id>.eryph.alt' after
        'egs-tool catlet add-ssh-config <catlet-id> --add-key'), not to this tool.
        Requires a running catlet with guest services - call wait_for_provisioning first.
        """)]
    public Task<ExecResult> CatletExec(
        [Description("The catlet id.")] string catletId,
        [Description("The command line to run.")] string command,
        [Description("Seconds before the command is aborted (default 300).")] int timeoutSeconds = 300,
        CancellationToken cancellation = default) =>
        ToolErrors.Guard(() => sessions.ExecAsync(
            catletId, command, TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 1)), cancellation));
}
