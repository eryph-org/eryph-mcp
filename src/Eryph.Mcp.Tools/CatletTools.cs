using System.ComponentModel;
using Eryph.ComputeClient.Models;
using ModelContextProtocol.Server;

namespace Eryph.Mcp.Tools;

public sealed record ValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> UnknownVariables,
    string? ExpandedConfig);

public sealed record CatletInfo(
    string CatletId,
    string Name,
    string Project,
    string Status,
    string ProvisioningStatus,
    string? VmId);

public sealed record DeployResult(
    string? CatletId,
    string? Name,
    string Status,
    OperationSummary Operation,
    IReadOnlyList<string> UnknownVariables,
    string? NextStep);

[McpServerToolType]
public sealed class CatletTools(IEryphApi api, OperationWaiter waiter, CatletConfigs configs)
{
    private static readonly TimeSpan ExpandBudget = TimeSpan.FromMinutes(5);

    [McpServerTool(Name = "validate_catlet", ReadOnly = true, Idempotent = true)]
    [Description("""
        Validates a catlet configuration (YAML or JSON) without creating anything.
        Runs the schema/semantic validation and then expands the configuration: parents are
        resolved, inherited fodder is merged and referenced genes are looked up (genes may be
        downloaded from the genepool, which can take a while on first use).
        Returns the errors or the fully expanded configuration. Always validate before deploy_catlet.
        """)]
    public Task<ValidationResult> ValidateCatlet(
        [Description("The catlet configuration as YAML (or JSON).")] string config,
        [Description("Optional variable values by name.")] Dictionary<string, string>? variables = null,
        [Description("Overrides the catlet name from the configuration, as deploy_catlet does.")] string? name = null,
        CancellationToken cancellation = default) =>
        ToolErrors.Guard(async () =>
        {
            var catletConfig = CatletConfigs.Parse(config);
            if (!string.IsNullOrWhiteSpace(name))
                catletConfig.Name = name;
            CatletConfigs.EnsureName(catletConfig);

            var validation = (await api.Catlets().ValidateConfigAsync(
                new ValidateConfigRequest(CatletConfigs.ToJson(catletConfig)), cancellation)).Value;
            if (!validation.IsValid)
            {
                return new ValidationResult(
                    false,
                    validation.Errors.Select(e => string.IsNullOrEmpty(e.Member) ? e.Message : $"{e.Member}: {e.Message}")
                        .ToList(),
                    [],
                    null);
            }

            IReadOnlyList<string> unknown;
            try
            {
                unknown = await configs.ApplyVariablesAsync(catletConfig, variables, cancellation);
            }
            catch (ToolException ex) when (ex.Category == ErrorCategory.Content)
            {
                // Resolving the variables already resolves the parents and genes,
                // so e.g. an unknown gene surfaces here.
                return new ValidationResult(false, [ex.Message], [], null);
            }

            var started = await api.Catlets().ExpandNewConfigAsync(
                new ExpandNewCatletConfigRequest(CatletConfigs.ToJson(catletConfig))
                {
                    CorrelationId = Guid.NewGuid(),
                    ShowSecrets = false,
                },
                cancellation);
            var operation = await waiter.WaitAsync(started.Value.Id, ExpandBudget, cancellation);
            if (operation.Status != OperationStatus.Completed)
            {
                var summary = OperationWaiter.Summarize(operation);
                return new ValidationResult(
                    false,
                    [OperationWaiter.CleanMessage(operation.StatusMessage) ?? $"Expanding the configuration ended with status {operation.Status}.",
                     .. summary.RecentLog],
                    unknown,
                    null);
            }

            var expanded = operation.Result is CatletConfigOperationResult result
                ? CatletConfigs.ToYaml(result.Configuration)
                : null;
            return new ValidationResult(true, [], unknown, expanded);
        });

    [McpServerTool(Name = "deploy_catlet", Destructive = false)]
    [Description("""
        Creates a new catlet (VM) from a catlet configuration. The catlet is created stopped;
        call start_catlet afterwards. Creation can take several minutes when genes have to be
        downloaded. If it is not finished within waitSeconds, the result contains the operation
        id and status 'running' - continue with get_operation.
        Catlet names must be unique within a project. Develop fodder incrementally: deploy with
        a minimal configuration first, verify it, then add one fodder item at a time.
        """)]
    public Task<DeployResult> DeployCatlet(
        [Description("The catlet configuration as YAML (or JSON).")] string config,
        [Description("Optional variable values by name.")] Dictionary<string, string>? variables = null,
        [Description("Overrides the catlet name from the configuration.")] string? name = null,
        [Description("Overrides the project from the configuration (default: 'default').")] string? project = null,
        [Description("Seconds to wait for the creation to finish (default 600).")] int waitSeconds = 600,
        CancellationToken cancellation = default) =>
        ToolErrors.Guard(async () =>
        {
            var catletConfig = CatletConfigs.Parse(config);
            if (!string.IsNullOrWhiteSpace(name))
                catletConfig.Name = name;
            if (!string.IsNullOrWhiteSpace(project))
                catletConfig.Project = project;

            var catletName = CatletConfigs.EnsureName(catletConfig);
            var projectName = string.IsNullOrWhiteSpace(catletConfig.Project) ? "default" : catletConfig.Project;
            await EnsureNameIsFreeAsync(catletName, projectName, cancellation);

            var unknown = await configs.ApplyVariablesAsync(catletConfig, variables, cancellation);

            var started = await api.Catlets().CreateAsync(
                new NewCatletRequest(CatletConfigs.ToJson(catletConfig)) { CorrelationId = Guid.NewGuid() },
                cancellation);
            var operation = await waiter.WaitAsync(
                started.Value.Id, TimeSpan.FromSeconds(Math.Max(waitSeconds, 1)), cancellation);
            var summary = OperationWaiter.Summarize(operation);

            if (!OperationWaiter.IsTerminal(operation))
                return new DeployResult(null, catletName, "running", summary, unknown,
                    $"Call get_operation with operationId '{operation.Id}' to continue waiting.");

            OperationWaiter.EnsureSucceeded(operation, "Creating the catlet");
            var catletId = summary.Resources.FirstOrDefault(r => r.Type == ResourceType.Catlet.ToString())?.Id;
            return new DeployResult(catletId, catletName, "created", summary, unknown,
                "Call start_catlet, then wait_for_provisioning.");
        });

