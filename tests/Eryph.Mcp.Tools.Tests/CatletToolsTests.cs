using Eryph.ComputeClient.Models;
using NSubstitute;
using static Eryph.Mcp.Tools.Tests.FakeEryph;

namespace Eryph.Mcp.Tools.Tests;

public class CatletToolsTests
{
    private const string MinimalYaml = "name: t1\nparent: dbosoft/ubuntu-24.04/starter\n";

    private readonly FakeEryph _eryph = new();
    private readonly CatletTools _tools;

    public CatletToolsTests()
    {
        _tools = new CatletTools(_eryph.Api, _eryph.Waiter, new CatletConfigs(_eryph.Api, _eryph.Waiter));

        _eryph.Catlets.PopulateConfigVariablesAsync(Arg.Any<PopulateCatletConfigVariablesRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Started("populate"));
        _eryph.SetupOperation("populate", Op("populate", OperationStatus.Completed,
            result: ConfigResult("""{"name":"t1","variables":[{"name":"user","value":"default-user"}]}""")));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Validate_returns_the_server_side_validation_errors()
    {
        _eryph.Catlets.ValidateConfigAsync(Arg.Any<ValidateConfigRequest>(), Arg.Any<CancellationToken>())
            .Returns(Ok(EryphComputeClientModelFactory.CatletConfigValidationResult(false,
            [
                EryphComputeClientModelFactory.ValidationIssue("$.memory.startup", "must be a multiple of 128"),
                EryphComputeClientModelFactory.ValidationIssue(null, "general problem"),
            ])));

        var result = await _tools.ValidateCatlet(MinimalYaml, cancellation: Ct);

        Assert.False(result.IsValid);
        Assert.Equal(["$.memory.startup: must be a multiple of 128", "general problem"], result.Errors);
        await _eryph.Catlets.DidNotReceive().ExpandNewConfigAsync(
            Arg.Any<ExpandNewCatletConfigRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validate_reports_unresolvable_genes_as_invalid_instead_of_failing()
    {
        _eryph.Catlets.ValidateConfigAsync(Arg.Any<ValidateConfigRequest>(), Arg.Any<CancellationToken>())
            .Returns(Ok(EryphComputeClientModelFactory.CatletConfigValidationResult(true, [])));
        _eryph.SetupOperation("populate", Op("populate", OperationStatus.Failed, "gene set not available"));

        var result = await _tools.ValidateCatlet(MinimalYaml, cancellation: Ct);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("gene set not available"));
    }

    [Fact]
    public async Task Validate_returns_the_expanded_configuration_as_yaml()
    {
        _eryph.Catlets.ValidateConfigAsync(Arg.Any<ValidateConfigRequest>(), Arg.Any<CancellationToken>())
            .Returns(Ok(EryphComputeClientModelFactory.CatletConfigValidationResult(true, [])));
        _eryph.Catlets.ExpandNewConfigAsync(Arg.Any<ExpandNewCatletConfigRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Started("expand"));
        _eryph.SetupOperation("expand", Op("expand", OperationStatus.Completed,
            result: ConfigResult("""{"name":"t1","parent":"dbosoft/ubuntu-24.04/starter-1.0"}""")));

        var result = await _tools.ValidateCatlet(MinimalYaml, new() { ["unknownVar"] = "x" }, cancellation: Ct);

        Assert.True(result.IsValid);
        Assert.Contains("parent: dbosoft/ubuntu-24.04/starter-1.0", result.ExpandedConfig);
        Assert.DoesNotContain("\r", result.ExpandedConfig);
        Assert.Equal(["unknownVar"], result.UnknownVariables);
    }

    [Fact]
    public async Task Validate_gives_a_configuration_without_name_the_default_name()
    {
        _eryph.Catlets.ValidateConfigAsync(Arg.Any<ValidateConfigRequest>(), Arg.Any<CancellationToken>())
            .Returns(Ok(EryphComputeClientModelFactory.CatletConfigValidationResult(true, [])));
        _eryph.Catlets.ExpandNewConfigAsync(Arg.Any<ExpandNewCatletConfigRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Started("expand"));
        _eryph.SetupOperation("expand", Op("expand", OperationStatus.Completed,
            result: ConfigResult("""{"name":"catlet"}""")));

        await _tools.ValidateCatlet("parent: dbosoft/ubuntu-24.04/starter\n", cancellation: Ct);

        await _eryph.Catlets.Received().ValidateConfigAsync(
            Arg.Is<ValidateConfigRequest>(r => r.Configuration.GetProperty("name").GetString() == "catlet"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validate_reports_unparseable_yaml_as_content_error()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            _tools.ValidateCatlet("name: [unclosed", cancellation: Ct));

        Assert.Equal(ErrorCategory.Content, ex.Category);
    }

    [Fact]
    public async Task Deploy_rejects_a_duplicate_name_before_creating_anything()
    {
        _eryph.Catlets.List(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Pageable([Catlet("c-existing", "T1", CatletStatus.Running)]));

        var ex = await Assert.ThrowsAsync<ToolException>(() => _tools.DeployCatlet(MinimalYaml, cancellation: Ct));

        Assert.Equal(ErrorCategory.State, ex.Category);
        Assert.Contains("c-existing", ex.Message);
        await _eryph.Catlets.DidNotReceive().CreateAsync(Arg.Any<NewCatletRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deploy_returns_the_catlet_id_and_applies_variables()
    {
        NewCatletRequest? request = null;
        _eryph.Catlets.CreateAsync(Arg.Do<NewCatletRequest>(r => request = r), Arg.Any<CancellationToken>())
            .Returns(_ => Started("create"));
        _eryph.SetupOperation("create", Op("create", OperationStatus.Completed,
            resources: [EryphComputeClientModelFactory.OperationResource("r1", "c-1", ResourceType.Catlet)]));

        var result = await _tools.DeployCatlet(MinimalYaml, new() { ["USER"] = "alice" }, cancellation: Ct);

        Assert.Equal("c-1", result.CatletId);
        Assert.Equal("created", result.Status);
        Assert.Empty(result.UnknownVariables);
        Assert.Contains("\"alice\"", request!.Configuration.GetRawText());
    }

    [Fact]
    public async Task Deploy_hands_back_the_operation_when_creation_takes_longer_than_the_wait()
    {
        _eryph.Catlets.CreateAsync(Arg.Any<NewCatletRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Started("create"));
        _eryph.SetupOperation("create", Op("create", OperationStatus.Running));

        var result = await _tools.DeployCatlet(MinimalYaml, waitSeconds: 0, cancellation: Ct);

        Assert.Equal("running", result.Status);
        Assert.Null(result.CatletId);
        Assert.Equal("create", result.Operation.OperationId);
        Assert.Contains("get_operation", result.NextStep);
    }

    [Fact]
    public async Task Deploy_reports_a_failed_creation_as_content_error()
    {
        _eryph.Catlets.CreateAsync(Arg.Any<NewCatletRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Started("create"));
        _eryph.SetupOperation("create", Op("create", OperationStatus.Failed, "invalid fodder"));

        var ex = await Assert.ThrowsAsync<ToolException>(() => _tools.DeployCatlet(MinimalYaml, cancellation: Ct));

        Assert.Equal(ErrorCategory.Content, ex.Category);
        Assert.Contains("invalid fodder", ex.Message);
    }

    [Fact]
    public async Task Start_reports_a_failed_operation_as_state_error()
    {
        _eryph.Catlets.StartAsync("c-1", Arg.Any<CancellationToken>()).Returns(_ => Started("start"));
        _eryph.SetupOperation("start", Op("start", OperationStatus.Failed, "already running"));

        var ex = await Assert.ThrowsAsync<ToolException>(() => _tools.StartCatlet("c-1", Ct));

        Assert.Equal(ErrorCategory.State, ex.Category);
    }

    [Fact]
    public async Task List_filters_by_project()
    {
        _eryph.Catlets.List("p-default", Arg.Any<CancellationToken>())
            .Returns(_ => Pageable([Catlet("c-1", "one", CatletStatus.Running)]));

        var catlets = await _tools.ListCatlets("DEFAULT", Ct);

        var catlet = Assert.Single(catlets);
        Assert.Equal("vm-c-1", catlet.VmId);
    }

    [Fact]
    public async Task List_reports_an_unknown_project_as_state_error()
    {
        var ex = await Assert.ThrowsAsync<ToolException>(() => _tools.ListCatlets("nope", Ct));

        Assert.Equal(ErrorCategory.State, ex.Category);
    }
}
