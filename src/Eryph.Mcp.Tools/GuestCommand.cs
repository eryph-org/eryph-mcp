using System.Text;
using Microsoft.DevTunnels.Ssh;
using Microsoft.DevTunnels.Ssh.Events;
using Microsoft.DevTunnels.Ssh.Messages;

namespace Eryph.Mcp.Tools;

// Runs one non-interactive command over an established guest session.
internal static class GuestCommand
{
    private const int MaxOutputBytes = 100_000;

    public static async Task<ExecResult> ExecAsync(
        SshClientSession session,
        string command,
        TimeSpan timeout,
        CancellationToken cancellation)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeoutCts.CancelAfter(timeout);

        var channel = await session.OpenChannelAsync(cancellation);
        var closed = new TaskCompletionSource<SshChannelClosedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.Closed += (_, e) => closed.TrySetResult(e);

        // Create the stream before the request so no output that arrives right
        // after the server accepted the command is missed.
        var stream = new SshStream(channel);
        var accepted = await channel.RequestAsync(new CommandRequestMessage(command), cancellation);
        if (!accepted)
        {
            await stream.DisposeAsync();
            return new ExecResult(null, "", false, false, "The guest rejected the command request.");
        }

        // No stdin: signal EOF so commands that read stdin do not wait forever.
        await channel.SendAsync(Microsoft.DevTunnels.Ssh.Buffer.Empty, cancellation);

        var output = new CappedOutput(MaxOutputBytes);
        var timedOut = false;
        try
        {
            await stream.CopyToAsync(output, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            timedOut = true;
        }
        catch (ObjectDisposedException)
        {
            // The server closes the channel when the command exits.
        }

        if (timedOut)
        {
            // Closing the channel makes the guest kill the process.
            await stream.DisposeAsync();
            return new ExecResult(null, output.GetText(), output.Truncated, true,
                $"The command did not finish within {timeout.TotalSeconds:0} seconds and was aborted.");
        }

        var closedEvent = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellation);
        await stream.DisposeAsync();

        string? error = null;
        if (closedEvent.ExitSignal is not null || closedEvent.ErrorMessage is not null)
            error = string.IsNullOrEmpty(closedEvent.ErrorMessage)
                ? $"The command was terminated with signal {closedEvent.ExitSignal}."
                : closedEvent.ErrorMessage;

        return new ExecResult(
            closedEvent.ExitStatus is { } status ? unchecked((int)status) : null,
            output.GetText(),
            output.Truncated,
            false,
            error);
    }
}

// Keeps the head and the tail of the output: the start usually shows what ran,
// the end shows why it failed.
internal sealed class CappedOutput(int limit) : Stream
{
    private readonly MemoryStream _head = new();
    private readonly Queue<byte> _tail = new();
    private readonly int _half = limit / 2;

    public bool Truncated { get; private set; }

    public string GetText()
    {
        var head = Encoding.UTF8.GetString(_head.GetBuffer(), 0, (int)_head.Length);
        return Truncated
            ? head + "\n... [output truncated] ...\n" + Encoding.UTF8.GetString(_tail.ToArray())
            : head;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var toHead = (int)Math.Min(buffer.Length, Math.Max(0, _half - _head.Length));
        _head.Write(buffer[..toHead]);
        foreach (var b in buffer[toHead..])
        {
            Truncated = true;
            _tail.Enqueue(b);
            if (_tail.Count > _half)
                _tail.Dequeue();
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _head.Length + _tail.Count;
    public override long Position { get => Length; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
