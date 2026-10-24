using System.Reflection;
using Eryph.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

var builder = Host.CreateApplicationBuilder(args);

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

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
        o.ServerInfo = new Implementation { Name = "eryph", Version = version };
        o.ServerInstructions = EryphMcpServerBuilderExtensions.Instructions;
    })
    .WithStdioServerTransport()
    .AddEryphTools(options);

await builder.Build().RunAsync();
