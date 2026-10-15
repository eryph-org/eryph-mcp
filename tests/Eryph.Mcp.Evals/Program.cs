using System.Text;
using System.Text.Json;
using Eryph.Mcp.Evals;
using Eryph.Mcp.Testing;

// Usage: dotnet run --project tests/Eryph.Mcp.Evals -- [--models sonnet,haiku] [--scenarios a,b] [--repeat N] [--include-slow] [--keep]
var models = Option("--models")?.Split(',') ?? ["sonnet"];
var scenarioFilter = Option("--scenarios")?.Split(',');
var repeat = int.Parse(Option("--repeat") ?? "1");
var includeSlow = args.Contains("--include-slow");
var keep = args.Contains("--keep");

var scenarios = Scenario.LoadAll(Path.Combine(AppContext.BaseDirectory, "scenarios"))
    .Where(s => scenarioFilter?.Contains(s.Id) ?? (includeSlow || !s.Slow))
    .ToList();
if (scenarios.Count == 0)
{
    Console.Error.WriteLine("No scenarios selected.");
    return 1;
}

var outputDirectory = Path.Combine(EryphMcpServer.RepositoryRoot, "results", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(outputDirectory);
Console.WriteLine($"Server:  {EryphMcpServer.ExecutablePath}");
Console.WriteLine($"Results: {outputDirectory}");

await using var server = await EryphMcpServer.StartAsync();
var grader = new Grader(server);
var results = new List<EvalResult>();

foreach (var scenario in scenarios)
foreach (var model in models)
for (var run = 1; run <= repeat; run++)
{
    var catletName = $"eval-{scenario.Id}-{Guid.NewGuid().ToString("N")[..4]}";
    var prompt = scenario.Prompt.Replace("{{name}}", catletName);
    Console.WriteLine($"\n>>> {scenario.Id} | {model} | run {run} | catlet {catletName}");

    var before = (await grader.ListCatletsAsync(default)).Select(c => c.Id).ToHashSet();
    var agent = await ClaudeRunner.RunAsync(model, scenario, prompt, EryphMcpServer.ExecutablePath, outputDirectory, default);
    var checks = await grader.GradeAsync(scenario, catletName, default);

    // Only the run's own catlets (by its unique name prefix) are removed. Other
    // catlets that appeared meanwhile may belong to a parallel run or to the
    // user; they are reported as stray only when they do not look like another
    // eval run's catlet, and are never removed.
    var created = (await grader.ListCatletsAsync(default)).Where(c => !before.Contains(c.Id)).ToList();
    var own = created.Where(c => c.Name.StartsWith(catletName, StringComparison.OrdinalIgnoreCase)).ToList();
    var stray = created.Except(own).Where(c => !c.Name.StartsWith("eval-", StringComparison.OrdinalIgnoreCase)).ToList();
    if (!keep)
    {
        foreach (var catlet in own)
            await grader.RemoveAsync(catlet, default);
    }

    var result = new EvalResult(scenario.Id, model, run, checks.All(c => c.Passed), checks, agent,
        stray.Select(c => $"{c.Name} ({c.Id})").ToList());
    results.Add(result);
    PrintRun(result);
}

await File.WriteAllTextAsync(Path.Combine(outputDirectory, "results.json"),
    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
var summary = Summary(results);
await File.WriteAllTextAsync(Path.Combine(outputDirectory, "summary.md"), summary);
Console.WriteLine("\n" + summary);
return results.All(r => r.Passed) ? 0 : 2;

string? Option(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static void PrintRun(EvalResult r)
{
    Console.WriteLine($"    {(r.Passed ? "PASS" : "FAIL")}  turns={r.Agent.Turns} cost=${r.Agent.CostUsd:0.00} " +
                      $"time={r.Agent.Duration:mm\\:ss} toolErrors={r.Agent.ToolErrors} agent={r.Agent.Subtype}");
    foreach (var check in r.Checks)
        Console.WriteLine($"    {(check.Passed ? "ok  " : "FAIL")} {check.Name}: {check.Detail.ReplaceLineEndings(" ")}");
    foreach (var stray in r.StrayCatlets)
        Console.WriteLine($"    stray catlet: {stray}");
}

static string Summary(IReadOnlyList<EvalResult> results)
{
    var sb = new StringBuilder();
    sb.AppendLine("| Scenario | Model | Pass | Turns | Tool calls | Tool errors | Cost | Time |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|");
    foreach (var r in results)
    {
        sb.AppendLine($"| {r.Scenario} | {r.Model} | {(r.Passed ? "yes" : "no")} | {r.Agent.Turns} | " +
                      $"{r.Agent.ToolCalls.Values.Sum()} | {r.Agent.ToolErrors} | ${r.Agent.CostUsd:0.00} | {r.Agent.Duration:mm\\:ss} |");
    }

    sb.AppendLine();
    foreach (var r in results)
    {
        sb.AppendLine($"**{r.Scenario} / {r.Model}**: " +
                      string.Join(", ", r.Agent.ToolCalls.OrderByDescending(t => t.Value).Select(t => $"{t.Key}×{t.Value}")));
        foreach (var check in r.Checks.Where(c => !c.Passed))
            sb.AppendLine($"- failed check `{check.Name}`: {check.Detail.ReplaceLineEndings(" ")}");
        foreach (var error in r.Agent.ErrorSamples)
            sb.AppendLine($"- tool error: {error.ReplaceLineEndings(" ")}");
    }

    return sb.ToString();
}

public sealed record EvalResult(
    string Scenario,
    string Model,
    int Run,
    bool Passed,
    IReadOnlyList<CheckResult> Checks,
    AgentRun Agent,
    IReadOnlyList<string> StrayCatlets);
