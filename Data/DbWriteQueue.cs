using System.Threading.Channels;

namespace ChartAnalyzer.Data;

public sealed record DbWork(string Description, Func<ChartDbContext, CancellationToken, Task> Run);

/// <summary>
/// Database work handed off by other services and run in order by <see cref="DbWriter"/>.
/// Callers continue immediately, so a paused database (which takes up to a minute to wake) never delays them.
/// </summary>
public sealed class DbWriteQueue
{
    private readonly Channel<DbWork> _channel = Channel.CreateUnbounded<DbWork>();

    public ChannelReader<DbWork> Reader => _channel.Reader;

    public void Enqueue(string description, Func<ChartDbContext, CancellationToken, Task> work) =>
        _channel.Writer.TryWrite(new DbWork(description, work));
}
