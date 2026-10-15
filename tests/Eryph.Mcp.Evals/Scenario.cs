using System.Text.Json;

namespace Eryph.Mcp.Evals;

public sealed record Scenario(
    string Id,
    string Description,
    string Prompt,
    int MaxTurns,
    decimal MaxBudgetUsd,
    int TimeoutMinutes,
    IReadOnlyList<Check> Checks,
    bool Slow = false)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<Scenario> LoadAll(string directory) =>
        Directory.GetFiles(directory, "*.json")
            .Select(f => JsonSerializer.Deserialize<Scenario>(File.ReadAllText(f), Options)
                         ?? throw new InvalidDataException($"Invalid scenario {f}"))
            .OrderBy(s => s.Id)
            .ToList();
}

// type: "provisioning" (expect = provisioning state) or "exec" (command is run in
// the catlet, expect is a regex the output has to match).
public sealed record Check(string Type, string? Name, string? Command, string Expect);
