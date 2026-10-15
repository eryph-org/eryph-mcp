using System.Text.Json;
using System.Text.RegularExpressions;
using Eryph.Mcp.Testing;

namespace Eryph.Mcp.Evals;

public sealed record CheckResult(string Name, bool Passed, string Detail);

public sealed record CatletRef(string Id, string Name);

// Grades the outcome deterministically from the catlet state, independent of what
// the model claims. Uses the eryph MCP server itself as the probe.
public sealed class Grader(EryphMcpServer server)
{
    public async Task<IReadOnlyList<CatletRef>> ListCatletsAsync(CancellationToken cancellation)
    {
        var catlets = await server.CallOkAsync("list_catlets", cancellation: cancellation);
        return catlets.EnumerateArray()
            .Select(c => new CatletRef(c.GetProperty("catletId").GetString()!, c.GetProperty("name").GetString()!))
            .ToList();
    }

    public async Task<IReadOnlyList<CheckResult>> GradeAsync(
        Scenario scenario,
        string catletName,
        CancellationToken cancellation)
    {
        var catlet = (await ListCatletsAsync(cancellation))
            .FirstOrDefault(c => string.Equals(c.Name, catletName, StringComparison.OrdinalIgnoreCase));
        if (catlet is null)
            return [new CheckResult("catlet exists", false, $"No catlet named '{catletName}'.")];

        var results = new List<CheckResult> { new("catlet exists", true, catlet.Id) };
        foreach (var check in scenario.Checks)
            results.Add(await RunCheckAsync(catlet.Id, check, cancellation));

        return results;
    }

    private async Task<CheckResult> RunCheckAsync(string catletId, Check check, CancellationToken cancellation)
    {
        var name = check.Name ?? check.Type;
        try
        {
            switch (check.Type)
            {
                case "provisioning":
                    var state = (await server.CallOkAsync("wait_for_provisioning",
                        new { catletId, timeoutSeconds = 60 }, cancellation)).GetProperty("state").GetString();
                    return new CheckResult(name, state == check.Expect, $"state={state}");

                case "exec":
                    var result = await server.CallOkAsync("catlet_exec",
                        new { catletId, command = check.Command, timeoutSeconds = 60 }, cancellation);
                    var output = result.GetProperty("output").GetString() ?? "";
                    var passed = Regex.IsMatch(output.Trim(), check.Expect, RegexOptions.Multiline);
                    return new CheckResult(name, passed, Truncate(output.Trim(), 200));

                default:
                    return new CheckResult(name, false, $"Unknown check type '{check.Type}'.");
            }
        }
        catch (Exception ex)
        {
            return new CheckResult(name, false, Truncate(ex.Message, 300));
        }
    }

    public async Task RemoveAsync(CatletRef catlet, CancellationToken cancellation) =>
        await server.CallAsync("remove_catlet", new { catletId = catlet.Id }, cancellation);

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length] + "...";
}
