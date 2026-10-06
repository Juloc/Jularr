using System.Threading.Channels;

namespace Jularr.Web.Infrastructure;

/// <summary>
/// Wakes a hosted worker whose work list is durable, for example after a user action queued something. What to do is always read
/// from the database, so a lost or repeated signal does no harm: repeated wakes collapse into one, and a worker that is not waiting
/// sees the wake on its next wait. Each worker derives its own signal type so it can be registered and injected on its own.
/// </summary>
public abstract class BackgroundWakeSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Wake() => channel.Writer.TryWrite(true);

    /// <summary>Waits until <see cref="Wake"/> is called or <paramref name="timeout"/> has passed.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await channel.Reader.WaitToReadAsync(timer.Token);
            channel.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }
}
