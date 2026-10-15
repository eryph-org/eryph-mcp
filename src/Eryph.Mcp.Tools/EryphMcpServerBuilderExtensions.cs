using Microsoft.Extensions.DependencyInjection;

namespace Eryph.Mcp.Tools;

public static class EryphMcpServerBuilderExtensions
{
    // Guidance sent to the model at initialization. It carries the process rules
    // the eryph-genes expert agents enforced, so a plain model follows them too.
    public const string Instructions = """
        Tools to create, run and inspect eryph catlets (Hyper-V VMs built from genes and fodder).

        Workflow: validate_catlet -> deploy_catlet -> start_catlet -> wait_for_provisioning -> catlet_exec.
        - Always address catlets by catlet id; names are not unique.
        - Long operations return an operation id when not finished in time; continue with get_operation.
        - Errors are prefixed with their category: [content error] means the catlet/fodder definition must
          change; [state error] means the system state must change (catlet stopped, already exists, not found);
          [transient error] can be retried.
        - Develop fodder incrementally: start with a minimal catlet (parent only), verify it, then add one fodder
          item at a time and verify after each. Fodder runs as one chain; a failing item blocks all later ones.
        - If wait_for_provisioning never completes or guest services never report, an earlier fodder item failed.
          Read get_provisioning_log and simplify the fodder - do not debug networking or try SSH via IP.
        - Use {{ variable }} syntax for variables in catlet YAML; variables are a list of objects with name/value.
        - Fodder genes are referenced as gene:org/geneset[/tag]:fodder-name.
        - Inline fodder items have name, type (cloud-config or shellscript) and content. On Windows give a
          shellscript item a filename (e.g. filename: setup.ps1 - the key is filename, not file_name); the
          extension selects the interpreter.
        - Windows catlets are provisioned by eryph guest services, Linux catlets by cloud-init. Windows accepts
          full cloud-config but ignores Linux-only keys such as packages; install software with scripts there.
          A failing Windows script does not stop later scripts - check the provisioning log.
        """;

    public static IMcpServerBuilder AddEryphTools(this IMcpServerBuilder builder, EryphMcpOptions? options = null)
    {
        builder.Services.AddSingleton(options ?? new EryphMcpOptions());
        builder.Services.AddSingleton<IEryphApi, EryphApi>();
        builder.Services.AddSingleton<OperationWaiter>();
        builder.Services.AddSingleton<CatletConfigs>();
        builder.Services.AddSingleton<IGuestSessions, GuestSessions>();

        return builder
            .WithTools<CatletTools>()
            .WithTools<ProvisioningTools>()
            .WithTools<GuestTools>();
    }
}
