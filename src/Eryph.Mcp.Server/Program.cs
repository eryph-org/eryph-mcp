using Eryph.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol; every log line has to go to stderr.
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var options = new EryphMcpOptions
{
    ClientId = builder.Configuration["eryph:clientId"],
    Configuration = builder.Configuration["eryph:configuration"],
};

builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "eryph", Version = "0.1.0" };
        o.ServerInstructions = EryphMcpServerBuilderExtensions.Instructions;
    })
    .WithStdioServerTransport()
    .AddEryphTools(options);

await builder.Build().RunAsync();
