using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Eryph.Mcp.Testing;

// Starts eryph-mcp as a separate process and talks to it over stdio with the
// SDK's MCP client - the same way Claude or any other MCP host does. Shared by
// the server tests and the eval runner.
public sealed class EryphMcpServer : IAsyncDisposable
{
    private EryphMcpServer(McpClient client, List<string> stderr)
    {
        Client = client;
        StandardError = stderr;
    }

    public McpClient Client { get; }

    public IReadOnlyList<string> StandardError { get; }

    // ERYPH_MCP_SERVER overrides the executable, e.g. to test a published build.
    public static string ExecutablePath =>
        Environment.GetEnvironmentVariable("ERYPH_MCP_SERVER") is { Length: > 0 } path
            ? path
            : Path.Combine(RepositoryRoot, "src", "Eryph.Mcp.Server", "bin", BuildConfiguration, "net10.0", "eryph-mcp.exe");

    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Eryph.Mcp.slnx")))
                directory = directory.Parent;
            return directory?.FullName
                   ?? throw new InvalidOperationException("Could not find the repository root (Eryph.Mcp.slnx).");
        }
    }

    // The test assembly is built with the same configuration as the server.
    private static string BuildConfiguration =>
        new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    public static async Task<EryphMcpServer> StartAsync(
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellation = default)
    {
        var stderr = new List<string>();
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "eryph",
            Command = ExecutablePath,
            Arguments = arguments?.ToList() ?? [],
            // The SDK waits up to this long for the process to exit on dispose,
            // although the server exits within milliseconds once stdin closes.
            ShutdownTimeout = TimeSpan.FromSeconds(1),
            StandardErrorLines = line =>
            {
                lock (stderr)
                    stderr.Add(line);
            },
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellation);
        return new EryphMcpServer(client, stderr);
    }

    public async Task<CallToolResult> CallAsync(
        string tool,
        object? arguments = null,
        CancellationToken cancellation = default)
    {
        var args = arguments is null
            ? new Dictionary<string, object?>()
            : JsonSerializer.SerializeToElement(arguments).EnumerateObject()
                .ToDictionary(p => p.Name, p => (object?)p.Value);
        return await Client.CallToolAsync(tool, args, cancellationToken: cancellation);
    }

    // Calls a tool that is expected to succeed and returns its JSON result.
    public async Task<JsonElement> CallOkAsync(
        string tool,
        object? arguments = null,
        CancellationToken cancellation = default)
    {
        var result = await CallAsync(tool, arguments, cancellation);
        if (result.IsError == true)
        {
            var diagnostics = StandardError.Count > 0
                ? $"\nServer stderr:\n{string.Join("\n", StandardError.TakeLast(20))}"
                : "";
            throw new InvalidOperationException($"Tool '{tool}' failed: {Text(result)}{diagnostics}");
        }

        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    public static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    public async ValueTask DisposeAsync() => await Client.DisposeAsync();
}
