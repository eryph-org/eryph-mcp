using Eryph.ComputeClient;
using Eryph.GuestServices.Client;

namespace Eryph.Mcp.Tools;

public sealed class EryphMcpOptions
{
    // Optional explicit identity. When both are null the connection is resolved
    // like the eryph CLI: the default client of the default configuration,
    // falling back to the local system client (elevated callers only).
    public string? ClientId { get; set; }

    public string? Configuration { get; set; }

    public TimeSpan OperationPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ProvisioningPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan GuestConnectRetryDelay { get; set; } = TimeSpan.FromSeconds(3);
}

// The seam between the tools and eryph. The generated compute clients have
// virtual members and protected constructors, so tests substitute them directly.
public interface IEryphApi
{
    CatletsClient Catlets(string? scope = null);

    OperationsClient Operations();

    ProjectsClient Projects();

    IEryphGuestServicesClient GuestServices();
}

// Lazily resolves the eryph connection from the user's client configuration and
// hands out compute API clients for it. The resolution is retried on the next
// call when it failed, so starting eryph-zero after the MCP server works.
public sealed class EryphApi(EryphMcpOptions options) : IEryphApi
{
    private readonly Lock _lock = new();
    private EryphConnection? _connection;

    private EryphConnection Connection
    {
        get
        {
            lock (_lock)
            {
                return _connection ??= EryphConnection.Resolve(options.ClientId, options.Configuration)
                    ?? throw new ToolException(
                        "No eryph connection found. Is eryph-zero running and is an eryph client configured "
                        + "for this user? (Without a configured client the local system client is used, "
                        + "which requires an elevated process.)",
                        ErrorCategory.State);
            }
        }
    }

    private ComputeClientsFactory Factory =>
        new(new EryphComputeClientOptions(Connection.Credentials), Connection.ComputeEndpoint);

    public CatletsClient Catlets(string? scope = null) => Factory.CreateCatletsClient(scope);

    public OperationsClient Operations() => Factory.CreateOperationsClient();

    public ProjectsClient Projects() => Factory.CreateProjectsClient();

    public IEryphGuestServicesClient GuestServices() => new EryphGuestServicesClient(Connection);
}
