using System.Collections.Concurrent;
using System.Xml;
using Eryph.ComputeClient.Models;
using Eryph.GuestServices.Client;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.DevTunnels.Ssh.Keys;

namespace Eryph.Mcp.Tools;

public sealed record ExecResult(
    int? ExitCode,
    string Output,
    bool Truncated,
    bool TimedOut,
    string? Error);

public interface IGuestSessions
{
    Task<ExecResult> ExecAsync(string catletId, string command, TimeSpan timeout, CancellationToken cancellation);
}

// Keeps one authenticated guest session per catlet over the eryph remote channel.
// The MCP server uses its own ephemeral key pair: it is generated per process,
// pushed to a catlet with a TTL on first use and never written to disk.
public sealed class GuestSessions(IEryphApi api, OperationWaiter waiter, EryphMcpOptions options)
    : IGuestSessions, IAsyncDisposable
{
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(8);

    private readonly IKeyPair _keyPair = SshAlgorithms.PublicKey.ECDsaSha2Nistp256.GenerateKeyPair();
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ExecResult> ExecAsync(
        string catletId,
        string command,
        TimeSpan timeout,
        CancellationToken cancellation)
    {
        var session = await GetSessionAsync(catletId, cancellation);
        try
        {
            return await GuestCommand.ExecAsync(session, command, timeout, cancellation);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshConnectionException)
        {
            // The cached session died (e.g. the catlet was restarted); reconnect once.
            await DropAsync(catletId);
            session = await GetSessionAsync(catletId, cancellation);
            return await GuestCommand.ExecAsync(session, command, timeout, cancellation);
        }
    }

    internal async Task<SshClientSession> GetSessionAsync(string catletId, CancellationToken cancellation)
    {
        var entry = _entries.GetOrAdd(catletId, _ => new Entry());
        await entry.Lock.WaitAsync(cancellation);
        try
        {
            if (entry.Connection is { Session.IsClosed: false } connection)
                return connection.Session;

            if (entry.Connection is not null)
                await entry.Connection.DisposeAsync();
            entry.Connection = null;

            if (!entry.KeyAuthorized)
            {
                await AuthorizeKeyAsync(catletId, cancellation);
                entry.KeyAuthorized = true;
            }

            entry.Connection = await ConnectAsync(catletId, cancellation);
            return entry.Connection.Session;
        }
        finally
        {
            entry.Lock.Release();
        }
    }

    private async Task DropAsync(string catletId)
    {
        if (_entries.TryRemove(catletId, out var entry) && entry.Connection is not null)
            await entry.Connection.DisposeAsync();
    }

    private async Task AuthorizeKeyAsync(string catletId, CancellationToken cancellation)
    {
        var publicKey = KeyPair.ExportPublicKey(_keyPair, KeyFormat.Ssh).Trim();
        var started = await api.Catlets(EryphConnection.RemoteAccessScope).AddAccessKeyAsync(
            catletId,
            new AddAccessKeyRequestBody(publicKey)
            {
                Ttl = XmlConvert.ToString(KeyTtl),
                ExpiresAt = DateTimeOffset.UtcNow + KeyTtl,
            },
            cancellation);

        // The key is delivered to the guest asynchronously; connecting before the
        // operation finished races the guest picking it up.
        var operation = await waiter.WaitAsync(started.Value.Id, TimeSpan.FromMinutes(1), cancellation);
        if (operation.Status != OperationStatus.Completed)
            throw new ToolException(
                $"Could not authorize the MCP server's key in the catlet: {OperationWaiter.CleanMessage(operation.StatusMessage) ?? operation.Status.ToString()}. "
                + "Guest services must be running in the catlet (see wait_for_provisioning).",
                ErrorCategory.State);
    }

    private async Task<GuestSshConnection> ConnectAsync(string catletId, CancellationToken cancellation)
    {
        var client = api.GuestServices();
        try
        {
            return await client.ConnectAsync(catletId, _keyPair, cancellation: cancellation);
        }
        catch (GuestConnectionException)
        {
            // One retry: the guest may need a moment to activate a freshly pushed key.
            await Task.Delay(options.GuestConnectRetryDelay, cancellation);
            return await client.ConnectAsync(catletId, _keyPair, cancellation: cancellation);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Connection is not null)
                await entry.Connection.DisposeAsync();
        }

        _entries.Clear();
    }

    private sealed class Entry
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public GuestSshConnection? Connection { get; set; }

        public bool KeyAuthorized { get; set; }
    }
}
