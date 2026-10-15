using System.Reflection;
using System.Text.Json;
using Azure;
using Eryph.ComputeClient;
using Eryph.ComputeClient.Models;
using Eryph.GuestServices.Client;
using NSubstitute;
using Operation = Eryph.ComputeClient.Models.Operation;

namespace Eryph.Mcp.Tools.Tests;

// Substitutes for the eryph compute clients. The generated clients have virtual
// members and protected constructors, so NSubstitute can stand in for them.
public sealed class FakeEryph
{
    public CatletsClient Catlets { get; } = Substitute.For<CatletsClient>();

    public OperationsClient Operations { get; } = Substitute.For<OperationsClient>();

    public ProjectsClient Projects { get; } = Substitute.For<ProjectsClient>();

    public IEryphGuestServicesClient GuestServices { get; } = Substitute.For<IEryphGuestServicesClient>();

    public IEryphApi Api { get; }

    public EryphMcpOptions Options { get; } = new()
    {
        OperationPollInterval = TimeSpan.FromMilliseconds(1),
        ProvisioningPollInterval = TimeSpan.FromMilliseconds(1),
        GuestConnectRetryDelay = TimeSpan.FromMilliseconds(1),
    };

    public FakeEryph()
    {
        Api = Substitute.For<IEryphApi>();
        Api.Catlets(Arg.Any<string?>()).Returns(Catlets);
        Api.Operations().Returns(Operations);
        Api.Projects().Returns(Projects);
        Api.GuestServices().Returns(GuestServices);

        Projects.List(Arg.Any<CancellationToken>())
            .Returns(_ => Pageable([EryphComputeClientModelFactory.Project("p-default", "default", "tenant")]));
        Catlets.List(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Pageable<Catlet>([]));
    }

    public OperationWaiter Waiter => new(Api, Options);

    // Makes an operation id resolve to the given sequence of polled states; the
    // last state repeats.
    public void SetupOperation(string id, params Operation[] states)
    {
        var index = 0;
        Operations.GetAsync(id, Arg.Any<DateTimeOffset?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Ok(states[Math.Min(index++, states.Length - 1)])));
    }

    public static Response<T> Ok<T>(T value) => Response.FromValue(value, Substitute.For<Response>());

    public static Task<Response<Operation>> Started(string id) =>
        Task.FromResult(Ok(Op(id, OperationStatus.Queued)));

    public static Operation Op(
        string id,
        OperationStatus status,
        string? message = null,
        OperationResult? result = null,
        IEnumerable<OperationResource>? resources = null,
        IEnumerable<OperationLogEntry>? log = null) =>
        EryphComputeClientModelFactory.Operation(
            id: id,
            status: status,
            statusMessage: message,
            resources: resources ?? [],
            logEntries: log ?? [],
            projects: [],
            tasks: [],
            result: result);

    public static Catlet Catlet(string id, string name, CatletStatus status) =>
        EryphComputeClientModelFactory.Catlet(
            id: id,
            name: name,
            vmId: "vm-" + id,
            project: EryphComputeClientModelFactory.Project("p-default", "default", "tenant"),
            status: status,
            provisioningStatus: CatletProvisioningStatus.Unknown);

    // CatletConfigOperationResult has no model factory method; its constructor is internal.
    public static OperationResult ConfigResult(string json) =>
        (OperationResult)typeof(CatletConfigOperationResult)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(string), typeof(JsonElement)])!
            .Invoke(["CatletConfig", JsonDocument.Parse(json).RootElement.Clone()]);

    public static Pageable<T> Pageable<T>(IReadOnlyList<T> items) where T : notnull =>
        Azure.Pageable<T>.FromPages([Page<T>.FromValues(items, null, Substitute.For<Response>())]);
}
