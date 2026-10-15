using System.Text.Json;
using Eryph.ComputeClient.Models;
using Eryph.ConfigModel.Catlets;
using Eryph.ConfigModel.Json;
using Eryph.ConfigModel.Yaml;

namespace Eryph.Mcp.Tools;

// Parses catlet configurations and resolves their variables the way New-Catlet
// does, minus the interactive prompt (which is what made the PowerShell route
// fail with a NullReferenceException when -SkipVariablesPrompt was forgotten).
public sealed class CatletConfigs(IEryphApi api, OperationWaiter waiter)
{
    private static readonly TimeSpan PopulateBudget = TimeSpan.FromMinutes(2);

    public static CatletConfig Parse(string config)
    {
        var text = config.Trim().Replace("\r\n", "\n");
        try
        {
            return text.StartsWith('{') && text.EndsWith('}')
                ? CatletConfigJsonSerializer.Deserialize(text)
                : CatletConfigYamlSerializer.Deserialize(text);
        }
        catch (Exception ex)
        {
            throw new ToolException(
                $"The catlet configuration could not be parsed: {ex.Message}", ErrorCategory.Content);
        }
    }

    public static JsonElement ToJson(CatletConfig config) => CatletConfigJsonSerializer.SerializeToElement(config);

    public static string ToYaml(JsonElement configuration) =>
        CatletConfigYamlSerializer.Serialize(CatletConfigJsonSerializer.Deserialize(configuration))
            .Replace("\r\n", "\n");

    // Fetches the variables declared by the configuration and its parents, then
    // applies the given values. Unknown variable names are reported back instead
    // of being silently ignored.
    public async Task<IReadOnlyList<string>> ApplyVariablesAsync(
        CatletConfig config,
        IReadOnlyDictionary<string, string>? values,
        CancellationToken cancellation)
    {
        var started = await api.Catlets().PopulateConfigVariablesAsync(
            new PopulateCatletConfigVariablesRequest(ToJson(config)) { CorrelationId = Guid.NewGuid() },
            cancellation);
        var operation = await waiter.WaitAsync(started.Value.Id, PopulateBudget, cancellation);
        OperationWaiter.EnsureSucceeded(operation, "Resolving the catlet variables");

        if (operation.Result is CatletConfigOperationResult result)
            config.Variables = CatletConfigJsonSerializer.Deserialize(result.Configuration).Variables;

        if (values is not { Count: > 0 })
            return [];

        var declared = config.Variables ?? [];
        var unknown = new List<string>();
        foreach (var (name, value) in values)
        {
            var variable = declared.FirstOrDefault(
                v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (variable is null)
                unknown.Add(name);
            else
                variable.Value = value;
        }

        return unknown;
    }
}
