using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Eryph.Mcp.Evals;

public sealed record AgentRun(
    bool Completed,
    string? Subtype,
    int Turns,
    decimal CostUsd,
    TimeSpan Duration,
    IReadOnlyDictionary<string, int> ToolCalls,
    int ToolErrors,
    IReadOnlyList<string> ErrorSamples,
    string? FinalMessage,
    string TranscriptPath);

// Runs Claude Code headless with only the eryph MCP server available: no built-in
// tools, no other MCP servers, an empty working directory (no CLAUDE.md or repo
// context) and no user/project settings. Each run is a fresh process with the
// current server build, so nothing has to be restarted between runs.
public static class ClaudeRunner
{
    private const string McpServerName = "eryph";

    public static async Task<AgentRun> RunAsync(
        string model,
        Scenario scenario,
        string prompt,
        string serverExecutable,
        string outputDirectory,
        CancellationToken cancellation)
    {
        var workDirectory = Directory.CreateTempSubdirectory("eryph-mcp-eval-").FullName;
        var mcpConfig = Path.Combine(workDirectory, "mcp.json");
        await File.WriteAllTextAsync(mcpConfig, new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                [McpServerName] = new JsonObject { ["command"] = serverExecutable, ["args"] = new JsonArray() },
            },
        }.ToJsonString(), cancellation);

        var transcriptPath = Path.Combine(outputDirectory, $"{scenario.Id}.{model}.jsonl");
        var start = new ProcessStartInfo("claude")
        {
            WorkingDirectory = workDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
                 {
                     "-p", prompt,
                     "--model", model,
                     "--mcp-config", mcpConfig,
                     "--strict-mcp-config",
                     "--tools", "",
                     "--allowedTools", $"mcp__{McpServerName}",
                     "--permission-mode", "dontAsk",
                     "--setting-sources", "project",
                     "--max-turns", scenario.MaxTurns.ToString(),
                     "--max-budget-usd", scenario.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture),
                     "--no-session-persistence",
                     "--output-format", "stream-json",
                     "--verbose",
                 })
        {
            start.ArgumentList.Add(argument);
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(scenario.TimeoutMinutes));

        var toolCalls = new Dictionary<string, int>();
        var toolErrors = 0;
        var errorSamples = new List<string>();
        JsonNode? result = null;

        await using (var transcript = File.CreateText(transcriptPath))
        {
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    await transcript.WriteLineAsync(line);
                    var message = TryParse(line);
                    switch (message?["type"]?.GetValue<string>())
                    {
                        case "assistant":
                            foreach (var block in Blocks(message, "tool_use"))
                            {
                                var name = block["name"]!.GetValue<string>().Replace($"mcp__{McpServerName}__", "");
                                toolCalls[name] = toolCalls.GetValueOrDefault(name) + 1;
                            }
                            break;
                        case "user":
                            foreach (var block in Blocks(message, "tool_result")
                                         .Where(b => b["is_error"]?.GetValue<bool>() == true))
                            {
                                toolErrors++;
                                if (errorSamples.Count < 5)
                                    errorSamples.Add(Truncate(block["content"]?.ToJsonString() ?? "", 300));
                            }
                            break;
                        case "result":
                            result = message;
                            break;
                    }
                }

                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
            }

            if (result is null)
                await transcript.WriteLineAsync(JsonSerializer.Serialize(new { type = "runner", stderr = await SafeAsync(stderrTask) }));
        }

        Directory.Delete(workDirectory, recursive: true);

        return new AgentRun(
            Completed: result?["subtype"]?.GetValue<string>() == "success",
            Subtype: result?["subtype"]?.GetValue<string>() ?? "timeout",
            Turns: result?["num_turns"]?.GetValue<int>() ?? 0,
            CostUsd: result?["total_cost_usd"]?.GetValue<decimal>() ?? 0,
            Duration: stopwatch.Elapsed,
            ToolCalls: toolCalls,
            ToolErrors: toolErrors,
            ErrorSamples: errorSamples,
            FinalMessage: result?["result"]?.GetValue<string>(),
            TranscriptPath: transcriptPath);
    }

    private static IEnumerable<JsonNode> Blocks(JsonNode message, string type) =>
        message["message"]?["content"] is JsonArray content
            ? content.OfType<JsonNode>().Where(b => b["type"]?.GetValue<string>() == type)
            : [];

    private static JsonNode? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string> SafeAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch (OperationCanceledException)
        {
            return "";
        }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
