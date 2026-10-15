using System.Text;
using Eryph.ComputeClient.Models;
using Eryph.GuestServices.Client;
using Microsoft.DevTunnels.Ssh.Algorithms;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using static Eryph.Mcp.Tools.Tests.FakeEryph;

namespace Eryph.Mcp.Tools.Tests;

public class GuestSessionsTests
{
    private readonly FakeEryph _eryph = new();
    private readonly GuestSessions _sessions;

    public GuestSessionsTests()
    {
        _sessions = new GuestSessions(_eryph.Api, _eryph.Waiter, _eryph.Options);
        _eryph.Catlets.AddAccessKeyAsync("c-1", Arg.Any<AddAccessKeyRequestBody>(), Arg.Any<CancellationToken>())
            .Returns(_ => Started("key"));
        _eryph.SetupOperation("key", Op("key", OperationStatus.Completed));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Exec_fails_with_a_state_error_when_the_key_cannot_be_authorized()
    {
        _eryph.SetupOperation("key", Op("key", OperationStatus.Failed, "guest services not running"));

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            _sessions.ExecAsync("c-1", "id", TimeSpan.FromSeconds(5), Ct));

        Assert.Equal(ErrorCategory.State, ex.Category);
        Assert.Contains("guest services not running", ex.Message);
        await _eryph.GuestServices.DidNotReceive().ConnectAsync(
            Arg.Any<string>(), Arg.Any<IKeyPair>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exec_pushes_an_ssh_public_key_with_a_ttl()
    {
        AddAccessKeyRequestBody? body = null;
        _eryph.Catlets.AddAccessKeyAsync("c-1", Arg.Do<AddAccessKeyRequestBody>(b => body = b), Arg.Any<CancellationToken>())
            .Returns(_ => Started("key"));
        _eryph.GuestServices.ConnectAsync("c-1", Arg.Any<IKeyPair>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GuestConnectionException("no"));

        await Assert.ThrowsAsync<GuestConnectionException>(() =>
            _sessions.ExecAsync("c-1", "id", TimeSpan.FromSeconds(5), Ct));

        Assert.StartsWith("ecdsa-sha2-nistp256 ", body!.PublicKey);
        Assert.Equal("PT8H", body.Ttl);
        _eryph.Api.Received().Catlets(EryphConnection.RemoteAccessScope);
    }

    [Fact]
    public async Task Exec_retries_the_connect_once_and_authorizes_the_key_only_once()
    {
        _eryph.GuestServices.ConnectAsync("c-1", Arg.Any<IKeyPair>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GuestConnectionException("auth failed"));

        await Assert.ThrowsAsync<GuestConnectionException>(() => _sessions.ExecAsync("c-1", "id", TimeSpan.FromSeconds(5), Ct));
        await Assert.ThrowsAsync<GuestConnectionException>(() => _sessions.ExecAsync("c-1", "id", TimeSpan.FromSeconds(5), Ct));

        await _eryph.GuestServices.Received(4).ConnectAsync(
            "c-1", Arg.Any<IKeyPair>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>());
        await _eryph.Catlets.Received(1).AddAccessKeyAsync(
            "c-1", Arg.Any<AddAccessKeyRequestBody>(), Arg.Any<CancellationToken>());
    }
}

public class CappedOutputTests
{
    [Fact]
    public void Keeps_short_output_unchanged()
    {
        var output = new CappedOutput(100);
        output.Write(Encoding.UTF8.GetBytes("hello äöü\n"));

        Assert.Equal("hello äöü\n", output.GetText());
        Assert.False(output.Truncated);
    }

    [Fact]
    public void Keeps_head_and_tail_of_long_output()
    {
        var output = new CappedOutput(20);
        output.Write(Encoding.UTF8.GetBytes("0123456789"));
        output.Write(Encoding.UTF8.GetBytes("abcdefghijklmnopqrstuvwxyz"));

        Assert.True(output.Truncated);
        Assert.Equal("0123456789\n... [output truncated] ...\nqrstuvwxyz", output.GetText());
    }
}

public class GuestToolsTests
{
    [Fact]
    public async Task Exec_maps_guest_connection_failures_to_state_errors()
    {
        var sessions = Substitute.For<IGuestSessions>();
        sessions.ExecAsync("c-1", "id", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new GuestConnectionException("channel closed"));

        var ex = await Assert.ThrowsAsync<ToolException>(() =>
            new GuestTools(sessions).CatletExec("c-1", "id", cancellation: TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCategory.State, ex.Category);
    }

    [Fact]
    public async Task Exec_clamps_the_timeout_to_at_least_one_second()
    {
        var sessions = Substitute.For<IGuestSessions>();
        sessions.ExecAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ExecResult(0, "", false, false, null));

        await new GuestTools(sessions).CatletExec("c-1", "id", timeoutSeconds: -5, cancellation: TestContext.Current.CancellationToken);

        await sessions.Received().ExecAsync("c-1", "id", TimeSpan.FromSeconds(1), Arg.Any<CancellationToken>());
    }
}