    [McpServerTool(Name = "get_operation", ReadOnly = true)]
    [Description("Gets (and optionally waits for) an eryph operation, e.g. one returned by deploy_catlet.")]
    public Task<OperationSummary> GetOperation(
        [Description("The operation id.")] string operationId,
        [Description("Seconds to wait for the operation to finish (0 = return immediately).")] int waitSeconds = 0,
        CancellationToken cancellation = default) =>
        ToolErrors.Guard(async () => OperationWaiter.Summarize(
            await waiter.WaitAsync(operationId, TimeSpan.FromSeconds(Math.Max(waitSeconds, 0)), cancellation)));

    [McpServerTool(Name = "list_catlets", ReadOnly = true, Idempotent = true)]
    [Description("Lists catlets, optionally filtered by project name. Catlet names are not unique; use the catlet id.")]
    public Task<IReadOnlyList<CatletInfo>> ListCatlets(
        [Description("Optional project name.")] string? project = null,
        CancellationToken cancellation = default) =>
        ToolErrors.Guard(async () =>
        {
            var projectId = project is null ? null : await GetProjectIdAsync(project, cancellation);
            IReadOnlyList<CatletInfo> catlets = api.Catlets().List(projectId, cancellation).Select(ToInfo).ToList();
            return catlets;
        });

    [McpServerTool(Name = "get_catlet", ReadOnly = true, Idempotent = true)]
    [Description("Gets a catlet by id, including its status and provisioning status.")]
    public Task<CatletInfo> GetCatlet(
        [Description("The catlet id.")] string catletId,
        CancellationToken cancellation = default) =>
        ToolErrors.Guard(async () => ToInfo((await api.Catlets().GetAsync(catletId, cancellation)).Value));

    [McpServerTool(Name = "start_catlet")]
    [Description("Starts a catlet. Afterwards use wait_for_provisioning before running commands in it.")]
    public Task<OperationSummary> StartCatlet(
        [Description("The catlet id.")] string catletId,
        CancellationToken cancellation = default) =>
        RunCatletOperation(catletId, "Starting the catlet",
            c => api.Catlets().StartAsync(c, cancellation), cancellation);

    [McpServerTool(Name = "stop_catlet")]
    [Description("Stops a catlet. Use 'shutdown' for a graceful stop; 'hard' or 'kill' when the guest does not react.")]
    public Task<OperationSummary> StopCatlet(
        [Description("The catlet id.")] string catletId,
        [Description("'shutdown' (graceful, default), 'hard' (power off) or 'kill'.")] string mode = "shutdown",
        CancellationToken cancellation = default) =>
        RunCatletOperation(catletId, "Stopping the catlet",
            c => api.Catlets().StopAsync(c, new StopCatletRequestBody(new CatletStopMode(mode)), cancellation),
            cancellation);

    [McpServerTool(Name = "remove_catlet", Destructive = true)]
    [Description("Deletes a catlet including its disks. This cannot be undone.")]
    public Task<OperationSummary> RemoveCatlet(
        [Description("The catlet id.")] string catletId,
        CancellationToken cancellation = default) =>
        RunCatletOperation(catletId, "Removing the catlet",
            c => api.Catlets().DeleteAsync(c, cancellation), cancellation);

    private Task<OperationSummary> RunCatletOperation(
        string catletId,
        string what,
        Func<string, Task<Azure.Response<Operation>>> start,
        CancellationToken cancellation) =>
        ToolErrors.Guard(async () =>
        {
            var started = await start(catletId);
            var operation = await waiter.WaitAsync(started.Value.Id, TimeSpan.FromMinutes(5), cancellation);
            if (operation.Status == OperationStatus.Failed)
                throw new ToolException($"{what} failed: {OperationWaiter.CleanMessage(operation.StatusMessage)}", ErrorCategory.State);
            return OperationWaiter.Summarize(operation);
        });

    private async Task EnsureNameIsFreeAsync(string catletName, string projectName, CancellationToken cancellation)
    {
        var projectId = await GetProjectIdAsync(projectName, cancellation);
        var existing = api.Catlets().List(projectId, cancellation)
            .FirstOrDefault(c => string.Equals(c.Name, catletName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            throw new ToolException(
                $"A catlet named '{catletName}' already exists in project '{projectName}' (catlet id {existing.Id}). "
                + "Remove it, or deploy with a different name.",
                ErrorCategory.State);
    }

    private Task<string> GetProjectIdAsync(string projectName, CancellationToken cancellation)
    {
        var project = api.Projects().List(cancellation)
            .FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ToolException($"Project '{projectName}' not found.", ErrorCategory.State);
        return Task.FromResult(project.Id);
    }

    private static CatletInfo ToInfo(Catlet catlet) => new(
        catlet.Id,
        catlet.Name,
        catlet.Project?.Name ?? "",
        catlet.Status.ToString(),
        catlet.ProvisioningStatus.ToString(),
        catlet.VmId);
}
